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
import os
import sys
import urllib.request

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QColor, QFont, QPainter, QPen
from PySide6.QtWidgets import (QApplication, QHBoxLayout, QLabel, QLineEdit,
                               QListWidget, QVBoxLayout, QWidget)

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
    def __init__(self, rapport=None, bpm_crate=None):
        super().__init__()
        self.charger(rapport, bpm_crate)
        self.setMinimumWidth(760)

        self.anneau = Anneau()
        self.paquet = None

        self.mono = QFont("monospace", 10)
        self.mono.setStyleHint(QFont.StyleHint.Monospace)

        self.minuterie = QTimer(self)
        self.minuterie.timeout.connect(self.battre)
        self.minuterie.start(50)          # vingt images par seconde suffisent pour comparer

    def charger(self, rapport, bpm_crate=None):
        """Change de morceau sans rien redemarrer.

        Le rapport peut manquer : on affiche alors ce que le moteur publie, sans rien a
        confronter. Mieux vaut le dire que de laisser croire a un accord.
        """
        self.rapport = rapport or {}
        self.bpm_crate = bpm_crate
        self.secondes = self.rapport.get("secondes", [])
        self.duree = max(1.0, self.rapport.get("duree", 1.0))
        self.update()

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
        y = self.bandes(d, x, y + 32, largeur, att, p)
        self.sources(d, x, y + 34, largeur, att, p)

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
        return y + haut

    def sources(self, d, x, y, largeur, att, p):
        """Les six sources du moteur, face aux six regions du spectre.

        C'EST UN PROXY, ET IL EST ETIQUETE COMME TEL. Les sources viennent d'une
        factorisation par timbre qu'on ne refait pas ici : une NMF s'initialise au hasard,
        donc deux implementations correctes ne rendent pas les memes composantes ni dans le
        meme ordre. Mais les sources sont ordonnees du grave a l'aigu, et les six regions
        aussi. Ce qui se verifie : quand une region s'allume, la forme correspondante
        s'allume-t-elle. Ce qui ne se verifie pas : si un instrument donne est bien separe.
        """
        d.setPen(GRIS_TEXTE)
        d.drawText(x, y - 8, "six sources   clair = region du spectre (approche)   vert = moteur")
        pas = largeur / 6
        regions = att.get("regions", [0.0] * 6) if att else [0.0] * 6
        for r in range(6):
            gx = int(x + r * pas)
            a = regions[r] if r < len(regions) else 0.0
            m = p.sources[r]["niveau"]
            d.setPen(QPen(QColor(26, 28, 30), 1))
            d.drawLine(gx, y + 40, gx + int(pas) - 20, y + 40)
            larg_barre = int(pas) - 24
            d.setPen(QPen(GRIS_CLAIR, 6))
            d.drawLine(gx, y + 32, gx + int(a * larg_barre), y + 32)
            d.setPen(QPen(VERT, 6))
            d.drawLine(gx, y + 44, gx + int(m * larg_barre), y + 44)
            d.setPen(GRIS_TEXTE)
            d.drawText(gx, y + 62, f"{r + 1}")

    def keyPressEvent(self, e):
        if e.key() in (Qt.Key.Key_Q, Qt.Key.Key_Escape):
            self.close()


class Selecteur(QWidget):
    """La liste du crate, a gauche du comparateur.

    LE CRATE EST LA PREMIERE SOURCE D'INFORMATION, ET IL COMMANDE DEPUIS ICI. Choisir une
    face fait deux choses : elle envoie la fiche au moteur par l'API REST — le seul reseau
    legitime de cette architecture — et elle charge le precalcul correspondant s'il existe.
    Le moteur amorce alors son tempo sur cette fiche sans jamais s'y verrouiller.
    """

    def __init__(self, faces, dossier, comparateur):
        super().__init__()
        self.faces = faces
        self.dossier = dossier
        self.comparateur = comparateur

        self.filtre = QLineEdit()
        self.filtre.setPlaceholderText("filtrer par titre, album ou tempo")
        self.filtre.textChanged.connect(self.remplir)

        self.liste = QListWidget()
        self.liste.currentRowChanged.connect(self.choisir)

        self.etat = QLabel("")
        self.etat.setWordWrap(True)

        colonne = QVBoxLayout()
        colonne.setContentsMargins(10, 10, 6, 10)
        colonne.addWidget(self.filtre)
        colonne.addWidget(self.liste, 1)
        colonne.addWidget(self.etat)
        gauche = QWidget()
        gauche.setLayout(colonne)
        gauche.setMaximumWidth(330)

        rangee = QHBoxLayout(self)
        rangee.setContentsMargins(0, 0, 0, 0)
        rangee.addWidget(gauche)
        rangee.addWidget(comparateur, 1)

        self.setStyleSheet(
            "QWidget { background: #0e0e0f; color: #c6cace; }"
            "QLineEdit { background: #17181a; border: 1px solid #303234; padding: 4px; }"
            "QListWidget { background: #131415; border: 1px solid #303234; }"
            "QListWidget::item:selected { background: #1d3b2b; color: #40c47a; }"
            "QLabel { color: #7a7e82; }")

        self.visibles = []
        self.remplir()

    def remplir(self):
        motif = self.filtre.text().strip().lower()
        self.visibles = [f for f in self.faces
                         if not motif or motif in f["cle"]]
        self.liste.blockSignals(True)
        self.liste.clear()
        for f in self.visibles:
            self.liste.addItem(f"{f['bpm']:6.2f}  {f['titre'][:26]}")
        self.liste.blockSignals(False)

    def choisir(self, rang):
        if rang < 0 or rang >= len(self.visibles):
            return
        f = self.visibles[rang]
        rapport = self.rapport_de(f)
        self.comparateur.charger(rapport, f["bpm"])

        envoye = poser_fiche(f)
        dit = "fiche envoyee au moteur" if envoye else "moteur injoignable — fiche non posee"
        trouve = ("precalcul charge"
                  if rapport else
                  f"aucun precalcul — attendu {ardoise(f['titre'])}.json")
        self.etat.setText(f"{f['titre']}\n{f['album']}\n{dit}\n{trouve}")

    def rapport_de(self, face):
        """Le precalcul d'une face, s'il a ete produit.

        ON APPARIE PAR LE TITRE, ET PLUS PAR LE NUMERO DE PISTE.
        
        La convention precedente cherchait `t05.json` pour la cinquieme piste — de
        n'importe quel album. Le bac en compte plusieurs : selectionner la piste cinq d'un
        autre disque chargeait donc le precalcul de celui-ci et l'affichait comme la
        verite. Un rapprochement faux presente comme une reference est pire que pas de
        reference du tout : on croit mesurer un ecart alors qu'on compare deux morceaux
        differents.
        """
        if not self.dossier:
            return None
        chemin = os.path.join(self.dossier, ardoise(face["titre"]) + ".json")
        if os.path.exists(chemin):
            with open(chemin, encoding="utf-8") as fh:
                return json.load(fh)
        return None


def poser_fiche(face, hote="http://localhost:5099"):
    """Envoie la fiche au moteur par l'API REST.

    C'EST LE SEUL RESEAU DE CETTE ARCHITECTURE, ET IL EST LEGITIME. Le crate parle au moteur
    en HTTP ; le moteur parle au rendu par memoire partagee. L'un porte une intention, l'autre
    un flux — ils n'ont pas les memes contraintes et n'ont jamais eu a passer par le meme
    canal.

    L'echec est silencieux et rapporte : le moteur peut ne pas tourner, et la fenetre de
    mesure doit rester utilisable pour lire un precalcul sans lui.
    """
    corps = json.dumps({
        "title": face["titre"], "disc": face["album"], "side": face.get("face") or "A",
        "camelot": face.get("camelot") or "", "family": face.get("famille") or "M",
        "colorHex": "#4a7c59", "coverUrl": None, "bpm": face["bpm"],
    }).encode()
    requete = urllib.request.Request(f"{hote}/deck/play", data=corps,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(requete, timeout=1.5) as r:
            return 200 <= r.status < 300
    except Exception:                                  # noqa: BLE001
        return False


def ardoise(titre):
    """Le nom de fichier d'un titre : minuscules, seuls les mots, relies par des tirets."""
    mots = "".join(c.lower() if c.isalnum() else " " for c in titre).split()
    return "-".join(mots) or "sans-titre"


def lire_crate(chemin):
    """Les faces du crate, prêtes à être listées."""
    with open(chemin, encoding="utf-8") as fh:
        brut = json.load(fh)
    faces = []
    for t in brut:
        bpm = t.get("bpm") or 0
        if not bpm:
            continue
        titre = t.get("title") or "sans titre"
        album = t.get("album") or ""
        faces.append({
            "titre": titre, "album": album, "bpm": float(bpm),
            "piste": int(t.get("trackNumber") or 0),
            "camelot": t.get("key"), "famille": t.get("family"), "face": t.get("side"),
            "cle": f"{titre} {album} {bpm}".lower(),
        })
    faces.sort(key=lambda f: (f["album"], f["piste"]))
    return faces


CRATE = os.path.expanduser("~/Documents/crate/src/data/seed.json")


def main():
    dossier = sys.argv[1] if len(sys.argv) > 1 else None
    crate = sys.argv[2] if len(sys.argv) > 2 else CRATE

    app = QApplication(sys.argv[:1])
    comparateur = Comparateur()
    if os.path.exists(crate):
        faces = lire_crate(crate)
        fenetre = Selecteur(faces, dossier, comparateur)
        fenetre.setWindowTitle(f"mesure — {len(faces)} faces du crate")
    else:
        fenetre = comparateur
        fenetre.setWindowTitle("mesure — crate introuvable")
    fenetre.resize(1180, 620)
    fenetre.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
