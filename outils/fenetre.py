#!/usr/bin/env python3
"""Simulation de l'unité de rendu : elle reçoit, elle ne demande rien.

CE QUE CE FICHIER EST, ET CE QU'IL N'EST PAS.

Ce n'est pas une interface. C'est le **mock du GPU** : le jour où l'eGPU sera branché en
PCIe ou en USB-C, il lira exactement ces 256 octets dans `/dev/shm`, de la même façon, avec
les mêmes contraintes. Ce fichier se comporte donc comme lui — lecture seule, jamais
bloquant, et il vide l'anneau jusqu'au plus récent sans se plaindre de ce qu'il a manqué.
Une image en retard n'a aucune valeur : c'est la règle du projet et elle vaut aussi ici.

LE PARTAGE DES ROLES, TEL QUE LE DJ L'A POSE.

    crate  --HTTP REST-->  C#              le seul reseau legitime : la fiche arrive par la
    C#     --/dev/shm-->   cette fenetre   le GPU simule : il recoit
    C#     --/dev/shm-->   fenetre_reference.py   la mesure : est-ce juste

Le navigateur ne figure nulle part la-dedans, et c'est pour cela qu'il s'en va.

POURQUOI CE FICHIER EXISTE.

Le visuel était jugé à travers un navigateur, et le navigateur n'est pas la cible. Le jour
où l'unité de rendu tournera, elle lira ces mêmes 256 octets dans `/dev/shm` sans qu'aucun
HTTP, aucun WebSocket ni aucun moteur web ne s'interpose. Juger la fluidité à travers Chrome
revient à mesurer un chemin qu'on ne livrera jamais — et le DJ l'a dit avant moi : « c'est
lourd les rafraîchissements ».

Cette fenêtre ne demande rien à personne. Elle ouvre le fichier partagé, lit la dernière
case publiée, dessine. C'est aussi une validation du contrat GPU : si ce programme affiche
quelque chose de juste, le contrat tient, indépendamment du langage qui l'écrit.

    python3 outils/fenetre.py

**Elle est faite pour être modifiée.** Tout ce qui se dessine tient dans `Mur.paintEvent`,
et chaque grandeur du paquet est nommée dans `Paquet`. Ajouter une forme, c'est ajouter une
méthode et l'appeler ; rien d'autre à comprendre.

LA CHARTE EST CELLE DU DJ : du gris, un seul accent vert, aucun rouge.
"""

import copy
import mmap
import os
import struct
import sys
import time

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import (QColor, QFont, QFontMetricsF, QLinearGradient,
                           QPainter, QPen)
from PySide6.QtWidgets import QApplication, QWidget

sys.path.insert(0, __file__.rsplit("/", 1)[0])
import formes
import mouvement


CHEMIN = "/dev/shm/emotion-emulator"

# En-tête de l'anneau. Chaque curseur occupe sa propre ligne de cache : sans cet
# espacement, l'écriture de l'un invaliderait le cache de l'autre.
EN_TETE = 192
OFF_MAGIC, OFF_CAPACITE, OFF_TAILLE, OFF_ECRIT = 0, 4, 8, 64
MAGIC = 0x454D5230  # « EMR0 »

# Le paquet, 256 octets, quatre lignes de cache. Les décalages sont ceux de GpuPacket :
# ce fichier et lui doivent rester d'accord, c'est tout le contrat.
P_SEQUENCE, P_TEMPS = 4, 8
P_NIVEAU, P_BPM, P_PHASE = 16, 20, 24
P_FRAPPES = 40
P_BANDES = 48          # douze octets
P_CENTROIDE, P_OUVERTURE, P_DENSITE = 100, 101, 102
P_BEAT = 103
P_VOIX_BAS, P_VOIX_MED, P_VOIX_HAUT = 96, 97, 98
P_NOUVEAUTE = 47
P_VOIX_COUPS = 99
P_MONTEE = 106
P_STRUCTURE = 107
P_BPM_ATTENDU = 192
P_DERIVE_VUE = 200
P_BPM_ANNONCE = 204
P_ANNONCE = 208
P_SOURCES = 128        # huit mots de huit octets
SOURCE_PAS = 8
S_NIVEAU, S_HAUTEUR, S_DRAPEAUX, S_NOM, S_ENTENDU, S_NETTETE, S_FORME = 0, 1, 2, 3, 4, 5, 7

GRIS_FOND = QColor(14, 14, 15)
GRIS_CADRE = QColor(48, 50, 52)
GRIS_TEXTE = QColor(122, 126, 130)
GRIS_CLAIR = QColor(198, 202, 206)
VERT = QColor(64, 196, 122)
VERT_SOURD = QColor(38, 110, 74)


class Anneau:
    """Lecteur de l'anneau partagé. Ne bloque jamais, ne se plaint jamais."""

    def __init__(self, chemin=CHEMIN):
        self.chemin = chemin
        self.mm = None
        self.capacite = 0
        self.taille = 0
        self.ouvrir()

    def ouvrir(self):
        if self.mm is not None or not os.path.exists(self.chemin):
            return False
        taille_fichier = os.path.getsize(self.chemin)
        if taille_fichier < EN_TETE:
            return False
        fd = os.open(self.chemin, os.O_RDONLY)
        try:
            self.mm = mmap.mmap(fd, taille_fichier, prot=mmap.PROT_READ)
        finally:
            os.close(fd)

        magic = struct.unpack_from("<I", self.mm, OFF_MAGIC)[0]
        if magic != MAGIC:
            self.mm = None
            return False
        self.capacite = struct.unpack_from("<i", self.mm, OFF_CAPACITE)[0]
        self.taille = struct.unpack_from("<i", self.mm, OFF_TAILLE)[0]
        return True

    def dernier(self):
        """La case la plus récemment publiée, ou None."""
        if self.mm is None and not self.ouvrir():
            return None
        ecrit = struct.unpack_from("<q", self.mm, OFF_ECRIT)[0]
        if ecrit <= 0:
            return None
        # On lit la case précédant le curseur : celle-là est complète par construction,
        # le producteur n'avance le curseur qu'après avoir fini d'écrire.
        rang = (ecrit - 1) & (self.capacite - 1)
        base = EN_TETE + rang * self.taille
        return Paquet(self.mm, base)


class Paquet:
    """Une image, telle que le moteur l'a publiée. Chaque grandeur porte son nom."""

    def __init__(self, mm, base):
        self.sequence = struct.unpack_from("<I", mm, base + P_SEQUENCE)[0]
        self.temps = struct.unpack_from("<q", mm, base + P_TEMPS)[0]
        self.niveau = struct.unpack_from("<f", mm, base + P_NIVEAU)[0]
        self.bpm = struct.unpack_from("<f", mm, base + P_BPM)[0]
        self.phase = struct.unpack_from("<f", mm, base + P_PHASE)[0]
        frappes = mm[base + P_FRAPPES]
        self.kick = bool(frappes & 1)
        self.clap = bool(frappes & 2)
        self.charley = bool(frappes & 4)
        self.beat = mm[base + P_BEAT]
        self.brillance = mm[base + P_CENTROIDE] / 255.0
        self.ouverture = mm[base + P_OUVERTURE] / 255.0
        self.densite = mm[base + P_DENSITE] / 255.0
        self.voix = (mm[base + P_VOIX_BAS] / 255.0,
                     mm[base + P_VOIX_MED] / 255.0,
                     mm[base + P_VOIX_HAUT] / 255.0)
        self.bpm_attendu = struct.unpack_from("<f", mm, base + P_BPM_ATTENDU)[0]
        self.nouveaute = mm[base + P_NOUVEAUTE] / 255.0
        self.montee = mm[base + P_MONTEE] / 255.0
        self.rupture = bool(mm[base + P_STRUCTURE] & 1)
        self.derive_vue = mm[base + P_DERIVE_VUE] / 255.0
        self.bpm_annonce = struct.unpack_from("<f", mm, base + P_BPM_ANNONCE)[0]
        self.annonce = bool(mm[base + P_ANNONCE])
        coups = mm[base + P_VOIX_COUPS]
        self.coup_grave = bool(coups & 1)
        self.bandes = [mm[base + P_BANDES + i] / 255.0 for i in range(12)]
        self.sources = []
        for r in range(6):
            o = base + P_SOURCES + r * SOURCE_PAS
            self.sources.append({
                "niveau": mm[o + S_NIVEAU] / 255.0,
                "hauteur": mm[o + S_HAUTEUR] / 255.0,
                "frappe": bool(mm[o + S_DRAPEAUX] & 1),
                "nettete": mm[o + S_NETTETE] / 255.0,
                "entendu": mm[o + S_ENTENDU] / 255.0,
                "nom": mm[o + S_NOM],
                "forme": mm[o + S_FORME],
            })


def entre(avant, courant, a):
    """Une vue du paquet à mi-chemin entre les deux dernières images d'analyse.

    LES ÉVÉNEMENTS NE S'INTERPOLENT PAS, LES GRANDEURS SI. Une impulsion à mi-chemin
    n'est plus une impulsion : les drapeaux de frappe sont donc pris tels quels sur
    l'image courante, et seules les grandeurs continues sont mêlées.
    """
    v = copy.copy(courant)
    if avant is None or a >= 1.0:
        return v

    def m(x, y):
        return x + (y - x) * a

    v.niveau = m(avant.niveau, courant.niveau)
    v.brillance = m(avant.brillance, courant.brillance)
    v.ouverture = m(avant.ouverture, courant.ouverture)
    v.densite = m(avant.densite, courant.densite)
    v.montee = m(avant.montee, courant.montee)
    v.derive_vue = m(avant.derive_vue, courant.derive_vue)
    v.voix = tuple(m(x, y) for x, y in zip(avant.voix, courant.voix))
    v.bandes = [m(x, y) for x, y in zip(avant.bandes, courant.bandes)]

    # LA PHASE S'ENROULE, ET MÊLER DE PART ET D'AUTRE D'UN TOUR LA FERAIT REVENIR EN
    # ARRIÈRE — un curseur de mesure qui recule d'un tour à chaque temps. On ne mêle donc
    # que tant qu'elle avance ; au passage du tour on prend la valeur courante.
    v.phase = m(avant.phase, courant.phase) if courant.phase >= avant.phase else courant.phase

    v.sources = [
        {**c,
         "niveau": m(x["niveau"], c["niveau"]),
         "hauteur": m(x["hauteur"], c["hauteur"]),
         "nettete": m(x["nettete"], c["nettete"])}
        for x, c in zip(avant.sources, courant.sources)
    ]
    return v


class Pulse:
    """Une impulsion qui décroît.

    LE PAQUET PORTE DES IMPULSIONS, PAS DES ÉTATS, et les faire décroître est le travail du
    renderer. La leçon vient du renderer web : il redessinait le dernier paquet à chaque
    réveil et relisait les mêmes drapeaux, ce qui rallumait l'éclair en boucle et épinglait
    le battement. Ici une impulsion ne se consomme qu'une fois par paquet.
    """

    def __init__(self, chute):
        self.valeur = 0.0
        self.chute = chute

    def tirer(self):
        self.valeur = 1.0

    def pas(self, dt):
        self.valeur = max(0.0, self.valeur - dt * self.chute)


class Mur(QWidget):
    """Ce qui se dessine. Tout est ici, et rien ailleurs."""

    def __init__(self):
        super().__init__()
        self.setWindowTitle("emotion emulator")
        self.resize(1180, 780)
        self.setAutoFillBackground(False)

        self.anneau = Anneau()
        self.paquet = None
        self.derniere_sequence = -1

        self.kick = Pulse(3.2)
        self.clap = Pulse(4.5)
        self.charley = Pulse(7.0)
        self.coups = [Pulse(4.0) for _ in range(6)]
        self.grave = Pulse(3.0)

        # L'ANNONCE DE TEMPO NE DURE QU'UNE IMAGE D'ANALYSE. On la tient quatre temps a
        # l'ecran, sans quoi elle passerait sous l'oeil sans etre lue. La rupture, elle,
        # tient trois temps : c'est le geste qui fait respirer le cadre.
        self.annonce = Pulse(0.4)
        self.annonce_bpm = 0.0
        self.rupture = Pulse(0.5)
        self.derive = 0.0

        # Le balayage traverse l'ecran a chaque rupture. Il n'obeit a aucune frappe : il
        # annonce, comme la tension.
        self.balayage = -1.0

        # L'onde defile : elle a besoin d'une horloge, pas d'un evenement.
        self.tempo = 0.0

        # LA PRÉDICTION ET L'INTERPOLATION, LES DEUX SEULES CHOSES QUE LE RENDU CALCULE.
        #
        # L'horloge attend le kick au lieu de le constater : c'est la réponse du projet à
        # la centaine de millisecondes que la chaîne accumule, et l'œil décroche vers
        # quarante. L'interpolation comble les paliers de 21 ms entre deux images
        # d'analyse. Ce sont deux opérations distinctes, et il faut les deux — le renderer
        # web l'a appris en retirant l'une en croyant garder l'autre.
        self.horloge = mouvement.Horloge()
        self.entre_images = mouvement.Interpolation()
        self.avance_ms = float(os.environ.get("EMOTION_AVANCE_MS", "30"))
        self.horodatage = time.monotonic() * 1000.0

        self.mono = QFont("monospace", 10)
        self.mono.setStyleHint(QFont.StyleHint.Monospace)

        self.minuterie = QTimer(self)
        self.minuterie.timeout.connect(self.battre)
        self.minuterie.start(16)          # environ soixante images par seconde

    def battre(self):
        maintenant = time.monotonic() * 1000.0
        dt_ms = max(1.0, min(100.0, maintenant - self.horodatage))
        self.horodatage = maintenant

        p = self.anneau.dernier()
        if p is not None:
            # Une impulsion par paquet, jamais par image de rendu. Le renderer web
            # relisait les mêmes drapeaux à chaque réveil : 47 images côté signal, 120
            # côté écran, donc chaque frappe lue deux à trois fois — et le recalage
            # rappelé sur la même frappe, ce qui épinglait la phase.
            if p.sequence != self.derniere_sequence:
                self.derniere_sequence = p.sequence
                self.entre_images.pousser(p, maintenant)

                # LE KICK NE SE DÉCLENCHE PLUS SUR LA DÉTECTION QUAND LA GRILLE TIENT :
                # la détection ne sert alors qu'à recaler l'horloge, et c'est l'horloge
                # qui tire, au moment où le temps tombe plutôt qu'après l'avoir constaté.
                if p.kick:
                    self.horloge.caler()
                    if not self.horloge.verrouille:
                        self.kick.tirer()
                if p.clap:
                    self.clap.tirer()
                if p.charley:
                    self.charley.tirer()
                for r, s in enumerate(p.sources):
                    if s["frappe"]:
                        self.coups[r].tirer()
                if p.coup_grave:
                    self.grave.tirer()
                if p.nouveaute > 0.5:
                    self.balayage = 0.0
                if p.rupture:
                    self.rupture.tirer()
                if p.annonce and p.bpm_annonce > 0:
                    self.annonce.tirer()
                    self.annonce_bpm = p.bpm_annonce
            self.derive = p.derive_vue

        # L'horloge avance à la cadence de l'écran, pas à celle du signal.
        courant = self.entre_images.courant
        self.horloge.pas(dt_ms, courant.bpm if courant else None, self.avance_ms)
        if self.horloge.vient_de_tirer:
            self.kick.tirer()

        # Ce qu'on dessine est la vue interpolée, jamais la dernière image brute.
        if courant is not None:
            self.paquet = entre(self.entre_images.avant, courant,
                                self.entre_images.alpha(maintenant))

        dt = dt_ms / 1000.0
        self.tempo += dt
        if self.balayage >= 0:
            self.balayage += dt * 0.9
            if self.balayage > 1.3:
                self.balayage = -1.0
        for imp in (self.kick, self.clap, self.charley, self.grave,
                    self.annonce, self.rupture, *self.coups):
            imp.pas(dt)
        self.update()

    # ------------------------------------------------------------------ dessin
    def paintEvent(self, _):
        d = QPainter(self)
        d.setRenderHint(QPainter.RenderHint.Antialiasing, False)
        d.fillRect(self.rect(), GRIS_FOND)
        d.setFont(self.mono)

        w, h = self.width(), self.height()
        marge = 24

        if self.paquet is None:
            d.setPen(GRIS_TEXTE)
            d.drawText(marge, 40, "anneau vide — lancer le serveur : ./run.sh pulse")
            return

        p = self.paquet

        # LE CADRE NE SUIT PAS L'ENERGIE, ET C'EST UNE CORRECTION DEJA PAYEE.
        #
        # L'ouverture du filtre pilotait l'echelle de la scene entiere : mesuree en direct,
        # elle oscillait entre 0,979 et 1,000 A CHAQUE IMAGE, soit une trentaine de pixels
        # de battement permanent sur les bords. Les cases sont une grille de lecture ;
        # l'oeil s'y ancre pour comparer une source a sa voisine, et une grille qui respire
        # empeche exactement cela. Seuls les gestes lents — une montee sur huit mesures,
        # une rupture — ont le droit de deplacer le cadre. Le filtre agit toujours, mais
        # A L'INTERIEUR des cases, par `coupe`.
        # L'AMPLITUDE SE DEDUIT DE LA MARGE, ELLE NE SE RECOPIE PAS.
        #
        # Le web soufflait de 8 % par grandeur, soit 16 % au pire. Sa toile occupait tout
        # l'ecran et ses cases etaient posees loin des bords, si bien que ce zoom ne rognait
        # que du vide. Ici la marge vaut 24 px sur 1180 : le meme 16 % emporte 94 px de
        # chaque cote, donc l'en-tete et les deux colonnes extremes. Recopier la constante
        # aurait reimporte l'intention a l'envers — c'est la meme erreur que l'avance de
        # caractere supposee. On garde le geste, on en calcule l'amplitude sur la place
        # reellement disponible.
        marge_max = 2.0 * marge / w
        souffle = 1 + (p.montee + self.rupture.valeur) * 0.5 * marge_max
        d.save()
        d.translate(w / 2, h / 2)
        d.scale(souffle, souffle)
        d.translate(-w / 2, -h / 2)

        self.tension(d, p, w, h)
        y = self.bandeau(d, p, marge, 34, w - 2 * marge)
        y = self.mesure(d, p, marge, y + 26, w - 2 * marge)
        y = self.registres(d, p, marge, y + 30, w - 2 * marge)
        y = self.trois_cases(d, p, marge, y + 18, w - 2 * marge)
        self.piste_frappes(d, p, marge, y + 18, w - 2 * marge)
        self.balayer(d, w, h)
        d.restore()

    def bandeau(self, d, p, x, y, largeur):
        d.setPen(GRIS_CLAIR)
        d.drawText(x, y, "emotion")
        d.setPen(VERT)
        d.drawText(x + 90, y, f"{p.bpm:6.1f} BPM" if p.bpm > 0 else "     — BPM")
        d.setPen(GRIS_TEXTE)
        d.drawText(x + 220, y, f"paquet {p.sequence}     temps {p.temps / 1000:7.1f} s")

        # L'ETAT DE L'HORLOGE, PARCE QU'ELLE DECIDE DU KICK. Tant qu'elle n'est pas
        # verrouillee, le kick vient de la detection et arrive donc en retard de toute la
        # chaine ; verrouillee, il est predit. Ne pas le montrer rendrait indiscernables
        # deux comportements tres differents.
        etat = f"avance {self.avance_ms:3.0f} ms"
        if self.horloge.verrouille:
            etat += f"   horloge ● {self.horloge.ecart_ms:+5.1f} ms"
        else:
            etat += f"   horloge ○ {self.horloge.fiabilite:.2f}"
        d.setPen(GRIS_CLAIR if self.horloge.verrouille else GRIS_CADRE)
        d.drawText(x + largeur - 260, y, etat)
        return y

    def mesure(self, d, p, x, y, largeur):
        """La position dans la mesure de quatre temps, telle que le moteur la publie."""
        cases = 32
        pas = largeur / cases
        ou = int(min(max(p.phase, 0.0), 0.999) * cases)
        for i in range(cases):
            fort = i % 8 == 0
            d.setPen(QPen(VERT if i == ou else (GRIS_CADRE if fort else QColor(34, 36, 38)), 1))
            hauteur = 14 if (fort or i == ou) else 7
            gx = int(x + i * pas)
            d.drawLine(gx, y, gx, y + hauteur)
        d.setPen(GRIS_TEXTE)
        d.drawText(x + largeur - 70, y + 12, f"temps {p.beat + 1}" if p.beat < 4 else "temps -")
        return y + 16

    def registres(self, d, p, x, y, largeur):
        """Les six sources, chacune dans sa case, avec sa forme.

        Le rang ne decide pas de la forme : le paquet porte un octet par source et c'est lui
        qui commande. L'ordre du grave a l'aigu n'est qu'un repli quand la fiche n'a rien dit.
        """
        cols, rangs = 3, 2
        larg = (largeur - 2 * 14) / cols
        haut = 132
        for r, s in enumerate(p.sources):
            cx = x + (r % cols) * (larg + 14)
            cy = y + (r // cols) * (haut + 12)

            d.setPen(QPen(GRIS_CADRE, 1))
            d.drawRect(int(cx), int(cy), int(larg), haut)

            nom = formes.NOMS.get(s["forme"], formes.NOMS[(r % 6) + 1])
            d.setPen(GRIS_TEXTE)
            titre = f"{r + 1}·{s['nom']}  {nom}" if s["nom"] else f"{r + 1}  {nom}"
            d.drawText(int(cx) + 8, int(cy) + 16, titre)
            # LE TITRE PORTE LA MATURITE DE LA SOURCE, EN QUATRE CRANS.
            #
            # Tant que la source n'a pas ete assez entendue, on n'annonce rien : ce qui joue
            # dans une bande change d'un disque a l'autre, et annoncer un piano la ou passe
            # un saxophone est pire que de se taire. Une fois assez ecoutee, le verdict dit
            # si elle est seule dans son registre ou si plusieurs instruments s'y relaient.
            if s["entendu"] < 0.99:
                verdict, vif = "…", False
            elif s["nettete"] > 0.6:
                verdict, vif = "● nette", True
            elif s["nettete"] > 0.3:
                verdict, vif = "◐ mêlée", False
            else:
                verdict, vif = "○ partagée", False
            d.setPen(GRIS_CLAIR if vif else GRIS_CADRE)
            d.drawText(int(cx + larg) - 72, int(cy) + 16, verdict)

            # LA TAILLE SE DONNE EN PIXELS, PAS EN POINTS. setPointSizeF prend des
            # points ; a 96 points par pouce un point vaut 1,33 pixel, donc une taille
            # calculee en pixels et passee la sortait un tiers trop grande — et chaque
            # ligne debordait d'autant. C'est ce qui faisait se chevaucher les cases.
            provisoire = formes.Grille(cx, cy, larg, haut, lignes=9)
            police = QFont(self.mono)
            police.setPixelSize(max(6, int(provisoire.taille)))

            # Puis on mesure l'avance reelle de cette police, et l'on recompte les colonnes
            # avec elle. Mesurer plutot que supposer : c'est la seule chose qui empeche une
            # ligne de sortir de sa case.
            avance = QFontMetricsF(police).horizontalAdvance("M")
            g = formes.Grille(cx, cy, larg, haut, lignes=9, avance=avance)
            lignes, force = formes.rendu(nom, g, s["niveau"], s["hauteur"],
                                         self.coups[r].valeur, self.tempo)

            # ET L'ON DECOUPE, PAR-DESSUS TOUT LE RESTE. Les controles servent a comprendre,
            # le decoupage garantit : ce qui depasse n'est pas dessine, quelle qu'en soit la
            # cause. Le renderer web a mis trois tentatives a l'admettre.
            d.save()
            d.setClipRect(int(cx) + 1, int(cy) + 1, int(larg) - 2, haut - 2)
            d.setFont(police)
            teinte = QColor(VERT)
            teinte.setAlphaF(min(1.0, 0.22 + force * 0.70))
            d.setPen(teinte)
            for i, ligne in enumerate(lignes):
                d.drawText(int(g.x0), int(g.y0 + (i + 1) * g.ch), ligne)
            d.restore()
            d.setFont(self.mono)
        return y + rangs * (haut + 12)

    def _case(self, d, x, y, larg, haut, titre, lignes, teinte, alpha):
        """Une case : son cadre, son nom, et son dessin decoupe a l'interieur.

        LE DECOUPAGE GARANTIT, LES CONTROLES EXPLIQUENT. Ce qui depasse n'est pas dessine,
        quelle qu'en soit la cause — le renderer web a mis trois tentatives a l'admettre, et
        porter son dessin sans porter cette garantie serait refaire l'erreur.
        """
        d.setPen(QPen(GRIS_CADRE, 1))
        d.drawRect(int(x), int(y), int(larg), int(haut))
        d.setPen(GRIS_TEXTE)
        d.drawText(int(x) + 8, int(y) + 16, titre)
        if not lignes:
            return

        prov = formes.Grille(x, y, larg, haut, len(lignes))
        police = QFont(self.mono)
        police.setPixelSize(max(6, int(prov.taille)))
        avance = QFontMetricsF(police).horizontalAdvance("M")
        g = formes.Grille(x, y, larg, haut, len(lignes), avance=avance)

        d.save()
        d.setClipRect(int(x) + 1, int(y) + 1, int(larg) - 2, int(haut) - 2)
        d.setFont(police)
        c = QColor(teinte)
        c.setAlphaF(min(1.0, alpha))
        d.setPen(c)
        for i, ligne in enumerate(lignes):
            d.drawText(int(g.x0), int(g.y0 + (i + 1) * g.ch), ligne)
        d.restore()
        d.setFont(self.mono)

    def trois_cases(self, d, p, x, y, largeur):
        """GRAVE, GRAIN, SPECTRE. Elles ne portent pas de source : elles disent ce que le
        morceau fait dans son ensemble."""
        larg = (largeur - 2 * 14) / 3
        haut = 104

        v = min(1.0, p.voix[0] + self.grave.valeur * 0.5)
        self._case(d, x, y, larg, haut, "GRAVE",
                   formes.grave(formes.Grille(x, y, larg, haut, 7), v),
                   VERT, 0.30 + v * 0.6)

        # `coupe` est l'ouverture du filtre portee a la puissance 1,5 : c'est par elle que
        # le filtre agit, DANS la case, depuis qu'il ne deplace plus le cadre. Filtre ferme,
        # le grain s'eteint — ce qui est exactement ce que le geste fait au son.
        coupe = p.ouverture ** 1.5
        fond = (p.sources[4]["niveau"] + p.sources[5]["niveau"]) * 0.5
        vh = max(fond * 0.7, self.charley.valeur)
        cx = x + larg + 14
        self._case(d, cx, y, larg, haut, "GRAIN",
                   formes.grain(formes.Grille(cx, y, larg, haut, 7), fond, self.charley.valeur)
                   if vh > 0.01 and coupe > 0.05 else None,
                   GRIS_CLAIR, 0.25 + vh * 0.7)

        # DOUZE JAUGES COUCHEES, UNE PAR BANDE, REMPLIES DEPUIS LA GAUCHE.
        #
        # Le renderer web avait deja tranche ce point : des colonnes de blocs empilees sont
        # illisibles, une pile de jauges est la lecture d'un mixeur et l'oeil y suit une
        # bande sans effort. Le premier portage etait reparti sur la forme rejetee.
        sx = x + 2 * (larg + 14)
        g = formes.Grille(sx, y, larg, haut, 12)
        police = QFont(self.mono)
        police.setPixelSize(max(5, int(g.taille)))
        avance = QFontMetricsF(police).horizontalAdvance("M")
        g = formes.Grille(sx, y, larg, haut, 12, avance=avance)

        d.setPen(QPen(GRIS_CADRE, 1))
        d.drawRect(int(sx), int(y), int(larg), int(haut))
        d.setPen(GRIS_TEXTE)
        d.drawText(int(sx) + 8, int(y) + 16, "SPECTRE")

        d.save()
        d.setClipRect(int(sx) + 1, int(y) + 1, int(larg) - 2, int(haut) - 2)
        d.setFont(police)
        for i, b in enumerate(p.bandes):
            c = QColor(VERT)
            c.setAlphaF(min(1.0, 0.20 + b * 0.7))
            d.setPen(c)
            d.drawText(int(g.x0), int(g.y0 + (i + 1) * g.ch), formes.jauge(g, b))
        d.restore()
        d.setFont(self.mono)
        return y + haut

    def piste_frappes(self, d, p, x, y, largeur):
        """La mesure, sur toute la largeur : le kick s'ecarte du centre, les claps allument
        les bords, la tension s'ecrit dessous."""
        haut = 74
        sur_le_un = 1.0 if p.beat == 0 else (0.5 if p.beat < 4 else 0.62)
        prov = formes.Grille(x, y, largeur, haut, 3)
        police = QFont(self.mono)
        police.setPixelSize(max(6, int(prov.taille)))
        avance = QFontMetricsF(police).horizontalAdvance("M")
        g = formes.Grille(x, y, largeur, haut, 3, avance=avance)
        ligne, sous = formes.frappes(g, self.kick.valeur, self.clap.valeur,
                                     sur_le_un, p.montee)

        d.setPen(QPen(GRIS_CADRE, 1))
        d.drawRect(int(x), int(y), int(largeur), haut)
        d.setPen(GRIS_TEXTE)
        d.drawText(int(x) + 8, int(y) + 16, "FRAPPES")

        # L'ANNONCE DE TEMPO, LA OU L'ECART SE CONSTATE.
        #
        # Elle se pose contre les frappes parce que c'est la qu'on la verifie : le motif du
        # kick tombe a cote, et le chiffre dit pourquoi. Elle ne dure qu'une image cote
        # analyse ; on la tient quatre temps pour qu'elle soit lisible. La derive teinte
        # l'annonce — plus la grille a glisse, plus elle s'impose.
        if self.annonce.valeur > 0.02 and self.annonce_bpm > 0:
            c = QColor(VERT)
            c.setAlphaF(min(1.0, 0.25 + self.annonce.valeur * 0.55 + self.derive * 0.20))
            d.setPen(c)
            d.drawText(int(x) + 90, int(y) + 16, f"\u25b8 {self.annonce_bpm:.1f} BPM")

        d.save()
        d.setClipRect(int(x) + 1, int(y) + 1, int(largeur) - 2, haut - 2)
        d.setFont(police)
        c = QColor(VERT)
        c.setAlphaF(min(1.0, 0.14 + self.kick.valeur * 0.8))
        d.setPen(c)
        d.drawText(int(g.x0), int(g.y0 + 2 * g.ch), ligne)
        if sous:
            c2 = QColor(VERT_SOURD)
            c2.setAlphaF(min(1.0, 0.25 + p.montee * 0.5))
            d.setPen(c2)
            d.drawText(int(g.x0), int(g.y0 + 3 * g.ch), sous)
        d.restore()
        d.setFont(self.mono)

    def tension(self, d, p, w, h):
        """Une lueur qui monte du bas. Elle n'obeit a aucun evenement : elle annonce.

        C'est la seule chose du systeme qu'on lise en avance — partout ailleurs on constate
        un evenement et l'on court apres. Une montee se joue donc SUR l'instant plutot
        qu'apres, et la latence de la chaine cesse de compter pour elle.
        """
        if p.montee < 0.02:
            return
        g = QLinearGradient(0, h, 0, h * 0.4)
        c = QColor(VERT_SOURD)
        c.setAlphaF(min(1.0, p.montee * 0.28))
        g.setColorAt(0.0, c)
        fin = QColor(VERT_SOURD)
        fin.setAlphaF(0.0)
        g.setColorAt(1.0, fin)
        d.fillRect(0, int(h * 0.4), w, int(h * 0.6), g)

    def balayer(self, d, w, h):
        """Une bande qui traverse a chaque rupture."""
        if self.balayage < 0:
            return
        x = (self.balayage * 1.4 - 0.2) * w
        fondu = max(0.0, 1 - max(0.0, self.balayage - 0.8) * 5)
        g = QLinearGradient(x - w * 0.10, 0, x + w * 0.10, 0)
        bord = QColor(VERT)
        bord.setAlphaF(0.0)
        centre = QColor(VERT)
        centre.setAlphaF(0.16 * fondu)
        g.setColorAt(0.0, bord)
        g.setColorAt(0.5, centre)
        g.setColorAt(1.0, bord)
        d.fillRect(int(x - w * 0.10), 0, int(w * 0.20), h, g)

    def keyPressEvent(self, e):
        """Q ferme, + et - reglent l'avance du visuel.

        LE CALAGE SE FAIT DEPUIS LA PISTE, PAS DEPUIS LA TABLE. Le son met 5,8 ms pour
        atteindre celui qui regle a la table, et 29 pour le public a dix metres : regler
        depuis la table revient a faire preceder le mur de vingt-trois millisecondes pour
        tout le monde d'autre, un ecart plus grand que tout ce que l'analyse a gagne en une
        soiree de mesures. Le reglage appartient donc a ce qui affiche, et a lui seul —
        c'est la seule piece de la chaine qui puisse reellement l'appliquer.
        """
        if e.key() in (Qt.Key.Key_Q, Qt.Key.Key_Escape):
            self.close()
        elif e.key() in (Qt.Key.Key_Plus, Qt.Key.Key_Equal):
            self.avance_ms = min(200.0, self.avance_ms + 5.0)
        elif e.key() in (Qt.Key.Key_Minus, Qt.Key.Key_Underscore):
            self.avance_ms = max(0.0, self.avance_ms - 5.0)


def main():
    app = QApplication(sys.argv)
    mur = Mur()
    mur.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
