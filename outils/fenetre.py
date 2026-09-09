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

import mmap
import os
import struct
import sys

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QFont, QFontMetricsF, QPainter, QPen
from PySide6.QtWidgets import QApplication, QWidget

sys.path.insert(0, __file__.rsplit("/", 1)[0])
import formes


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
P_BPM_ATTENDU = 192
P_SOURCES = 128        # huit mots de huit octets
SOURCE_PAS = 8
S_NIVEAU, S_HAUTEUR, S_DRAPEAUX, S_NETTETE, S_FORME = 0, 1, 2, 5, 7

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
        self.bandes = [mm[base + P_BANDES + i] / 255.0 for i in range(12)]
        self.sources = []
        for r in range(6):
            o = base + P_SOURCES + r * SOURCE_PAS
            self.sources.append({
                "niveau": mm[o + S_NIVEAU] / 255.0,
                "hauteur": mm[o + S_HAUTEUR] / 255.0,
                "frappe": bool(mm[o + S_DRAPEAUX] & 1),
                "nettete": mm[o + S_NETTETE] / 255.0,
                "forme": mm[o + S_FORME],
            })


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
        self.resize(1180, 620)
        self.setAutoFillBackground(False)

        self.anneau = Anneau()
        self.paquet = None
        self.derniere_sequence = -1

        self.kick = Pulse(3.2)
        self.clap = Pulse(4.5)
        self.charley = Pulse(7.0)
        self.coups = [Pulse(4.0) for _ in range(6)]

        # L'onde defile : elle a besoin d'une horloge, pas d'un evenement.
        self.tempo = 0.0

        self.mono = QFont("monospace", 10)
        self.mono.setStyleHint(QFont.StyleHint.Monospace)

        self.minuterie = QTimer(self)
        self.minuterie.timeout.connect(self.battre)
        self.minuterie.start(16)          # environ soixante images par seconde

    def battre(self):
        p = self.anneau.dernier()
        if p is not None:
            self.paquet = p
            # Une impulsion par paquet, jamais par image de rendu.
            if p.sequence != self.derniere_sequence:
                self.derniere_sequence = p.sequence
                if p.kick:
                    self.kick.tirer()
                if p.clap:
                    self.clap.tirer()
                if p.charley:
                    self.charley.tirer()
                for r, s in enumerate(p.sources):
                    if s["frappe"]:
                        self.coups[r].tirer()

        dt = 1.0 / 60.0
        self.tempo += dt
        for imp in (self.kick, self.clap, self.charley, *self.coups):
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
        y = self.bandeau(d, p, marge, 34, w - 2 * marge)
        y = self.mesure(d, p, marge, y + 26, w - 2 * marge)
        y = self.registres(d, p, marge, y + 30, w - 2 * marge)
        y = self.spectre(d, p, marge, y + 26, w - 2 * marge)
        self.frappes(d, marge, y + 26, w - 2 * marge)

    def bandeau(self, d, p, x, y, largeur):
        d.setPen(GRIS_CLAIR)
        d.drawText(x, y, "emotion")
        d.setPen(VERT)
        d.drawText(x + 90, y, f"{p.bpm:6.1f} BPM" if p.bpm > 0 else "     — BPM")
        d.setPen(GRIS_TEXTE)
        d.drawText(x + 220, y, f"paquet {p.sequence}     temps {p.temps / 1000:7.1f} s")
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
            d.drawText(int(cx) + 8, int(cy) + 16, f"{r + 1}  {nom}")
            d.setPen(GRIS_CLAIR if s["nettete"] > 0.6 else GRIS_CADRE)
            d.drawText(int(cx + larg) - 52, int(cy) + 16,
                       "nette" if s["nettete"] > 0.6 else "....")

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

    def spectre(self, d, p, x, y, largeur):
        """Douze jauges couchées, une par bande."""
        pas = largeur / 12
        for i, v in enumerate(p.bandes):
            gx = int(x + i * pas)
            haut = int(v * 46)
            d.setPen(QPen(QColor(26, 28, 30), 1))
            d.drawLine(gx, y + 46, gx, y)
            if haut > 0:
                d.setPen(QPen(VERT if v > 0.66 else VERT_SOURD, 6))
                d.drawLine(gx, y + 46, gx, y + 46 - haut)
        return y + 46

    def frappes(self, d, x, y, largeur):
        for nom, imp in (("kick", self.kick), ("clap", self.clap), ("charley", self.charley)):
            d.setPen(GRIS_TEXTE)
            d.drawText(x, y + 10, nom)
            gx = x + 78
            util = largeur - 100
            d.setPen(QPen(QColor(28, 30, 32), 1))
            d.drawLine(gx, y + 6, gx + util, y + 6)
            n = int(imp.valeur * util)
            if n > 0:
                d.setPen(QPen(VERT, 5))
                d.drawLine(gx, y + 6, gx + n, y + 6)
            y += 22

    def keyPressEvent(self, e):
        if e.key() in (Qt.Key.Key_Q, Qt.Key.Key_Escape):
            self.close()


def main():
    app = QApplication(sys.argv)
    mur = Mur()
    mur.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
