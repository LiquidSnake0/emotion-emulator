#!/usr/bin/env python3
"""Six pistes, six niveaux, un seul son — le stem player du bac.

L'IDÉE EST DU DJ, ET ELLE VIENT DE L'APPAREIL DE KANYE WEST.

> « Tu vois le stem player de Kanye West ? »

Quatre stems, un fader par stem, on monte, on coupe, on isole pendant que ça tourne. Et le
point qu'il fallait comprendre : **cet appareil ne sépare rien en temps réel.** Il a les
stems, et il ne fait que les mélanger. C'est ce qui rend la chose simple ici aussi — les six
pistes sont extraites une fois (`stems.py`), et ce fichier ne fait plus que doser.

La différence avec l'appareil : ses quatre stems sont nommés et fiables. Nos six sources ne
sont ni l'un ni l'autre, et c'est précisément ce qu'on cherche à vérifier à l'oreille.

PAS DE DÉPENDANCE AUDIO NOUVELLE, ET CE N'ÉTAIT PAS ÉVIDENT.

`pacat` lit du brut sur son entrée standard et régule le débit lui-même — mesuré : une
seconde de son écrite en 1,06 s. On calcule donc le mélange en numpy et on le lui pousse.
Ajouter PortAudio ou PyAudio au projet pour faire la même chose aurait été une dépendance de
plus pour rien, et ce dépôt se tient à trois.

LA POSITION VIENT DE L'HORLOGE, PAS DE CE QU'ON A POUSSÉ DANS LE TUYAU.

Première version : on comptait les échantillons écrits vers `pacat`. C'était faux, et la
mesure l'a montré tout de suite — le lecteur avançait **par à-coups de 1,36 seconde puis
stagnait**, pour une moyenne juste. `pacat` ne respecte pas les quarante millisecondes qu'on
lui demande : il avale de gros paquets quand son tampon se vide, puis bloque. La position
d'écriture court donc devant le son d'un tampon qui varie de zéro à une seconde et demie.

Confronté au moteur, cela donnait des écarts de 500 à 1800 ms qui n'étaient **pas** de la
dérive : c'était l'avance du tampon, mesurée comme si c'était un décalage. Le lecteur se
serait recalé sans arrêt pour corriger un défaut qui n'existait pas.

La position se calcule donc **depuis l'horloge** : elle dit ce que l'oreille entend
maintenant. L'écriture, elle, court devant d'une AVANCE fixe — c'est le tampon, et il est
choisi, pas subi.

C'est exactement la leçon que ce projet a déjà payée ailleurs : « ce qui est mesuré doit
décrire ce qui est affiché ». Ici, ce qui est mesuré doit décrire ce qui est ENTENDU.
"""

import subprocess
import threading
import time
import wave

import numpy as np

BLOC = 1024                  # 21 ms a 48 kHz : la fenetre d'analyse du moteur
LATENCE_MS = 40              # ce qu'on demande a pacat ; il fait ce qu'il veut

# CE QU'ON GARDE D'AVANCE DANS LE TUYAU. Trop peu, la carte son se retrouve a sec et
# craquelle ; trop, un coup de fader met une demi-seconde a s'entendre. Cent cinquante
# millisecondes tiennent les deux bouts — c'est aussi l'ordre de grandeur du geste humain.
AVANCE_S = 0.150

# COMBIEN DE CHEMIN LE GAIN FAIT PAR BLOC, vers la valeur demandee. Un tiers par bloc de
# vingt et une millisecondes : le fader suit la main en une soixantaine de millisecondes,
# sans jamais sauter.
GLISSE = 0.34

# Au-dela de quoi on saute plutot que de laisser courir. LARGE, ET C'EST DELIBERE : la chaine
# audio a un retard constant — mesure entre 47 et 66 ms sur cette machine — qui n'est pas une
# derive et qu'un seuil serre ferait « corriger » vingt fois par minute pour rien. On mesure
# donc ce retard une fois (voir `caler`), on le retranche, et ce seuil-ci ne sert plus qu'a
# rattraper un vrai decrochage : le moteur qui bloque, un morceau qui reboucle.
DERIVE_MAX_S = 0.150

# Combien de mesures servent a estimer le retard constant avant de le corriger. Huit demi-
# secondes : assez pour une mediane qui ne soit pas un accident, assez peu pour que la
# correction tombe avant qu'on ait fini de regarder la premiere source.
CALAGES_ESTIMATION = 8


class Lecteur:
    """Six pistes en mémoire, un mélange poussé vers la carte son."""

    def __init__(self, chemins, dire=print):
        self.dire = dire
        self.pistes = []
        taux = None
        for c in chemins:
            with wave.open(c) as f:
                taux = taux or f.getframerate()
                # ON GARDE EN ENTIERS SEIZE BITS. En flottants, six pistes de cinq minutes
                # pesent trois cent quarante mega-octets pour rien : la conversion par bloc
                # coute quelques microsecondes et divise la memoire par deux.
                self.pistes.append(np.frombuffer(f.readframes(f.getnframes()), "<i2"))
        self.taux = taux or 48_000
        self.n = max((len(p) for p in self.pistes), default=0)
        self.duree = self.n / self.taux

        # DEUX JEUX DE GAINS, ET C'EST INDISPENSABLE.
        #
        # `gains` est ce qu'on DEMANDE, `_gains` est ce qui SORT. Les faire coïncider
        # instantanément produisait un saut dans la forme d'onde a chaque clic : un gain qui
        # passe de zero a un entre deux echantillons est une discontinuite, et une
        # discontinuite s'entend comme un claquement. Le DJ l'a decrit comme « une distorsion
        # quand je reclique sur une source ».
        #
        # On glisse donc de l'un vers l'autre en quelques millisecondes. C'est ce que fait
        # n'importe quelle table de mixage, et pour la meme raison.
        self.gains = np.ones(len(self.pistes), np.float32)
        self._gains = np.ones(len(self.pistes), np.float32)
        self._origine = 0.0               # position du morceau, en secondes, au temps _t0
        self._t0 = 0.0                    # instant monotone correspondant
        self._ecrit = 0                   # echantillons deja pousses, en absolu
        # LE NUMERO DU SAUT, ET IL EST INDISPENSABLE.
        #
        # Le fil de lecture prend la position, calcule son bloc, puis ecrit la position
        # suivante. Si un calage tombe entre les deux, le fil ECRASE le saut avec une valeur
        # calculee avant lui — et le saut est annule a chaque fois. Mesure : l'ecart au
        # moteur restait bloque a +513 ms, soit exactement l'intervalle entre deux calages.
        #
        # C'est la meme course que celle de l'anneau, et elle se corrige de la meme facon :
        # on verifie apres coup que personne n'a bouge, et l'on jette son bloc si quelqu'un
        # l'a fait. « Une course ne se corrige pas quand on la voit, elle se corrige quand
        # on l'ecrit » — celle-ci a ete ecrite et vue le meme jour, ce qui est un progres.
        self._saut = 0
        # LE RETARD DE LA CHAINE AUDIO, MESURE ET NON SUPPOSE.
        #
        # Entre l'instant qu'on croit jouer et celui qui sort du haut-parleur il y a le
        # tampon de `pacat`, celui du serveur de son et celui de la carte. Ce retard est
        # CONSTANT : mesure sur douze secondes, il vaut 47 a 66 ms et ne derive pas. Le
        # traiter comme une derive ferait sauter le lecteur en permanence pour corriger
        # quelque chose qui n'a pas bouge — exactement ce que faisait la version d'avant.
        #
        # On l'estime donc sur les premiers calages, on le retranche une fois, et l'on n'en
        # reparle plus. C'est la meme discipline que pour la latence de la main : elle se
        # mesure, elle ne se devine pas.
        self._retards = []
        self._retard = 0.0
        self.retard_mesure = False
        # LE PREMIER CALAGE RECALE, IL NE MESURE PAS.
        #
        # Un lecteur neuf part d'une position qui a vieilli : charger six pistes en memoire
        # prend une bonne seconde, et le moteur a avance pendant ce temps. Mesure : le
        # « retard de la chaine audio » sortait a 1617 ms, c'est-a-dire le temps de
        # chargement pris pour de la latence — et fige comme telle pour la soiree.
        self._neuf = True
        self._verrou = threading.Lock()
        self._fil = None
        self._vivant = False
        self._pacat = None

    # ------------------------------------------------------------------ marche
    def demarrer(self, position_s=0.0):
        if self._vivant:
            return
        self._pacat = subprocess.Popen(
            ["pacat", "--format=s16le", f"--rate={self.taux}", "--channels=1",
             f"--latency-msec={LATENCE_MS}", "--stream-name=emotion stems"],
            stdin=subprocess.PIPE)
        self._origine = max(0.0, position_s)
        self._t0 = time.monotonic()
        self._neuf = True
        self._ecrit = int(self._origine * self.taux)
        self._saut += 1
        self._vivant = True
        self._fil = threading.Thread(target=self._tourner, daemon=True)
        self._fil.start()

    def arreter(self):
        self._vivant = False
        if self._fil:
            self._fil.join(timeout=1.0)
        if self._pacat:
            try:
                self._pacat.stdin.close()
            except (OSError, ValueError):
                pass
            self._pacat.terminate()
            self._pacat = None

    def _tourner(self):
        while self._vivant:
            with self._verrou:
                debut = self._ecrit
                saut = self._saut
                vises = self.gains.copy()
                retard = self._cible() - debut

            # ON N'ECRIT QUE CE QUE L'HORLOGE RECLAME. Sans cette attente, on remplirait le
            # tampon de `pacat` aussi vite qu'il l'accepte — c'est exactement ce que faisait
            # la premiere version, et c'est ce qui rendait la position inutilisable.
            if retard < BLOC:
                time.sleep(BLOC / self.taux / 4)
                continue

            n = max(1, self.n)
            i = debut % n
            melange = np.zeros(BLOC, np.float32)

            # LA RAMPE COUVRE LE BLOC ENTIER. Vingt et une millisecondes de glissement : assez
            # pour qu'aucune discontinuite ne subsiste, assez peu pour que le geste reste vif.
            depart = self._gains.copy()
            self._gains = depart + (vises - depart) * GLISSE
            rampe = np.linspace(0.0, 1.0, BLOC, dtype=np.float32)

            for k, piste in enumerate(self.pistes):
                a, b = float(depart[k]), float(self._gains[k])
                if max(a, b) <= 0.0004 or i >= len(piste):
                    continue
                bout = piste[i:min(i + BLOC, len(piste))]
                g = a + (b - a) * rampe[:len(bout)]
                melange[:len(bout)] += g * bout

            # LA SOMME DES SIX EST LE MORCEAU, mesure entre 64 et 97 dB : a gains pleins on
            # retrouve donc l'original, sans marge. On ecrete plutot que de laisser tourner
            # un entier — mais on ne normalise pas, sans quoi le niveau bougerait a chaque
            # fois qu'on touche un fader et l'on ne saurait plus ce qu'on entend.
            np.clip(melange, -32768, 32767, out=melange)
            try:
                self._pacat.stdin.write(melange.astype("<i2").tobytes())
            except (BrokenPipeError, ValueError, AttributeError):
                self._vivant = False
                break

            with self._verrou:
                # Un saut est tombe pendant qu'on calculait : ce bloc appartient au passe,
                # et avancer le compteur d'ecriture le ferait repartir du mauvais endroit.
                if self._saut == saut:
                    self._ecrit = debut + BLOC

    def _cible(self):
        """Jusqu'ou l'on devrait avoir ecrit, tampon compris. A appeler sous verrou."""
        ecoule = time.monotonic() - self._t0
        return int((self._origine + ecoule + AVANCE_S) * self.taux)

    # ------------------------------------------------------------------ pilotage
    def gain(self, rang, valeur):
        if 0 <= rang < len(self.gains):
            with self._verrou:
                self.gains[rang] = max(0.0, min(1.0, float(valeur)))

    def tous(self, valeur):
        with self._verrou:
            self.gains[:] = max(0.0, min(1.0, float(valeur)))

    def solo(self, rang):
        """Une source a plein, les autres a zero. Rang nul : tout revient."""
        with self._verrou:
            if rang is None:
                self.gains[:] = 1.0
            else:
                self.gains[:] = 0.0
                if 0 <= rang < len(self.gains):
                    self.gains[rang] = 1.0

    def position(self):
        """Ce que l'oreille entend MAINTENANT, en secondes depuis le debut du morceau.

        Elle vient de l'horloge et non du compteur d'ecriture : celui-ci court devant d'un
        tampon variable, et le prendre pour la position donnait des ecarts de 500 a 1800 ms
        qui n'etaient que du tampon.
        """
        with self._verrou:
            t = self._origine + (time.monotonic() - self._t0)
        return t % max(1e-9, self.duree)

    def caler(self, temps_moteur):
        """Rattrape le moteur, mais seulement si l'écart s'entend.

        Rend l'écart mesuré, pour qu'on puisse l'afficher : un lecteur qui se recale sans le
        dire cacherait exactement le défaut qu'on veut voir.
        """
        with self._verrou:
            vise = temps_moteur % max(1e-9, self.duree)
            ici = (self._origine + (time.monotonic() - self._t0)) % max(1e-9, self.duree)
            ecart = vise - ici - self._retard
            # L'ECART SE PREND PAR LE PLUS COURT CHEMIN. Sur un morceau qui boucle, etre en
            # retard d'une seconde et en avance de quatre minutes cinq est la meme chose ;
            # sans ce repli, chaque passage de la fin au debut compterait comme une derive
            # enorme et declencherait un saut inutile.
            if ecart > self.duree / 2:
                ecart -= self.duree
            elif ecart < -self.duree / 2:
                ecart += self.duree
            if self._neuf:
                self._neuf = False
                self._origine = vise
                self._t0 = time.monotonic()
                self._ecrit = int(vise * self.taux)
                self._saut += 1
                return ecart

            # PHASE D'ESTIMATION : on regarde, on ne touche a rien. Sauter pendant qu'on
            # mesure fausserait la mesure avec ses propres corrections.
            if not self.retard_mesure:
                self._retards.append(ecart)
                if len(self._retards) >= CALAGES_ESTIMATION:
                    # LA MEDIANE, PAS LA MOYENNE : un seul calage tombe pendant un hoquet du
                    # moteur tirerait une moyenne, et l'on aurait fige ce hoquet pour la
                    # soiree.
                    ordonnes = sorted(self._retards)
                    self._retard = ordonnes[len(ordonnes) // 2]
                    self.retard_mesure = True
                return ecart

            if abs(ecart) > DERIVE_MAX_S:
                self._origine = vise - self._retard
                self._t0 = time.monotonic()
                self._ecrit = int(max(0.0, self._origine) * self.taux)
                self._saut += 1
        return ecart

    @property
    def retard_ms(self):
        """Le retard constant de la chaine audio, une fois mesure. Zero avant."""
        return 1000.0 * self._retard

    @property
    def joue(self):
        return self._vivant
