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

NOMS = {1: "barres", 2: "onde", 3: "masse", 4: "chute",
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


def rendu(nom, g, niveau, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """Rend la forme comme une liste de lignes de caractères.

    CHAQUE CASE EXPOSE SA SOURCE À SA FAÇON, ET AUCUNE DEUX FOIS.

    Six motifs qui se ressemblent obligent à lire l'étiquette pour savoir ce qu'on regarde,
    et l'œil perd alors le temps qu'un visuel est censé lui faire gagner. Le DJ l'a dit
    autrement : « trois sources sont affichées de la même manière, ils ressemblent à des
    anneaux lumineux qui clignotent, et ça n'aide pas ».

    LA FAUTE ÉTAIT DE COMPOSER TOUS LES MOTIFS PAREIL. Anneau, losange, étoile étaient
    trois dessins différents, mais tous centrés, tous en contour, tous de la même taille —
    donc trois taches identiques à un mètre. Ce qui distingue vraiment deux motifs dans une
    grille de caractères n'est pas leur tracé, c'est leur COMPOSITION : où la matière se
    trouve dans la case, et comment elle bouge.

      barres   des colonnes, depuis le bas          remplit par le bas, discret
      onde     une sinusoïde qui traverse           une ligne, de bord à bord
      orbe     un disque plein qui enfle            centré, massif
      comete   une masse qui glisse et traîne       se DÉPLACE horizontalement
      etoile   un éclat ponctuel                    centré, minuscule et vif
      grain    un semis                             réparti partout
      vague    des crêtes serrées qui déferlent     remplit par le bas, continu

    Aucun anneau parmi les sources : la case GRAVE en porte un, et c'est la seule forme que
    le DJ ait dite bonne. La garder unique est ce qui la rend lisible.
    """
    # CE QUE L'ATTAQUE AJOUTE DEPEND DE LA NATURE DE LA SOURCE.
    #
    # Une constante donnait le meme sursaut a une corde pincee et a un souffle. Le pique
    # dit lequel des deux on regarde : a un, l'attaque emporte la forme ; a zero, elle ne
    # la touche pas et le mouvement ne vient que du niveau — ce qui est exactement ce que
    # fait un instrument a vent, qui ne frappe jamais.
    force = min(1.0, niveau + frappe * (0.15 + 0.75 * pique))
    return _FORMES.get(nom, masse)(g, force, contour, frappe, tempo, pique, tenue), force


def barres(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """Des colonnes depuis le bas, espacées. La lecture d'un égaliseur.

    Espacées, et c'est ce qui les distingue de la vague : celle-ci est un front continu,
    celles-ci sont des objets séparés qu'on peut compter. Le contour désigne la colonne qui
    ressort — c'est là que la source se tient dans son étendue.
    """
    haut = g.lignes
    pas = 3
    vive = int(contour * (g.cols - 1))
    lignes = [[" "] * g.cols for _ in range(g.lignes)]
    for c in range(0, g.cols, pas):
        # Chaque colonne a sa propre hauteur : sans cela le motif serait un rectangle, et
        # un rectangle ne dit rien de plus qu'une jauge.
        onde = 0.72 + 0.28 * math.sin(c * 0.7 + tempo * 1.1)
        h = max(1, int(haut * (0.12 + force * 0.88) * onde))

        # LE CORPS EST PLEIN, ET C'EST CE QUI FAIT UNE BARRE. Un remplissage en petits
        # carres rend une trame de points regulierement espaces — l'oeil y lit un semis, et
        # l'on retombe sur le motif du grain. Une colonne se reconnait a sa masse continue.
        for l in range(haut - h, haut):
            lignes[l][c] = "█"
        if h < haut:
            lignes[haut - h - 1][c] = "▪"

        # La colonne du contour porte une hampe : c'est le seul endroit ou la source dit ou
        # elle se tient dans son etendue, et il faut pouvoir la trouver sans compter.
        if abs(c - vive) <= pas // 2:
            for l in range(max(0, haut - h - 3), haut - h):
                lignes[l][c] = "│"
    return ["".join(l) for l in lignes]


def onde(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """UNE seule sinusoïde lente qui traverse la case de bord à bord.

    Elle se distingue de la vague par sa longueur d'onde : ici une crête et demie sur toute
    la largeur, là une dizaine. Le contour la fait monter et descendre, le niveau lui donne
    son amplitude.
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
            # UNE SOURCE TENUE ONDULE LENTEMENT, UNE SOURCE BREVE S'AGITE. C'est la meme
            # information que la retombee de l'impulsion, dite par le mouvement continu
            # plutot que par l'eclair — ce qui la rend lisible sur une source qui ne frappe
            # jamais et n'a donc aucun eclair a montrer.
            y = centre + math.sin(c * k - tempo * (3.4 - 2.2 * tenue)) * amp
            d = abs(l - y)
            ligne.append("≈" if d < 0.55 else ("~" if d < 1.25 else " "))
        lignes.append("".join(ligne))
    return lignes


def masse(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """Une masse PLEINE et ANGULEUSE qui enfle et retombe.

    LE GESTE EST CELUI QU'ON VOULAIT, LA GEOMETRIE A CHANGE. Le DJ demandait « une orbe qui
    grandit et rapetisse » à la place des lèvres, et il l'a eue — puis il a constaté que la
    case ronde et l'anneau de GRAVE se ressemblaient trop. Or l'anneau est la seule forme
    qu'il ait jamais dite bonne : c'est donc à l'autre de céder.

    Elle enfle et retombe exactement pareil ; ses arêtes sont droites. Deux masses rondes
    dans la même matrice se confondent à un mètre, une ronde et une anguleuse jamais.
    """
    cx, cy, unite = _repere(g)
    marge = (g.lignes - 1) / 2 * g.ch / unite
    r = 0.55 + force * 0.60
    libre = max(0.0, marge - r)
    centre = cy + (0.5 - contour) * 2 * libre * unite / g.ch

    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            # La distance de Manhattan donne un losange la ou la distance euclidienne
            # donnait un disque : c'est tout ce qui change, et c'est ce qui suffit.
            d = (abs((c - cx) * g.cw / unite) + abs((l - centre) * g.ch / unite))
            if d < r * 0.55:
                ligne.append("█")
            elif d < r * 0.82:
                ligne.append("▪")
            elif d < r:
                ligne.append("⬥")
            elif d < r + 0.18:
                ligne.append("·")
            else:
                ligne.append(" ")
        lignes.append("".join(ligne))
    return lignes


def chute(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """Des traits qui TOMBENT. Le seul mouvement vertical de la matrice.

    La comète qu'elle remplace glissait horizontalement avec une traînée, et le DJ l'a
    jugée sans forme : sur neuf lignes et cinquante colonnes, un déplacement horizontal se
    confond avec l'onde qui traverse et avec la traînée du grain. Il manquait au vocabulaire
    un mouvement que rien d'autre ne fait — la verticale.

    Chaque colonne tombe à sa propre vitesse, plus vite quand la source est brève. Le
    contour décide d'où elles partent : haut dans l'aigu, bas dans le grave.
    """
    haut = g.lignes
    depart = (1.0 - contour) * haut
    vitesse = 2.5 + 5.0 * (1.0 - tenue)
    densite = 0.15 + force * 0.85

    # UN TRAIT CONTINU, ET NON DES CASES EPARSES. Une premiere version posait trois cellules
    # a des hauteurs independantes : l'oeil y lisait un semis, c'est-a-dire le motif du
    # grain. Ce qui tombe se reconnait a sa TRAINEE — une tete pleine, une queue qui
    # s'efface derriere elle.
    longueur = max(2, int(2 + force * 3))
    lignes = [[" "] * g.cols for _ in range(g.lignes)]
    for c in range(g.cols):
        # Une colonne sur deux, pour que les traits se detachent les uns des autres.
        if c % 2:
            continue
        # Un décalage propre à chaque colonne, sans quoi elles tomberaient toutes ensemble
        # et l'on verrait un rideau au lieu d'une pluie.
        phase = ((c * 0.618) % 1 + tempo * vitesse / haut) % 1.0
        if ((c * 7) % 10) >= densite * 10:
            continue
        tete = depart + phase * haut
        for k in range(longueur):
            l = int(tete - k) % haut
            lignes[l][c] = "█" if k == 0 else ("▪" if k < longueur - 1 else "·")
    return ["".join(l) for l in lignes]


def etoile(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """Un éclat ponctuel qui scintille sur l'attaque. Le plus petit motif de la matrice.

    Petit, et c'est ce qui le distingue : là où l'orbe occupe la case, l'étoile n'en prend
    que le centre et disparaît presque entre deux frappes. La différence de TAILLE fait
    autant que la différence de tracé.
    """
    cx, cy, unite = _repere(g)
    r = 0.10 + force * 0.30 + frappe * 0.25
    lignes = []
    for l in range(g.lignes):
        ligne = []
        for c in range(g.cols):
            dx = (c - cx) * g.cw / unite
            dy = (l - cy) * g.ch / unite
            d = math.hypot(dx, dy)
            if d > r + 0.05:
                ligne.append(" ")
                continue
            ang = math.atan2(dy, dx) + contour * 3.14
            if d < 0.08:
                ligne.append("⁕")
            elif abs(math.cos(ang * 3)) > 0.82:
                ligne.append("⁎")
            else:
                ligne.append(" ")
        lignes.append("".join(ligne))
    return lignes


def vague(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
    """Des crêtes serrées qui déferlent, remplies depuis le bas. Pour les registres aigus.

    L'aigu n'a pas de contour franc : ni attaque nette ni hauteur stable, seulement une
    agitation. Un motif centré lui va mal — il lui faut quelque chose qui bouge partout à
    la fois. D'où le train de vagues : une dizaine de crêtes, deux fréquences qui se
    battent pour que le motif ne se répète jamais à l'identique, et un front CONTINU, ce
    qui le distingue des barres, qui sont des objets séparés.
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


def grain_source(g, force, contour, frappe, tempo, pique=0.0, tenue=0.0):
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
    "barres": barres, "onde": onde, "masse": masse, "chute": chute,
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
