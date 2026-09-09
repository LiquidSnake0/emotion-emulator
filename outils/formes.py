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

NOMS = {1: "anneau", 2: "onde", 3: "orbe", 4: "losange",
        5: "etoile", 6: "grain", 7: "vague"}

# Tous les caractères employés. Ils doivent tous avoir la même chasse, sans quoi une ligne
# entière dérive — le renderer web s'y est fait prendre : ◆ avançait de 18 px sur une
# grille réglée à 9, et la case débordait de 213 px chez sa voisine.
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


def _repere(g):
    """Le repère d'une case : centre, et unité de longueur EN PIXELS.

    TOUTE FORME RONDE DOIT SE MESURER EN PIXELS, ET C'EST LA CORRECTION QUI LES A
    DÉSEMBROUILLÉES. Une case fait trois fois et demie sa hauteur en largeur ; un cercle
    calculé en cellules y devient une bande horizontale. C'est pour ça que l'anneau, le
    losange et l'étoile se ressemblaient tous — trois motifs différents, écrasés en la même
    barre. La case GRAVE, elle, mesurait déjà en pixels, et c'est la seule forme que le DJ
    ait dite bonne. On généralise ce qui marchait.
    """
    cx = (g.cols - 1) / 2
    cy = (g.lignes - 1) / 2
    unite = max(1.0, min(cx * g.cw, cy * g.ch))
    return cx, cy, unite


def rendu(nom, g, niveau, contour, frappe, tempo):
    """Rend la forme comme une liste de lignes de caractères.

    CHAQUE CASE EXPOSE SA SOURCE À SA FAÇON, et ce n'est pas de la décoration : six motifs
    qui se ressemblent obligent à lire l'étiquette pour savoir ce qu'on regarde, et l'œil
    perd alors le temps qu'un visuel est censé lui faire gagner.

      anneau   un cercle creux qui respire            centré, en creux
      onde     une sinusoïde lente qui traverse       une ligne, de bord à bord
      orbe     un disque plein qui enfle et retombe   centré, plein
      losange  des arêtes droites qui pulsent         centré, anguleux
      etoile   un éclat qui scintille sur l'attaque   centré, ponctuel
      grain    un semis                               réparti partout
      vague    des crêtes serrées qui déferlent       rempli depuis le bas
    """
    force = min(1.0, niveau + frappe * 0.6)
    fonction = _FORMES.get(nom, orbe)
    return fonction(g, force, contour, frappe, tempo), force


def anneau(g, force, contour, frappe, tempo):
    """Un cercle creux qui respire. Il enfle avec le niveau, s'épaissit sur la frappe."""
    cx, cy, unite = _repere(g)
    r = 0.22 + force * 0.72
    ep = 0.08 + frappe * 0.10
    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            d = math.hypot((c - cx) * g.cw / unite, (l - cy) * g.ch / unite)
            e = abs(d - r)
            ligne.append("●" if e < ep else ("·" if e < ep + 0.14 else " "))
        lignes.append("".join(ligne))
    return lignes


def onde(g, force, contour, frappe, tempo):
    """UNE seule sinusoïde lente qui traverse la case de bord à bord.

    Elle se distingue de la vague par sa longueur d'onde : ici deux crêtes au plus sur
    toute la largeur, là une dizaine. Le contour la fait monter et descendre, le niveau
    lui donne son amplitude.
    """
    cy = (g.lignes - 1) / 2
    marge = (g.lignes - 1) / 2 * 0.85
    centre = cy + (0.5 - contour) * 2 * marge * 0.6
    amp = marge * (0.15 + force * 0.85)
    k = 2 * math.pi * 1.5 / max(1, g.cols - 1)

    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            y = centre + math.sin(c * k - tempo * 2.0) * amp
            d = abs(l - y)
            ligne.append("≈" if d < 0.55 else ("~" if d < 1.25 else " "))
        lignes.append("".join(ligne))
    return lignes


def orbe(g, force, contour, frappe, tempo):
    """Un disque PLEIN qui grandit et rapetisse. Il a remplacé les lèvres.

    La bouche voulait dire « ce qui chante » et ne disait rien : deux rangées de filets
    qu'aucun œil ne lisait comme une bouche. Une masse qui enfle et retombe se lit sans
    apprentissage, et c'est déjà ce que le DJ avait retenu de la case GRAVE.

    Plein et non creux : c'est ce qui le distingue de l'anneau au premier coup d'œil, et
    les deux peuvent alors cohabiter dans la même matrice.
    """
    cx, cy, unite = _repere(g)
    marge = (g.lignes - 1) / 2 * g.ch / unite
    r = 0.18 + force * 0.78
    libre = max(0.0, marge - r)
    centre = cy + (0.5 - contour) * 2 * libre * unite / g.ch

    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            d = math.hypot((c - cx) * g.cw / unite, (l - centre) * g.ch / unite)
            # LE DÉGRADÉ ARRONDIT LA SILHOUETTE. Un remplissage franc de blocs pleins rend
            # un rectangle : chaque cellule est un pavé, et l'œil ne voit que leur contour
            # commun. Trois densités décroissantes vers le bord dessinent la courbe que la
            # grille de caractères ne peut pas tracer.
            if d < r * 0.52:
                ligne.append("█")
            elif d < r * 0.78:
                ligne.append("▪")
            elif d < r:
                ligne.append("●")
            elif d < r + 0.18:
                ligne.append("·")
            else:
                ligne.append(" ")
        lignes.append("".join(ligne))
    return lignes


def losange(g, force, contour, frappe, tempo):
    """Des arêtes droites qui pulsent. Le seul motif anguleux de la matrice."""
    cx, cy, unite = _repere(g)
    r = 0.25 + force * 0.75
    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            d = abs((c - cx) * g.cw / unite) + abs((l - cy) * g.ch / unite)
            if abs(d - r) < 0.11:
                ligne.append("⬥")
            elif d < r and frappe > 0.25:
                ligne.append("⬦")
            else:
                ligne.append(" ")
        lignes.append("".join(ligne))
    return lignes


def etoile(g, force, contour, frappe, tempo):
    """Un éclat qui scintille sur l'attaque ; ses branches tournent avec le contour."""
    cx, cy, unite = _repere(g)
    r = 0.25 + force * 0.75
    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            dx = (c - cx) * g.cw / unite
            dy = (l - cy) * g.ch / unite
            d = math.hypot(dx, dy)
            if d > r + 0.08:
                ligne.append(" ")
                continue
            ang = math.atan2(dy, dx) + contour * 3.14
            branche = abs(math.cos(ang * 3))
            if d < 0.12:
                ligne.append("⁕")
            elif branche > 0.86:
                ligne.append("⁎")
            else:
                ligne.append(" ")
        lignes.append("".join(ligne))
    return lignes


def vague(g, force, contour, frappe, tempo):
    """Des crêtes serrées qui déferlent, remplies depuis le bas. Pour les registres aigus.

    L'aigu n'a pas de contour franc : ni attaque nette ni hauteur stable, seulement une
    agitation. Un motif centré lui va mal — il lui faut quelque chose qui bouge partout à
    la fois. D'où le train de vagues : une dizaine de crêtes, deux fréquences qui se
    battent pour que le motif ne se répète jamais à l'identique, et un remplissage par le
    bas qui donne le niveau d'un coup d'œil.
    """
    haut = g.lignes - 1
    base = haut * (1.0 - (0.12 + force * 0.80))
    k1 = 2 * math.pi * 5.0 / max(1, g.cols - 1)
    k2 = 2 * math.pi * 8.0 / max(1, g.cols - 1)
    amp = haut * (0.06 + force * 0.22)

    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            crete = (base
                     + math.sin(c * k1 - tempo * 3.2) * amp
                     + math.sin(c * k2 + tempo * 1.7) * amp * 0.45)
            if l < crete - 0.6:
                ligne.append(" ")
            elif l < crete + 0.5:
                ligne.append("≈")
            elif l < crete + 1.6:
                ligne.append("~")
            else:
                ligne.append("·" if (l + c) % 2 == 0 else " ")
        lignes.append("".join(ligne))
    return lignes


def grain_source(g, force, contour, frappe, tempo):
    """Un semis qui scintille, concentré à la hauteur du contour."""
    haut = g.lignes - 1
    centre = haut * (1.0 - contour)
    portee = max(1.0, haut * (0.25 + force * 0.75))
    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            phase = ((l * g.cols + c) * 0.618) % 1
            pres = max(0.0, 1 - abs(l - centre) / portee)
            eclat = max(0.0, 1 - abs(((force + phase) % 1) - 0.5) * 2.6) * pres
            ligne.append("⁕" if eclat > 0.55 else
                         ("·" if eclat > 0.34 else ("˙" if eclat > 0.18 else " ")))
        lignes.append("".join(ligne))
    return lignes


_FORMES = {
    "anneau": anneau, "onde": onde, "orbe": orbe, "losange": losange,
    "etoile": etoile, "grain": grain_source, "vague": vague,
}


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
