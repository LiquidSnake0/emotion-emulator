#!/usr/bin/env python3
"""Ce que l'oreille entend, noté à la main, sur la même horloge que le moteur.

POURQUOI CET OUTIL EXISTE.

Tout ce que ce projet mesure se juge contre des grandeurs qu'il calcule lui-même. La seule
chose qui manque, et qui manque depuis le début, c'est un avis extérieur sur CE QU'ON ENTEND :
aucun outil ne peut dire si la source qui prétend suivre le piano suit bien le piano.

L'idée est du DJ : « j'appuie une touche quand j'entends un souffle ; si c'est un pizzicato,
je taperai en rythme, et on comparera ça pour être sûr que chaque cellule cadre bien. »

DEUX GESTES, UN SEUL MÉCANISME.

  MAINTENIR une touche tant qu'on entend l'instrument → des intervalles de présence, à
  confronter au niveau et au retrait que la source publie.

  TAPER une touche en rythme → des instants, à confronter aux frappes et à la grille.

On enregistre donc toujours l'enfoncement ET le relâchement, et c'est l'analyse qui décide
de les lire comme un intervalle ou comme un instant. Décider ici perdrait de l'information
qu'on ne pourrait plus retrouver.

L'HORLOGE EST CELLE DU MORCEAU, ET NON CELLE DE LA MACHINE.

Chaque touche est datée sur le temps que le moteur publie dans l'anneau — le même que celui
de ses propres mesures. Un décalage entre deux horloges est le genre de défaut qui survit
des semaines sans se voir ; ici la question ne se pose pas, il n'y en a qu'une.

    ./run.sh pulse                       (ou voir.sh, le moteur doit tourner)
    python3 outils/taper.py sortie.json  puis on joue le morceau et l'on tape

LA LATENCE DE LA MAIN SE MESURE, ELLE NE SE SUPPOSE PAS.

Une main tape APRÈS avoir entendu, de cinquante à cent cinquante millisecondes selon la
personne et le jour. Ce retard est systématique : il ne gêne pas une mesure de PÉRIODE, mais
il fausse toute mesure de PHASE, et c'est justement la phase qui manque au projet.

Il se mesure en une minute et demie, sans rien supposer : on tape sur `etalon-kick.wav`,
dont les clics sont dans le fichier et se relèvent à l'échantillon près. L'écart médian entre
la main et les clics EST la latence, et on la retranche ensuite de tout le reste. Voir
`outils/latence_main.py`.
"""

import json
import os
import sys
import time

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QFont, QPainter
from PySide6.QtWidgets import QApplication, QWidget

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fenetre

GRIS_FOND = QColor(14, 14, 15)
GRIS_TEXTE = QColor(122, 126, 130)
GRIS_CLAIR = QColor(198, 202, 206)
VERT = QColor(64, 196, 122)
VERT_SOURD = QColor(38, 110, 74)

# Les touches qu'on écoute, et ce qu'on leur fait dire. Une par doigt de la main gauche,
# pour qu'on puisse en tenir plusieurs sans regarder le clavier.
TOUCHES = {
    Qt.Key.Key_A: "a", Qt.Key.Key_Z: "z", Qt.Key.Key_E: "e",
    Qt.Key.Key_R: "r", Qt.Key.Key_T: "t", Qt.Key.Key_Y: "y",
    Qt.Key.Key_Space: "espace",
}


class Taper(QWidget):
    """Une fenêtre qui ne montre presque rien : elle écoute le clavier et l'anneau."""

    def __init__(self, sortie):
        super().__init__()
        self.setWindowTitle("taper — la vérité de l'oreille")
        self.resize(760, 420)
        self.sortie = sortie

        self.anneau = fenetre.Anneau()
        self.paquet = None
        self.enfoncees = {}          # touche -> instant du morceau où elle a été enfoncée
        self.notes = []              # (touche, debut, fin)

        self.mono = QFont("monospace", 11)
        self.mono.setStyleHint(QFont.StyleHint.Monospace)

        self.minuterie = QTimer(self)
        self.minuterie.timeout.connect(self.battre)
        self.minuterie.start(16)

    def instant(self):
        """Où l'on en est dans le morceau, en secondes. Zéro si le moteur ne dit rien."""
        return self.paquet.temps / 1000.0 if self.paquet else 0.0

    def battre(self):
        p = self.anneau.dernier()
        if p is not None:
            self.paquet = p
        self.update()

    # ------------------------------------------------------------------ clavier
    def keyPressEvent(self, e):
        if e.key() in (Qt.Key.Key_Q, Qt.Key.Key_Escape):
            self.enregistrer()
            self.close()
            return
        if e.key() == Qt.Key.Key_Backspace and self.notes:
            # ON PEUT SE REPRENDRE. Une note fausse laissée dans le fichier vaut moins que
            # pas de note du tout : elle se présente comme une vérité et n'en est pas une.
            self.notes.pop()
            return

        nom = TOUCHES.get(e.key())
        # AUTOREPEAT IGNORÉ. Le clavier renvoie l'enfoncement des dizaines de fois par
        # seconde tant qu'on tient la touche ; en tenir compte hacherait un souffle en
        # centaines de notes minuscules.
        if nom and not e.isAutoRepeat() and nom not in self.enfoncees:
            self.enfoncees[nom] = self.instant()

    def keyReleaseEvent(self, e):
        nom = TOUCHES.get(e.key())
        if nom and not e.isAutoRepeat() and nom in self.enfoncees:
            debut = self.enfoncees.pop(nom)
            self.notes.append((nom, debut, self.instant()))

    def closeEvent(self, e):
        self.enregistrer()
        e.accept()

    def enregistrer(self):
        """Écrit ce qui a été tapé. Les instants sont bruts, sans correction de latence.

        SANS CORRECTION, ET C'EST DÉLIBÉRÉ. La latence de la main se mesure à part et se
        retranche à l'analyse. Corriger ici enfouirait une hypothèse dans une donnée, et une
        donnée corrigée par une hypothèse fausse ne se répare plus.
        """
        if not self.notes:
            return
        data = {
            "fichier": os.path.basename(self.sortie),
            "notes": [{"touche": t, "debut": round(d, 3), "fin": round(f, 3)}
                      for t, d, f in self.notes],
            "brut": True,
        }
        with open(self.sortie, "w", encoding="utf-8") as fh:
            json.dump(data, fh, ensure_ascii=False, indent=1)
        print(f"{len(self.notes)} notes ecrites dans {self.sortie}")

    # ------------------------------------------------------------------ dessin
    def paintEvent(self, _):
        d = QPainter(self)
        d.fillRect(self.rect(), GRIS_FOND)
        d.setFont(self.mono)

        if self.paquet is None:
            d.setPen(GRIS_TEXTE)
            d.drawText(24, 40, "anneau vide — lancer le moteur, puis relancer cette fenetre")
            return

        d.setPen(GRIS_CLAIR)
        d.drawText(24, 40, f"morceau  {self.instant():7.1f} s")
        d.setPen(GRIS_TEXTE)
        d.drawText(24, 66, f"{len(self.notes)} notes    "
                           f"{self.paquet.bpm:5.1f} BPM" if self.paquet.bpm > 0
                   else f"{len(self.notes)} notes")

        # Les touches, et celles qu'on tient en ce moment.
        y = 120
        d.drawText(24, y, "a z e r t y  ·  espace")
        x = 24
        for nom in ("a", "z", "e", "r", "t", "y"):
            tenue = nom in self.enfoncees
            d.setPen(VERT if tenue else QColor(40, 42, 44))
            d.drawText(x, y + 34, "█" if tenue else "·")
            x += 26
        d.setPen(VERT if "espace" in self.enfoncees else QColor(40, 42, 44))
        d.drawText(x + 26, y + 34, "████")

        # Les dernières notes, pour qu'on voie ce qu'on vient de faire.
        d.setPen(VERT_SOURD)
        for i, (t, deb, fin) in enumerate(self.notes[-8:]):
            duree = fin - deb
            forme = "tenue" if duree > 0.25 else "tapee"
            d.drawText(24, y + 90 + i * 20,
                       f"{t:7s} {deb:7.2f} s   {1000*duree:6.0f} ms   {forme}")

        d.setPen(GRIS_TEXTE)
        d.drawText(24, self.height() - 30,
                   "maintenir = presence   ·   taper = instant   ·   "
                   "retour arriere = defaire   ·   Q = enregistrer et fermer")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    app = QApplication(sys.argv[:1])
    w = Taper(sys.argv[1])
    w.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
