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
import json
import math
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
P_PHASE_TEMPS = 117    # position dans le temps, toujours remplie
P_GRILLE_SURE = 118    # a quel point le « 1 » est etabli
P_ACCORD = 119         # ce qu'une voie independante dit de la periode
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

# L'enveloppe de chaque source, hors du mot de la source : celui-ci fait huit octets et
# ils sont tous pris. Deux octets chacun — le pique et la tenue.
P_ENVELOPPES = 216
ENVELOPPE_PAS = 2

# Depuis combien de temps chaque source s'est tue, en seiziemes de seconde. Zero si elle
# joue. Il y a eu deux octets de plus ici — le role de la source dans l'orchestre et sa
# place dans le temps — et ils ont ete retires : mesures sur six morceaux du bac, les
# montees des sources separees ne sont pas calees sur le temps, R valant 0,05 a 0,17 pour
# un hasard de 0,13 a 0,25. Le classement rendait « continu » trente-six fois sur
# trente-six.
P_RETRAITS = 232

# Ce qui se repete, et tous les combien. Trois octets : periode en mesures, certitude,
# bande. Une periode nulle veut dire « on ne sait pas », et c'est une reponse.
P_MOTIF = 240
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

        # LA PHASE DU TEMPS N'EST PAS LA PHASE DE LA MESURE.
        #
        # `phase` (offset 24) est la position dans la mesure de quatre temps, et elle vaut
        # zero tant que le « 1 » n'est pas identifie — sur un passage du bac, quatorze pour
        # cent des images seulement. `phase_temps` dit ou l'on est dans le temps courant,
        # et elle est toujours remplie : savoir ou l'on en est du temps ne demande pas de
        # savoir quel temps c'est. C'est elle qui nourrit l'horloge.
        self.phase_temps = mm[base + P_PHASE_TEMPS] / 255.0
        self.grille_sure = mm[base + P_GRILLE_SURE] / 255.0

        # L'ACCORD VIENT D'UNE VOIE QUI NE CONNAIT PAS LE TEMPO. Les familles de frappes
        # se forment sur le timbre ; si elles battent a des rapports francs du tempo
        # detecte, les deux voies se confirment. C'est la seule verification du projet qui
        # ne soit pas circulaire, et c'est elle qui autorise la prediction.
        self.accord = mm[base + P_ACCORD] / 255.0
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
        self.motif = mm[base + P_MOTIF]
        self.motif_sur = mm[base + P_MOTIF + 1] / 255.0
        self.motif_bande = mm[base + P_MOTIF + 2]
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
                # LE PIQUE ET LA TENUE : ce que le niveau seul ne disait pas. Une corde
                # pincee monte d'un coup et meurt ; un souffle s'installe et ne frappe
                # jamais. Les deux donnaient le meme niveau moyen et la meme hauteur.
                "pique": mm[base + P_ENVELOPPES + r * ENVELOPPE_PAS] / 255.0,
                "tenue": mm[base + P_ENVELOPPES + r * ENVELOPPE_PAS + 1] / 255.0,
                # DEPUIS COMBIEN DE TEMPS ELLE S'EST TUE.
                # « Quand le kick est en retrait pendant un moment, on est censé le savoir. »
                "retrait": mm[base + P_RETRAITS + r] / 16.0,
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
    v.grille_sure = m(avant.grille_sure, courant.grille_sure)
    # La certitude se mêle comme une grandeur ; la période est un verdict, elle saute.
    v.motif_sur = m(avant.motif_sur, courant.motif_sur)
    v.accord = m(avant.accord, courant.accord)
    v.voix = tuple(m(x, y) for x, y in zip(avant.voix, courant.voix))
    v.bandes = [m(x, y) for x, y in zip(avant.bandes, courant.bandes)]

    # LA PHASE S'ENROULE, ET MÊLER DE PART ET D'AUTRE D'UN TOUR LA FERAIT REVENIR EN
    # ARRIÈRE — un curseur de mesure qui recule d'un tour à chaque temps. On ne mêle donc
    # que tant qu'elle avance ; au passage du tour on prend la valeur courante.
    v.phase = m(avant.phase, courant.phase) if courant.phase >= avant.phase else courant.phase
    v.phase_temps = (m(avant.phase_temps, courant.phase_temps)
                     if courant.phase_temps >= avant.phase_temps else courant.phase_temps)

    # L'INTERPOLATION PASSE AVANT L'ENVELOPPE, ET L'ORDRE N'EST PAS INDIFFERENT.
    #
    # Les deux se ressemblent — toutes deux lissent quelque chose — et les intervertir
    # detruirait précisément ce qu'on vient de mesurer. La règle qui les sépare est celle
    # qui tient déjà tout le reste du projet : un DESCRIPTEUR se mêle, un ÉVÉNEMENT jamais.
    #
    #   `pique` et `tenue` décrivent la NATURE d'une source. Ils se moyennent sur une
    #   seconde et demie en amont et ne bougent pas d'une fenêtre à l'autre. Les mêler
    #   entre deux images ne perd rien et supprime les paliers de 21 ms — donc ils passent
    #   ici, avec le niveau.
    #
    #   La FRAPPE, elle, ne passe pas : elle est déjà exclue de cette fonction, et c'est
    #   pour ça qu'elle survit. Une impulsion mêlée n'est plus une impulsion.
    #
    # Et l'enveloppe agit APRÈS, au moment du rendu : `pique` et `tenue` interpolés règlent
    # la façon dont l'impulsion brute retombe. Si on inversait — enveloppe d'abord,
    # interpolation ensuite — on mêlerait deux retombées calculées à des instants
    # différents, ce qui arrondirait l'attaque même quand `pique` vaut un. On aurait alors
    # dépensé un descripteur pour décrire une netteté que le rendu venait d'effacer.
    v.sources = [
        {**c,
         "niveau": m(x["niveau"], c["niveau"]),
         "hauteur": m(x["hauteur"], c["hauteur"]),
         "nettete": m(x["nettete"], c["nettete"]),
         "pique": m(x["pique"], c["pique"]),
         "tenue": m(x["tenue"], c["tenue"]),
         "retrait": m(x["retrait"], c["retrait"])}
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

    def tirer(self, force=1.0):
        """Déclenche. `force` permet à une source peu piquante de ne pas claquer.

        Une source continue — un souffle, un archet — voit passer des attaques dans son
        registre sans en être une elle-même. Lui faire produire un éclair plein serait lui
        prêter un geste qu'elle ne fait pas.
        """
        self.valeur = max(self.valeur, max(0.0, min(1.0, force)))

    def pas(self, dt, chute=None):
        """Décroît. La chute peut venir du dehors : c'est la tenue de la source.

        C'EST ICI QUE « UNE FRAPPE SUIVIE D'UNE ONDE COURTE OU LONGUE » PREND CORPS. La
        frappe est l'impulsion, l'onde est sa retombée, et sa longueur est la tenue mesurée
        sur la source elle-même. Un pizzicato retombe en un dixième de seconde, un piano en
        une seconde, un vent ne retombe pas du tout.
        """
        self.valeur = max(0.0, self.valeur - dt * (self.chute if chute is None else chute))


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

        # LE DERNIER TEMPO CONNU, ET POURQUOI ON LE GARDE A L'ECRAN.
        #
        # Le moteur ne publie un tempo que lorsqu'il en est sûr — « ne rien dire plutôt que
        # dire faux », et cette règle est juste : elle porte sur ce qu'il AFFIRME. Mais il
        # n'en est sûr que sur quatre images sur cinq, et le chiffre disparaissait donc une
        # image sur cinq. Un nombre qui clignote vingt fois par seconde ne se lit pas — le
        # DJ l'a décrit comme du buffering, et c'était exactement ça.
        #
        # On garde donc la dernière valeur connue, EN GRIS. Ce n'est pas dire faux : c'est
        # dire « voilà ce que c'était », et la couleur dit que ce n'est plus frais. Ce qui
        # serait faux, ce serait de l'afficher en vert comme une mesure de l'instant.
        self.dernier_bpm = 0.0
        self.bpm_frais = False

        # LE CURSEUR DE MESURE AVANCE SEUL ET SE CORRIGE, IL NE SAUTE JAMAIS.
        #
        # Il sautait de deux façons. `phase` vaut zéro tant que le « 1 » n'est pas
        # identifié — un tiers du temps — donc le curseur retombait au début du bandeau à
        # chaque fois que le vote lâchait. Et le rattraper par la phase du temps, qui elle
        # est toujours remplie, ne faisait que déplacer le saut : il se produisait alors à
        # chaque bascule entre les deux régimes, quarante fois par minute.
        #
        # Un repère qui saute est pire qu'un repère absent : l'œil suit le saut et perd la
        # musique. On tient donc une position locale qui avance toujours à la cadence du
        # temps, et qu'on TIRE vers la phase publiée quand celle-ci existe — la même règle
        # que partout ailleurs dans ce projet : corriger une fraction, jamais recaler d'un
        # coup.
        self.mesure_pos = 0.0
        self.phase_temps_prec = None

        # UNE SOURCE ABSENTE NE DISPARAIT PAS DE L'ECRAN, ELLE S'Y MONTRE ETEINTE.
        #
        # « Quand le kick est en retrait pendant un moment, on est censé le savoir. » Une
        # case vide et une case dont la source s'est tue se ressemblent trop ; la seconde
        # garde donc son motif, en gris, et dit depuis combien de temps.
        #
        # Il y a eu ici un battement fantôme qui marquait la PLACE de la source absente,
        # tour après tour. Il dépendait du rôle, et le rôle a été retiré faute de se
        # mesurer sur cette matière.

        # ISOLER UNE SOURCE, ET DIRE CE QU'ON ENTEND.
        #
        # « J'isole en cliquant sur la source que je veux, et je regarde si ça suit bien ce
        # qu'il dit. Si ça suit, je le laisse ; sinon je veux pouvoir montrer, à travers la
        # touche espace, moi ce que j'entends. »
        #
        # C'est la seule mesure du projet qui vienne de l'extérieur du programme. Tout le
        # reste se juge contre des grandeurs que le moteur calcule lui-même : un décalage
        # commun au juge et au jugé leur est invisible par construction. Une oreille, non.
        #
        # ON N'AFFICHE AUCUN VERDICT, ET C'EST VOULU. Ce qui est tapé part dans un journal
        # qu'on analyse après coup, avec les instants que le moteur publiait au même
        # moment. Calculer un accord à l'écran obligerait à trancher tout de suite ce qu'on
        # ne sait pas encore trancher — à commencer par la latence de la main.
        self.isolee = None
        self.cases = []                  # rectangles des six cases, pour le clic
        self.marques = []                # (source, geste, debut, fin)
        self.tenue = None                # (source, instant d'enfoncement)
        self.journal = []               # ce que le moteur publiait, image par image
        self.morceau = (sys.argv[1] if len(sys.argv) > 1 else "sans-nom")

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
                self.journaliser(p)

                # L'HORLOGE SE CALE SUR LA GRILLE DU MOTEUR, PLUS SUR LES FRAPPES.
                #
                # Elle se calait sur les drapeaux de kick. Mesuré sur un morceau du bac :
                # une frappe tous les 962 ms pour un temps de 688, soit sept temps marqués
                # sur dix — la fiabilité restait à 0,00 et la prédiction ne partait jamais.
                # La grille, elle, publie une phase qui s'accumule et se corrige.
                #
                # Le kick reste réactif tant que la grille ne sait pas : mieux vaut un
                # battement en retard qu'un battement inventé.
                self.horloge.caler(p.phase_temps, p.accord)
                if p.kick and not self.horloge.verrouille:
                    self.kick.tirer()
                if p.clap:
                    self.clap.tirer()
                if p.charley:
                    self.charley.tirer()
                for r, s in enumerate(p.sources):
                    # L'impulsion vaut ce que la source a de piquant. Une source qui monte
                    # doucement ne claque pas : son mouvement vient de son niveau.
                    if s["frappe"]:
                        self.coups[r].tirer(0.25 + 0.75 * s["pique"])
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

            # LE CURSEUR DE MESURE AVANCE DE CE DONT LE TEMPS A AVANCE, et se corrige
            # doucement quand la mesure est connue. Il doit se calculer APRES la vue
            # interpolee : nourri du paquet brut, il avancerait par paliers de 21 ms au
            # lieu de suivre l'ecran, ce qui est exactement le hoquet qu'on cherche a
            # supprimer.
            pt = self.paquet.phase_temps
            if self.phase_temps_prec is not None:
                pas_temps = pt - self.phase_temps_prec
                if pas_temps < -0.5:
                    pas_temps += 1.0
                if 0.0 <= pas_temps < 0.5:
                    self.mesure_pos = (self.mesure_pos + pas_temps / 4.0) % 1.0
            self.phase_temps_prec = pt

            if self.paquet.beat < 4:
                ecart = self.paquet.phase - self.mesure_pos
                if ecart > 0.5:
                    ecart -= 1.0
                elif ecart < -0.5:
                    ecart += 1.0
                self.mesure_pos = (self.mesure_pos + ecart * 0.12) % 1.0

        dt = dt_ms / 1000.0
        self.tempo += dt
        if self.balayage >= 0:
            self.balayage += dt * 0.9
            if self.balayage > 1.3:
                self.balayage = -1.0
        for imp in (self.kick, self.clap, self.charley, self.grave,
                    self.annonce, self.rupture):
            imp.pas(dt)

        # LA RETOMBEE DE CHAQUE SOURCE SUIT SA TENUE, ET C'EST TOUT L'INTERET DU
        # DESCRIPTEUR. Une chute commune donnait le meme geste a un pizzicato et a un
        # souffle. De huit par seconde — un huitieme de seconde de vie — a moins d'un,
        # c'est-a-dire une onde qui ne retombe plus.
        src = self.paquet.sources if self.paquet else None
        for r, imp in enumerate(self.coups):
            tenue = src[r]["tenue"] if src else 0.0
            imp.pas(dt, chute=0.7 + 8.0 * (1.0 - tenue))
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
        self.aide(d, marge, h)
        d.restore()

    def aide(self, d, x, h):
        """Une ligne en bas, et elle se pose depuis le BAS de la fenetre.

        Tout le reste de cet ecran se pose en cascade, chaque bloc rendant le y du suivant.
        Cette ligne-la n'appartient a aucune cascade : elle vit dans la place qui reste, et
        la calculer depuis le haut la ferait bouger a chaque fois qu'un bloc au-dessus
        change de hauteur.
        """
        d.setPen(GRIS_CADRE)
        if self.isolee is None:
            d.drawText(x, h - 26,
                       "clic ou 1-6 : isoler une source   ·   "
                       "espace : marquer ce que tu entends   ·   Q : enregistrer et fermer")
        else:
            d.drawText(x, h - 26,
                       f"source {self.isolee + 1} isolee   ·   "
                       "espace : maintenir = presence, taper = instants   ·   "
                       "retour arriere : defaire   ·   Q : enregistrer et fermer")

    def bandeau(self, d, p, x, y, largeur):
        d.setPen(GRIS_CLAIR)
        d.drawText(x, y, "emotion")
        if p.bpm > 0:
            self.dernier_bpm, self.bpm_frais = p.bpm, True
        else:
            self.bpm_frais = False
        d.setPen(VERT if self.bpm_frais else GRIS_CADRE)
        d.drawText(x + 90, y,
                   f"{self.dernier_bpm:6.1f} BPM" if self.dernier_bpm > 0 else "     — BPM")
        d.setPen(GRIS_TEXTE)
        # LA POSITION SE MESURE. Un décalage fixe a déjà fait déborder la mention du temps
        # fort hors du cadre ; ici il faisait chevaucher le motif sur l'horodatage.
        gauche = f"paquet {p.sequence}     temps {p.temps / 1000:7.1f} s"
        d.drawText(x + 220, y, gauche)
        apres = x + 220 + QFontMetricsF(self.mono).horizontalAdvance(gauche + "    ")

        # L'ETAT DE L'HORLOGE, PARCE QU'ELLE DECIDE DU KICK. Tant qu'elle n'est pas
        # verrouillee, le kick vient de la detection et arrive donc en retard de toute la
        # chaine ; verrouillee, il est predit. Ne pas le montrer rendrait indiscernables
        # deux comportements tres differents.
        # CE QUI SE REPETE, ET DEPUIS QUAND ON LE SAIT.
        #
        # « Dès la première écoute on a cet indice, puis quand on l'entend une deuxième fois
        # on sait que c'est un refrain. » La première fois, l'information n'existe pas
        # encore — le moteur rend zéro, et l'écran ne montre rien plutôt que d'inventer.
        if p.motif > 0:
            c = QColor(VERT)
            c.setAlphaF(min(1.0, 0.35 + p.motif_sur * 0.6))
            d.setPen(c)
            d.drawText(int(apres), y, f"motif {p.motif} mesures · bande {p.motif_bande}")

        etat = f"avance {self.avance_ms:3.0f} ms"
        if self.horloge.verrouille:
            etat += f"   horloge ● {self.horloge.ecart_ms:+5.1f} ms"
        else:
            etat += f"   horloge ○ {self.horloge.fiabilite:.2f}"
        if p.beat >= 4:
            etat += "   1 ?"
        # LA POSITION SE MESURE, ELLE NE SE DEVINE PAS. Un decalage fixe de 260 px suffisait
        # tant que le texte etait court ; l'etat de l'horloge l'a allonge et la mention du
        # temps fort sortait du cadre. C'est la troisieme fois dans ce fichier qu'une
        # largeur supposee coute un debordement.
        d.setPen(GRIS_CLAIR if self.horloge.verrouille else GRIS_CADRE)
        largeur_etat = QFontMetricsF(self.mono).horizontalAdvance(etat)
        d.drawText(int(x + largeur - largeur_etat), y, etat)
        return y

    def mesure(self, d, p, x, y, largeur):
        """La position dans la mesure, et LE CURSEUR NE RECULE JAMAIS.

        Il reculait, et c'est une bonne part de ce que le DJ prenait pour du buffering.
        `phase` est la position dans la mesure de quatre temps, et elle vaut zéro tant que
        le « 1 » n'est pas identifié — ce qui arrive quatre fois sur dix. Chaque fois que le
        vote du temps fort lâchait, le curseur sautait donc au début du bandeau, y restait,
        puis repartait d'un bond quand le vote revenait. Un repère qui saute est pire qu'un
        repère absent : l'œil suit le saut et perd la musique.

        `phase_temps`, elle, est toujours remplie — savoir où l'on en est du temps ne
        demande pas de savoir quel temps c'est. Faute de mesure, on affiche donc le temps
        seul : un quart du bandeau, parcouru sans interruption, et la teinte dit qu'on ne
        sait pas dans quelle mesure on se trouve.
        """
        cases = 32
        pas = largeur / cases
        sur_la_mesure = p.beat < 4
        ou = int(min(max(self.mesure_pos, 0.0), 0.999) * cases)

        for i in range(cases):
            fort = i % 8 == 0
            if i == ou:
                teinte = VERT if sur_la_mesure else VERT_SOURD
            else:
                teinte = GRIS_CADRE if fort else QColor(34, 36, 38)
            d.setPen(QPen(teinte, 1))
            hauteur = 14 if (fort or i == ou) else 7
            gx = int(x + i * pas)
            d.drawLine(gx, y, gx, y + hauteur)

        d.setPen(GRIS_TEXTE if sur_la_mesure else GRIS_CADRE)
        d.drawText(x + largeur - 70, y + 12,
                   f"temps {p.beat + 1}" if sur_la_mesure else "mesure ?")
        return y + 16

    def registres(self, d, p, x, y, largeur):
        """Les six sources, chacune dans sa case, avec sa forme.

        Le rang ne decide pas de la forme : le paquet porte un octet par source et c'est lui
        qui commande. L'ordre du grave a l'aigu n'est qu'un repli quand la fiche n'a rien dit.
        """
        cols, rangs = 3, 2
        larg = (largeur - 2 * 14) / cols
        haut = 132
        self.cases = []
        for r, s in enumerate(p.sources):
            cx = x + (r % cols) * (larg + 14)
            cy = y + (r // cols) * (haut + 12)
            self.cases.append((cx, cy, larg, haut))

            # ISOLER, C'EST ETEINDRE LES AUTRES — PAS LES EFFACER.
            #
            # Elles gardent leur place et leur mouvement, en sourdine : on veut pouvoir
            # verifier du coin de l'oeil qu'une voisine ne fait pas exactement la meme chose
            # que celle qu'on ecoute. C'est meme la question du moment, puisque deux sources
            # sur six portent presque le meme son.
            eteinte = self.isolee is not None and r != self.isolee
            choisie = self.isolee == r

            d.setPen(QPen(VERT if choisie else GRIS_CADRE, 1))
            d.drawRect(int(cx), int(cy), int(larg), haut)

            nom = formes.NOMS.get(s["forme"], formes.NOMS[(r % 6) + 1])
            d.setPen(GRIS_TEXTE)
            # DIRE LA NATURE DE LA SOURCE, PUISQUE C'EST ELLE QUI COMMANDE LE GESTE. Sans
            # cela, on voit un mouvement sans savoir s'il decrit un instrument qui frappe ou
            # un qui souffle — et un ecran qui montre autre chose que ce qui decide est pire
            # qu'aucun ecran.
            nature = ("pincé" if s["pique"] > 0.5 and s["tenue"] < 0.45 else
                      "frappé" if s["pique"] > 0.5 else
                      "tenu" if s["tenue"] > 0.6 else
                      "")
            gauche = f"{r + 1}·{s['nom']}" if s["nom"] else f"{r + 1}"
            titre = "  ".join(x for x in (gauche, nom, nature) if x)

            # UNE SOURCE RETIREE SE DIT, ELLE NE DISPARAIT PAS.
            # « Quand le kick est en retrait pendant un moment, on est censé le savoir. »
            # « ABSENT 0 S » NE VEUT RIEN DIRE, et c'est ce que l'arrondi affichait pour un
            # retrait de quatre dixièmes. Un retrait se compte en mesures — deux, avant
            # qu'on en parle — donc on ne l'annonce qu'une fois qu'il en vaut la peine, et
            # on l'arrondit à la seconde par le haut.
            absente = s["retrait"] > 0.4
            if absente:
                titre += f"  ⌁ absent {max(1, math.ceil(s['retrait']))} s"

            d.setPen(GRIS_CADRE if (absente or eteinte) else
                     GRIS_CLAIR if choisie else GRIS_TEXTE)
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
            # SUR LA CASE ISOLEE, LE COMPTE DES MARQUES PREND LA PLACE DU VERDICT.
            #
            # La place est deja reservee, donc rien ne bouge dans la geometrie — et pendant
            # qu'on annote, savoir que la touche est bien prise compte plus que de relire
            # « nette ». C'est le SEUL retour a l'ecran : on n'affiche aucun accord calcule,
            # parce que le trancher tout de suite obligerait a decider de la latence de la
            # main, qui n'est pas connue.
            if choisie:
                n = sum(1 for m in self.marques if m[0] == r)
                verdict = "tenue" if self.tenue else f"marques {n}"
                vif = True

            # ET LA POSITION SE MESURE, ELLE NE SE DEVINE PAS. Le decalage de 72 px etait
            # taille pour « ○ partagée » ; « marques 12 » est plus long et touchait le bord
            # de la case. C'est la QUATRIEME fois dans ce fichier qu'une largeur supposee
            # coute un debordement — on la mesure, comme partout ailleurs.
            d.setPen(GRIS_CADRE if eteinte else (GRIS_CLAIR if vif else GRIS_CADRE))
            large = QFontMetricsF(self.mono).horizontalAdvance(verdict)
            d.drawText(int(cx + larg - 8 - large), int(cy) + 16, verdict)

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
            # UNE SOURCE ABSENTE SE DESSINE EN CREUX, A SA PLACE.
            #
            # Elle ne joue plus, donc son niveau est nul et la forme s'effondrerait à rien.
            # Or ce qu'on veut montrer n'est pas son silence, c'est SA PLACE : là où elle
            # tombait et où elle retombera. On lui prête donc le battement de sa propre
            # place, et une teinte qui ne peut pas être confondue avec du son présent.
            if absente:
                niveau, frappe = 0.30, 0.0
            else:
                niveau, frappe = s["niveau"], self.coups[r].valeur

            lignes, force = formes.rendu(nom, g, niveau, s["hauteur"],
                                         frappe, self.tempo,
                                         s["pique"], s["tenue"])

            # ET L'ON DECOUPE, PAR-DESSUS TOUT LE RESTE. Les controles servent a comprendre,
            # le decoupage garantit : ce qui depasse n'est pas dessine, quelle qu'en soit la
            # cause. Le renderer web a mis trois tentatives a l'admettre.
            d.save()
            d.setClipRect(int(cx) + 1, int(cy) + 1, int(larg) - 2, haut - 2)
            d.setFont(police)
            teinte = QColor(GRIS_CADRE if absente else VERT)
            alpha = min(1.0, (0.35 + force * 0.45) if absente
                        else (0.22 + force * 0.70))
            teinte.setAlphaF(alpha * 0.30 if eteinte else alpha)
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

        # LE TIMBRE, LA OU LE GRAIN FAISAIT DOUBLON.
        #
        # Cette case montrait un semis nourri des registres aigus — c'est-a-dire exactement
        # ce que la source 6 montre deja, avec le meme dessin. Deux cases pour une seule
        # information, et le DJ l'a dit : « la source du grain est presentee deux fois ».
        #
        # Le timbre, lui, n'etait montre nulle part alors qu'il decide de choses visibles :
        # `ouverture` eteint le grain quand le filtre se ferme, `centroide` dit la couleur
        # du son, `densite` dit s'il est plein ou clairseme. On les montre donc, en jauges
        # couchees comme le spectre, puisque c'est la meme lecture — trois grandeurs qu'on
        # compare d'un coup d'oeil.
        cx = x + larg + 14
        self.timbre(d, p, cx, y, larg, haut)

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

    def timbre(self, d, p, x, y, larg, haut):
        """Trois jauges : la couleur du son, l'ouverture du filtre, la densite."""
        g = formes.Grille(x, y, larg, haut, 6)
        police = QFont(self.mono)
        police.setPixelSize(max(5, int(g.taille)))
        avance = QFontMetricsF(police).horizontalAdvance("M")
        g = formes.Grille(x, y, larg, haut, 6, avance=avance)

        d.setPen(QPen(GRIS_CADRE, 1))
        d.drawRect(int(x), int(y), int(larg), int(haut))
        d.setPen(GRIS_TEXTE)
        d.drawText(int(x) + 8, int(y) + 16, "TIMBRE")

        d.save()
        d.setClipRect(int(x) + 1, int(y) + 1, int(larg) - 2, int(haut) - 2)
        for i, (nom, v) in enumerate((("clair", p.brillance),
                                      ("ouvert", p.ouverture),
                                      ("dense", p.densite))):
            ligne = 2 * i + 1
            d.setFont(self.mono)
            d.setPen(GRIS_CADRE)
            d.drawText(int(g.x0), int(g.y0 + ligne * g.ch), f"{nom:6s}")
            d.setFont(police)
            c = QColor(VERT)
            c.setAlphaF(min(1.0, 0.20 + v * 0.7))
            d.setPen(c)
            largeur_nom = QFontMetricsF(self.mono).horizontalAdvance("xxxxxxx")
            reste = formes.Grille(x + largeur_nom, y, larg - largeur_nom, haut, 6,
                                  avance=avance)
            d.drawText(int(reste.x0), int(g.y0 + ligne * g.ch), formes.jauge(reste, v))
        d.restore()
        d.setFont(self.mono)

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

    # ------------------------------------------------------------------ annoter
    def journaliser(self, p):
        """Ce que le moteur publiait, image d'analyse par image d'analyse.

        SANS LUI, LE JOURNAL DES TOUCHES NE VAUDRAIT RIEN. Comparer ce que l'oreille marque
        a ce que la source fait suppose de connaitre les deux SUR LA MEME HORLOGE. Rejouer
        le morceau apres coup pour retrouver les frappes du moteur donnerait un alignement
        approximatif — et c'est justement l'alignement qu'on mesure.

        On n'enregistre que pendant qu'une source est isolee : le reste du temps il n'y a
        rien a confronter, et un journal de cinq minutes d'ecoute passive ne servirait qu'a
        peser.
        """
        if self.isolee is None:
            return
        s = p.sources[self.isolee]
        self.journal.append({
            "t": round(p.temps / 1000.0, 4),
            "niveau": round(s["niveau"], 3),
            "frappe": bool(s["frappe"]),
            "retrait": round(s["retrait"], 2),
            "pique": round(s["pique"], 3),
            "tenue": round(s["tenue"], 3),
            "bpm": round(p.bpm, 2),
            "phase_temps": round(p.phase_temps, 3),
            "kick": bool(p.kick),
        })

    def instant(self):
        """Ou l'on en est dans le morceau, en secondes. L'horloge du moteur, et elle seule.

        Un decalage entre deux horloges est le genre de defaut qui survit des semaines sans
        se voir. Il n'y en a qu'une ici, donc la question ne se pose pas.
        """
        return self.paquet.temps / 1000.0 if self.paquet else 0.0

    def isoler(self, r):
        """Choisir une source, ou revenir a la vue d'ensemble en la rechoisissant."""
        avant = self.isolee
        self.isolee = None if self.isolee == r else r
        if self.isolee != avant:
            self.tenue = None

    def marquer(self, appuye):
        """Espace enfonce, espace relache. On garde LES DEUX, toujours.

        DEUX GESTES, UN SEUL MECANISME. Maintenir une touche tant qu'on entend l'instrument
        donne un intervalle de presence, a confronter au niveau et au retrait ; taper en
        rythme donne des instants, a confronter aux frappes. Decider ici lequel des deux on
        vient de faire perdrait de l'information qu'on ne pourrait plus retrouver — seule la
        duree les separe, et elle n'est connue qu'apres le relachement.
        """
        if self.isolee is None:
            return
        if appuye:
            if self.tenue is None:
                self.tenue = (self.isolee, self.instant())
        elif self.tenue is not None:
            source, debut = self.tenue
            self.tenue = None
            self.marques.append((source, debut, self.instant()))

    def enregistrer(self):
        """Le rapport, hors du depot : il contient une oeuvre en creux.

        Les instants sont BRUTS, sans correction de latence. Une main tape apres avoir
        entendu, de cinquante a cent cinquante millisecondes selon la personne et le jour.
        Corriger ici enfouirait une hypothese dans une donnee, et une donnee corrigee par
        une hypothese fausse ne se repare plus. Le decalage se lit a l'analyse, ou il reste
        visible — et s'il est constant, c'est la main ; s'il part dans tous les sens, c'est
        le moteur.
        """
        if not self.marques and not self.journal:
            return
        dossier = os.path.expanduser("~/Documents/emotion-sources/rapports")
        os.makedirs(dossier, exist_ok=True)
        nom = f"{self.morceau}-{time.strftime('%Y%m%d-%H%M%S')}.json"
        chemin = os.path.join(dossier, nom)
        with open(chemin, "w", encoding="utf-8") as fh:
            json.dump({
                "morceau": self.morceau,
                "brut": True,
                "marques": [{"source": r + 1, "debut": round(d, 3), "fin": round(f, 3)}
                            for r, d, f in self.marques],
                "moteur": self.journal,
            }, fh, ensure_ascii=False)
        print(f"{len(self.marques)} marques et {len(self.journal)} images dans {chemin}")

    # ------------------------------------------------------------------ souris
    def mousePressEvent(self, e):
        pos = e.position() if hasattr(e, "position") else e.pos()
        for r, (cx, cy, larg, haut) in enumerate(self.cases):
            if cx <= pos.x() <= cx + larg and cy <= pos.y() <= cy + haut:
                self.isoler(r)
                return

    def closeEvent(self, e):
        self.enregistrer()
        e.accept()

    def keyReleaseEvent(self, e):
        if e.key() == Qt.Key.Key_Space and not e.isAutoRepeat():
            self.marquer(False)

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
        # LES TOUCHES 1 A 6 FONT CE QUE FAIT LE CLIC. On a rarement la souris en main quand
        # on ecoute, et changer de source ne doit pas couter un geste.
        elif Qt.Key.Key_1 <= e.key() <= Qt.Key.Key_6:
            self.isoler(e.key() - Qt.Key.Key_1)
        elif e.key() == Qt.Key.Key_Space and not e.isAutoRepeat():
            # L'AUTOREPEAT EST IGNORE. Le clavier renvoie l'enfoncement des dizaines de fois
            # par seconde tant qu'on tient la touche ; en tenir compte hacherait un souffle
            # en centaines de marques minuscules.
            self.marquer(True)
        elif e.key() == Qt.Key.Key_Backspace and self.marques:
            # ON PEUT SE REPRENDRE. Une marque fausse laissee dans le rapport vaut moins que
            # pas de marque du tout : elle s'y presente comme une verite et n'en est pas une.
            self.marques.pop()


def main():
    app = QApplication(sys.argv)
    mur = Mur()
    mur.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
