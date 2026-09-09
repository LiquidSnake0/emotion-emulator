#!/usr/bin/env python3
"""Les six formes, portées du renderer web vers Qt.

POURQUOI CE PORTAGE.

Le rendu passait par un navigateur, et le navigateur n'est pas la cible : l'unité de rendu
lira les mêmes 256 octets sur PCIe ou USB-C, sans qu'aucun réseau ne s'interpose. Tout ce
que le JavaScript savait faire doit donc exister ici avant qu'on puisse le supprimer.

Le portage est fidèle : mêmes rayons, mêmes seuils, mêmes caractères. Une forme qui change
d'aspect en changeant de langage ne prouverait rien sur le contrat.

LA CHARTE : du gris, un seul accent vert, aucun rouge.

CE QUE CHAQUE FORME DIT.

  anneau   respire — une masse qui enfle et retombe
  onde     ondule — un mouvement continu qui traverse
  levres   s'ouvre — une bouche, pour ce qui chante
  losange  pulse — des arêtes droites, franches
  etoile   éclate — scintille sur l'attaque
  grain    scintille — un semis, pour ce qui n'a pas de contour

Le rang ne décide pas : le paquet porte un octet par source, et c'est lui qui commande.
L'ordre du grave à l'aigu n'est qu'un repli quand la fiche n'a rien dit.
"""

import math

from PySide6.QtGui import QFontMetricsF

NOMS = {1: "anneau", 2: "onde", 3: "levres", 4: "losange", 5: "etoile", 6: "grain"}

# Ce que chaque forme occupe verticalement, en fraction de la demi-hauteur. Sert à lui
# réserver sa place AVANT de la déplacer : couper un motif qui déborde le mutile, lui
# laisser sa place le garde entier.
ENCOMBREMENT = {
    "anneau": 1.7, "onde": 1.3, "levres": 0.4,
    "losange": 0.9, "etoile": 0.6, "grain": 0.0,
}

# Tous les caractères employés. Ils doivent tous avoir la même chasse, sans quoi une ligne
# entière dérive — le renderer web s'y est fait prendre : ◆ avançait de 18 px sur une
# grille réglée à 9, et la case débordait de 213 px chez sa voisine.
# █ ▪ ▔ ▬ viennent des trois cases du bas, portees apres les six formes.
PALETTE = "·˙~≈─│╭╮╯╰●⬥⬦⁕⁎█▪▔▬ "


def verifier_chasse(police):
    """Rend les caractères qui ne tiennent pas la chasse. Vide si tout va bien."""
    m = QFontMetricsF(police)
    ref = m.horizontalAdvance("M")
    return [c for c in PALETTE if abs(m.horizontalAdvance(c) - ref) > 0.01]


class Grille:
    """Le découpage d'une case en cellules de caractères.

    LA GRILLE PART DES LIGNES, ET NON DES COLONNES. Le web faisait l'inverse et la case
    GRAVE se retrouvait avec une seule ligne : son anneau n'avait nulle part où exister.
    """

    def __init__(self, x, y, largeur, hauteur, lignes=9, avance=None):
        titre = hauteur * 0.22
        dispo = max(8.0, hauteur - titre - 3)
        self.lignes = lignes
        self.ch = dispo / lignes
        self.taille = self.ch * 0.82

        # L'AVANCE SE MESURE, ELLE NE SE SUPPOSE PAS.
        #
        # Le renderer web comptait ses colonnes en supposant une avance de 0,52 fois la
        # hauteur de cellule. Cette constante etait fausse des que la police reelle differait
        # de celle imaginee, et la case GRAIN debordait alors de 213 px chez sa voisine.
        # Recopier la supposition en portant le code aurait reimporte le meme defaut — ce
        # qui est exactement ce qui s'est passe au premier jet.
        #
        # L'appelant mesure donc l'avance de sa police et la passe ici. A defaut, on retombe
        # sur l'ancienne estimation, mais on ne pretend pas qu'elle soit juste.
        self.cw = avance if avance and avance > 0 else self.ch * 0.52
        self.cols = max(5, int((largeur - 4) / self.cw))
        self.x0 = x + max(2.0, (largeur - self.cols * self.cw) / 2)
        self.y0 = y + titre


def rendu(nom, g, niveau, contour, frappe, tempo):
    """Rend la forme comme une liste de lignes de caractères.

    TOUT EST EN COORDONNÉES NORMALISÉES. Chaque forme travaille dans un carré de -1 à 1,
    quelles que soient les dimensions réelles de la grille : une forme de rayon r y tient
    par construction, et le contour ne déplace le centre que de ce qui reste libre. Les
    débordements répétés des cases 4, 5 et 6 venaient tous de dessins calculés en cellules,
    où une même constante tenait dans une case et débordait dans une autre.
    """
    cxg = (g.cols - 1) / 2
    cyg = (g.lignes - 1) / 2
    ratio = cyg / max(1.0, cxg)
    force = min(1.0, niveau + frappe * 0.6)

    rmax = min(cxg * ratio, cyg) * 0.55
    marge = ENCOMBREMENT.get(nom, 0.0)
    if nom == "anneau":
        rayon = (0.25 + force * 0.75) * rmax + marge
    elif nom == "onde":
        rayon = rmax * (0.20 + force * 0.80) + marge
    elif nom == "levres":
        rayon = max(0.35, (0.10 + force * 0.95) * rmax) * 1.20 + marge
    elif nom in ("losange", "etoile"):
        rayon = (0.30 + force * 0.70) * rmax + marge
    else:
        rayon = rmax

    libre = max(0.0, cyg - rayon)
    cy = cyg + (0.5 - contour) * 2 * libre

    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            dx = (c - cxg) * ratio
            dy = l - cy
            ligne.append(_cellule(nom, dx, dy, l, c, cy, cxg, ratio,
                                  rmax, force, frappe, contour, tempo, g, rayon))
        lignes.append("".join(ligne))
    return lignes, force


def _cellule(nom, dx, dy, l, c, cy, cxg, ratio, rmax, force, frappe, contour, tempo, g, rayon):
    if nom == "anneau":
        d = math.hypot(dx, dy)
        r = (0.25 + force * 0.75) * rmax
        ep = 0.8 + frappe * 1.4
        e = abs(d - r)
        return "●" if e < ep else ("·" if e < ep + 0.9 else " ")

    if nom == "onde":
        y = cy + math.sin(c * 0.62 - tempo * 2.4) * rmax * (0.20 + force * 0.80)
        d = abs(l - y)
        return "≈" if d < 0.55 else ("~" if d < 1.3 else " ")

    if nom == "levres":
        # Une bouche est un contour fermé dont la hauteur varie et dont les coins se
        # rejoignent. Deux rangées de blocs alignés ne font pas une bouche : elles font
        # deux rangées de blocs.
        a = (0.55 + contour * 0.45) * cxg * ratio
        bb = max(0.35, (0.10 + force * 0.95) * rmax)
        q = (dx * dx) / (a * a) + (dy * dy) / (bb * bb)
        if q > 1.35:
            return " "
        if abs(math.sqrt(q) - 1) >= 0.28:
            return "·" if q < 1 and force > 0.55 else " "
        if abs(dx) / a > 0.45 and abs(dy) / bb > 0.45:
            if (dx < 0) == (dy < 0):
                return "╭" if dy < 0 else "╰"
            return "╮" if dy < 0 else "╯"
        return "│" if abs(dx) / max(0.001, a) > 0.72 else "─"

    if nom == "losange":
        d = abs(dx) + abs(dy)
        r = (0.30 + force * 0.70) * rmax
        if abs(d - r) < 0.9:
            return "⬥"
        return "⬦" if d < r and frappe > 0.25 else " "

    if nom == "etoile":
        d = math.hypot(dx, dy)
        r = (0.30 + force * 0.70) * rmax
        if d > r + 0.6:
            return " "
        ang = math.atan2(dy, dx) + contour * 3.14
        branche = abs(math.cos(ang * 3))
        if branche > 0.88 or d < 0.9:
            return "⁕" if d < 0.9 else "⁎"
        return " "

    if nom == "grain":
        i = l * g.cols + c
        phase = (i * 0.618) % 1
        pres = max(0.0, 1 - abs(l - cy) / max(1.0, rayon))
        eclat = max(0.0, 1 - abs(((force + phase) % 1) - 0.5) * 2.6) * pres
        if eclat > 0.55:
            return "⁕"
        if eclat > 0.34:
            return "·"
        return "˙" if eclat > 0.18 else " "

    return " "


# ---------------------------------------------------------------- les trois cases du bas
#
# Elles ne portent pas de source : elles disent ce que le morceau fait dans son ensemble.
# GRAVE respire avec la basse, GRAIN scintille avec les aigus, FRAPPES montre la mesure.


def grave(g, niveau):
    """Un anneau de caracteres qui respire. La seule forme que le DJ ait dite bonne.

    MESURE EN PIXELS, ET NON EN CELLULES. Une normalisation par le nombre de cellules
    donnait deux rangees de points au lieu d'un anneau : les cases sont trois fois plus
    larges que hautes, donc un cercle compte en cellules y devient une bande.
    """
    cxg = (g.cols - 1) / 2
    cyg = (g.lignes - 1) / 2
    unite = max(1.0, min(cxg * g.cw, cyg * g.ch))
    r = 0.25 + niveau * 0.70

    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            dx = (c - cxg) * g.cw / unite
            dy = (l - cyg) * g.ch / unite
            e = abs(math.hypot(dx, dy) - r)
            ligne.append("●" if e < 0.16 else ("·" if e < 0.32 else " "))
        lignes.append("".join(ligne))
    return lignes


def grain(g, fond, charley):
    """Un semis qui scintille.

    Il ne montrait rien tant qu'il ne dependait que d'une impulsion : celle-ci retombe en
    moins d'un sixieme de temps, donc invisible entre deux frappes. Le fond suit desormais
    les registres aigus en continu, et l'impulsion ne fait que l'aviver.
    """
    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            i = l * g.cols + c
            phase = (i * 0.618) % 1
            eclat = max(0.0, 1 - abs(((charley * 0.7 + fond * 0.3 + phase) % 1) - 0.5) * 2.6)
            if eclat > 0.62:
                ligne.append("⁕")
            elif eclat > 0.42:
                ligne.append("·")
            else:
                ligne.append("˙" if eclat > 0.25 else " ")
        lignes.append("".join(ligne))
    return lignes


def frappes(g, kick, clap, sur_le_un, tension):
    """La mesure : le kick s'ecarte du centre, les claps allument les bords.

    Le premier temps porte un accent plus large — c'est le seul endroit ou le rang du temps
    se voit directement, et il ne se voit que si l'on sait ou il est. On reste neutre tant
    que la grille n'a pas tranche, plutot que d'accentuer un temps au hasard.
    """
    centre = (g.cols - 1) / 2
    d = (1 - kick) * centre
    bord = round(clap * g.cols * 0.12)

    ligne = []
    for c in range(g.cols):
        dist = abs(c - centre)
        if kick > 0.02 and abs(dist - d) < 0.8 + sur_le_un:
            ligne.append("█")
        elif clap > 0.02 and (c < bord or c >= g.cols - bord):
            ligne.append("▪")
        elif kick > 0.02 and sur_le_un > 0.9 and dist < 0.7:
            ligne.append("│")
        else:
            ligne.append("·")

    sous = None
    if tension > 0.03:
        n = round(tension * g.cols)
        sous = "".join("▔" if c < n else " " for c in range(g.cols))
    return "".join(ligne), sous


def jauge(g, valeur):
    """Une bande remplie depuis la gauche, sur une ligne.

    Douze de ces lignes forment la case SPECTRE. Des colonnes de blocs empilees avaient
    ete essayees et rejetees : illisibles. Couchees, c'est la lecture d'un mixeur, et
    l'oeil suit une bande sans effort.
    """
    utiles = max(1, g.cols - 2)
    n = round(max(0.0, min(1.0, valeur)) * utiles)
    return "".join("\u25ac" if c < n else "\u00b7" for c in range(utiles))
