#!/usr/bin/env python3
"""Seconde fenêtre : ce que le morceau contient, face à ce que le moteur en publie.

POURQUOI DEUX FENÊTRES.

La première montre ce que le moteur dit, en direct. Elle ne peut pas dire s'il a raison —
un renderer affiche fidèlement ce qu'on lui donne, juste ou faux. Celle-ci pose la question
que l'autre ne peut pas poser : **est-ce que ce qui s'affiche correspond à ce que le disque
fait vraiment ?**

Elle lit deux choses qui ne viennent pas du moteur :

- le **rapport d'analyse** produit hors ligne par `tempo_reference.py`, en Python, avec ses
  propres fenêtres et ses propres méthodes ;
- le **tempo fiché dans le crate**, que le DJ a calé lui-même à l'oreille.

Et elle les confronte, seconde par seconde, à ce que l'anneau publie au même instant.

CE QU'ELLE VÉRIFIE, ET CE N'EST PAS QUE LE TEMPO. Le paquet transporte une quarantaine de
grandeurs ; se contenter du tempo laisserait passer tout le reste. On compare donc aussi
l'énergie, les douze bandes, la brillance et la densité d'attaques — tout ce qui se dérive
du seul signal sans demander un choix perceptif.

CE QU'ELLE NE VÉRIFIE PAS, ET IL FAUT LE DIRE. La séparation en six sources par timbre
n'est pas refaite ici : la contrôler demanderait de réécrire la factorisation, donc de
vérifier le moteur avec le moteur. Les registres grave, médium et aigu sont des tranches de
spectre, pas des instruments — ils disent qu'il se passe quelque chose dans ce registre,
pas qu'un saxophone joue.

    python3 outils/tempo_reference.py morceau.wav rapport=reference/
    python3 outils/fenetre_reference.py reference/morceau.json 63.5

Le second argument est le tempo du crate. Sans lui, on affiche celui de l'analyse Python —
qui se trompe encore d'octave sur certains morceaux, et le dit.
"""

import json
import sys

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QFont, QPainter, QPen
from PySide6.QtWidgets import QApplication, QWidget

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from fenetre import Anneau, GRIS_CADRE, GRIS_CLAIR, GRIS_FOND, GRIS_TEXTE, VERT, VERT_SOURD

# Le désaccord se dessine en gris clair, jamais en rouge : la charte du DJ n'en veut pas,
# et un écart n'est pas une erreur — c'est une chose à regarder.
GRIS_ECART = QColor(150, 150, 155)


# Les rapports qu'on accepte de reconnaitre entre deux tempos.
#
# UNE OCTAVE N'EST PAS UNE ERREUR DE LECTURE. Annoncer 127,8 quand la fiche dit 63,50, c'est
# avoir trouve le bon pouls et l'avoir compte un niveau metrique plus haut — les frappes
# tombent aux memes endroits, il y en a simplement deux fois plus. Choisir lequel des
# niveaux est « le temps » est un choix perceptif, que le crate tranche et qu'aucun
# algorithme ne tranchera seul. L'afficher comme cent pour cent d'ecart serait mentir sur
# la nature du desaccord.
RAPPORTS = ((0.25, "le quart"), (1 / 3, "le tiers"), (0.5, "la moitie"),
            (2 / 3, "deux tiers"), (1.0, "le meme"), (1.5, "une fois et demie"),
            (2.0, "le double"), (3.0, "le triple"), (4.0, "le quadruple"))


def accord_tempo(vrai, publie):
    """Rend l'ecart au rapport le plus proche, et le nom de ce rapport."""
    if not vrai or not publie:
        return 0.0, None
    meilleur = None
    for facteur, nom in RAPPORTS:
        cible = vrai * facteur
        ecart = abs(publie - cible) / cible * 100
        if meilleur is None or ecart < meilleur[0]:
            meilleur = (ecart, nom)
    return meilleur


class Comparateur(QWidget):
    def __init__(self, rapport, bpm_crate=None):
        super().__init__()
        self.rapport = rapport
        self.bpm_crate = bpm_crate
        self.secondes = rapport.get("secondes", [])
        self.duree = max(1.0, rapport.get("duree", 1.0))

        self.setWindowTitle(f"attendu — {rapport.get('fichier', '?')}")
        self.resize(1180, 620)

        self.anneau = Anneau()
        self.paquet = None

        self.mono = QFont("monospace", 10)
        self.mono.setStyleHint(QFont.StyleHint.Monospace)

        self.minuterie = QTimer(self)
        self.minuterie.timeout.connect(self.battre)
        self.minuterie.start(50)          # vingt images par seconde suffisent pour comparer

    def battre(self):
        self.paquet = self.anneau.dernier()
        self.update()

    def instant(self):
        """Où l'on en est dans le morceau, en secondes.

        L'horloge de l'anneau compte depuis le démarrage du moteur, pas depuis le début du
        disque. Tant que le morceau tourne en boucle depuis le lancement, le modulo suffit ;
        dès qu'on posera une aiguille au milieu d'une face, il faudra que la fiche porte la
        position. On l'affiche donc comme une approximation et non comme une vérité.
        """
        if self.paquet is None:
            return 0.0
        return (self.paquet.temps / 1000.0) % self.duree

    def attendu(self, t):
        if not self.secondes:
            return None
        i = min(int(t), len(self.secondes) - 1)
        return self.secondes[i]

    # ------------------------------------------------------------------ dessin
    def paintEvent(self, _):
        d = QPainter(self)
        d.fillRect(self.rect(), GRIS_FOND)
        d.setFont(self.mono)
        x, largeur = 24, self.width() - 48

        if self.paquet is None:
            d.setPen(GRIS_TEXTE)
            d.drawText(x, 40, "anneau vide — lancer le serveur, puis relancer cette fenetre")
            return

        t = self.instant()
        att = self.attendu(t)
        p = self.paquet

        y = self.entete(d, x, 34, largeur, t)
        y = self.tempo(d, x, y + 28, largeur, p)
        y = self.chronologie(d, x, y + 30, largeur, t)
        y = self.paire(d, x, y + 34, largeur, "energie",
                       att["rms"] * 3.0 if att else 0.0, p.niveau)
        y = self.paire(d, x, y + 26, largeur, "brillance",
                       att["brillance"] if att else 0.0, p.brillance)
        y = self.paire(d, x, y + 26, largeur, "densite",
                       min(1.0, att["attaques"] / 8.0) if att else 0.0, p.densite)
        self.bandes(d, x, y + 32, largeur, att, p)

    def entete(self, d, x, y, largeur, t):
        d.setPen(GRIS_CLAIR)
        d.drawText(x, y, f"attendu   {self.rapport.get('fichier', '?')}")
        d.setPen(GRIS_TEXTE)
        d.drawText(x + 340, y, f"~{t:5.1f} s sur {self.duree:.0f}  (position approchee)")
        return y

    def tempo(self, d, x, y, largeur, p):
        """Le tempo est la seule grandeur dont la vérité vient du crate, pas du signal."""
        if self.bpm_crate:
            vrai, source = self.bpm_crate, "crate"
        else:
            vrai, source = self.rapport.get("median", 0.0), "analyse python"

        publie = p.bpm
        ecart, relation = accord_tempo(vrai, publie)

        d.setPen(GRIS_TEXTE)
        d.drawText(x, y, f"tempo  {source}")
        d.setPen(GRIS_CLAIR)
        d.drawText(x + 200, y, f"{vrai:6.2f}")
        d.setPen(GRIS_TEXTE)
        d.drawText(x + 290, y, "moteur")
        d.setPen(VERT if relation == "le meme" else GRIS_ECART)
        d.drawText(x + 380, y, f"{publie:6.2f}" if publie else "     —")
        if relation:
            d.setPen(GRIS_TEXTE if relation == "le meme" else GRIS_ECART)
            d.drawText(x + 470, y, f"{relation}   a {ecart:4.1f} %")
        return y

    def chronologie(self, d, x, y, largeur, t):
        """L'énergie du morceau entier, avec un curseur là où l'on en est."""
        haut = 48
        n = len(self.secondes)
        if n == 0:
            return y + haut
        pic = max((s["rms"] for s in self.secondes), default=1.0) or 1.0
        pas = largeur / n
        for i, s in enumerate(self.secondes):
            h = int(s["rms"] / pic * haut)
            gx = int(x + i * pas)
            d.setPen(QPen(VERT_SOURD, max(1, int(pas))))
            d.drawLine(gx, y + haut, gx, y + haut - h)
        cx = int(x + min(t, n - 1) / n * largeur)
        d.setPen(QPen(VERT, 2))
        d.drawLine(cx, y - 4, cx, y + haut + 4)
        d.setPen(GRIS_TEXTE)
        d.drawText(x, y - 8, "energie du morceau entier")
        return y + haut

    def paire(self, d, x, y, largeur, nom, valeur_attendue, valeur_publiee):
        """Deux barres l'une sous l'autre : ce qu'on attend, ce que le moteur publie."""
        util = largeur - 200
        d.setPen(GRIS_TEXTE)
        d.drawText(x, y + 4, nom)

        gx = x + 110
        for rang, (etiquette, v, couleur) in enumerate(
                (("attendu", valeur_attendue, GRIS_CLAIR), ("moteur", valeur_publiee, VERT))):
            yy = y + rang * 11
            d.setPen(QPen(QColor(28, 30, 32), 1))
            d.drawLine(gx, yy, gx + util, yy)
            n = int(max(0.0, min(1.0, v)) * util)
            if n > 0:
                d.setPen(QPen(couleur, 5))
                d.drawLine(gx, yy, gx + n, yy)
            d.setPen(GRIS_TEXTE)
            d.drawText(gx + util + 12, yy + 4, f"{etiquette} {v:4.2f}")
        return y + 11

    def bandes(self, d, x, y, largeur, att, p):
        """Les douze bandes, attendues et publiées, côte à côte.

        Les bornes sont exactement celles du moteur — trente hertz à seize kilohertz en
        octaves — sans quoi la comparaison bande par bande n'aurait aucun sens.
        """
        d.setPen(GRIS_TEXTE)
        d.drawText(x, y - 8, "douze bandes   clair = attendu   vert = moteur")
        haut = 60
        pas = largeur / 12
        for i in range(12):
            gx = int(x + i * pas)
            a = att["bandes"][i] if att else 0.0
            m = p.bandes[i]
            d.setPen(QPen(QColor(26, 28, 30), 1))
            d.drawLine(gx, y + haut, gx, y)
            d.setPen(QPen(GRIS_CLAIR, 5))
            d.drawLine(gx - 3, y + haut, gx - 3, y + haut - int(a * haut))
            d.setPen(QPen(VERT, 5))
            d.drawLine(gx + 4, y + haut, gx + 4, y + haut - int(m * haut))
        d.setPen(GRIS_CADRE)
        d.drawLine(x, y + haut, x + largeur, y + haut)

    def keyPressEvent(self, e):
        if e.key() in (Qt.Key.Key_Q, Qt.Key.Key_Escape):
            self.close()


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    with open(sys.argv[1], encoding="utf-8") as fh:
        rapport = json.load(fh)
    bpm = float(sys.argv[2]) if len(sys.argv) > 2 else None

    app = QApplication(sys.argv[:1])
    w = Comparateur(rapport, bpm)
    w.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
