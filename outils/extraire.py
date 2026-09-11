#!/usr/bin/env python3
"""Entendre ce que chaque source entend : un WAV par source.

POURQUOI CET OUTIL EXISTE.

Toutes les mesures de ce projet disent si une source est RÉGULIÈRE. Aucune ne dit si elle
contient ce qu'elle prétend contenir — et c'est pourtant la seule question qui compte quand
on affiche des formes en prétendant qu'elles suivent des instruments.

L'idée est du DJ : « on pourrait faire en sorte que tu extraies ce qu'entend chaque source ;
par exemple `sourceXpiano.wav`, je l'écoute et je regarde si c'est vraiment le piano, si
c'est en rythme avec le piano réel ». L'oreille tranche en dix secondes ce qu'aucun chiffre
n'a su dire.

COMMENT.

Le moteur apprend des **gabarits** — une forme spectrale par source, sur un axe
logarithmique, libre de glisser sur deux octaves — et les exporte avec leur axe
(`tools/Emotion.Probe … profils=…json`, ou `/profils` du moteur qui tourne). Ici on refait
la transformée du morceau, on la projette sur le même axe avec la même projection que le
moteur, on retrouve à quelle position et à quel niveau chaque gabarit joue à chaque instant,
et l'on répartit le spectre entre les sources au prorata de leur reconstruction. Chaque part
est retransformée en son, avec la phase d'origine.

    dotnet run -c Release --project tools/Emotion.Probe -- morceau.wav 0 90 \\
        fiche=90.92 profils=gabarits.json
    python3 outils/extraire.py gabarits.json morceau.wav sources/

LE PIÈGE QU'IL FALLAIT ÉVITER, ET IL ÉTAIT GRAVE.

L'analyse du moteur avance par blocs de 1024 échantillons **sans recouvrement**.
Reconstruire sur cette grille produirait une coupure franche toutes les 21 ms — un
bourdonnement à 47 Hz sur tous les fichiers. On entendrait un artefact de reconstruction et
on l'attribuerait à la séparation : une fausse preuve à charge contre une pièce qui n'y peut
rien, ce qui est pire qu'aucune mesure.

L'extraction fait donc **sa propre transformée**, très recouverte et fenêtrée de Hann des
deux côtés. Un gabarit est fréquentiel : il se transporte tel quel d'une grille temporelle à
une autre, gratuitement.

**Et le recouvrement de moitié n'a pas suffi.** Mesuré, il laissait un bourdonnement à
soixante-douze fois le plancher — non plus la coupure des blocs, mais le masque lui-même qui
module d'une trame à l'autre. Il a fallu monter à quatre-vingt-huit pour cent pour passer
sous le bruit du morceau. L'outil VÉRIFIE ces deux choses à chaque passage plutôt que de les
affirmer : que la chaîne reconstruit exactement, et que la somme des sources est le morceau.

CE QUE LA SOMME DES SOURCES DOIT VALOIR.

Les parts se partagent le spectre au prorata : leur somme est le spectre entier, donc leur
somme resynthétisée est le morceau d'origine. Si ce n'est pas le cas, quelque chose est faux
dans la chaîne et il vaut mieux le savoir avant d'écouter. C'est le premier contrôle, et il
échouerait bruyamment.

CE QUI EST PARTI AVEC LES PROFILS FIXES : le témoin (le morceau dans un filtre fixe taillé sur
le profil — un gabarit qui glisse n'est pas un filtre fixe), le masque « indépendant », et le
contrôle de reproductibilité des rangs. Leurs mesures restent dans le README.
"""

import json
import math
import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tempo_reference import lire_mono

def reglage(nom, defaut, conv=float):
    """Un reglage lu dans l'environnement, qui ne fait jamais tomber le programme.

    UNE VARIABLE VIDE N'EST PAS UNE VARIABLE ABSENTE, et la difference a coute deux pannes le
    meme jour. Un script qui ecrit `EMOTION_AVANCE_MS=` quand il n'a rien a dire produit une
    chaine vide, et `float("")` leve. Le reglage est FACULTATIF : ne pas le donner ne doit
    jamais empecher le programme de demarrer.
    """
    v = os.environ.get(nom, "").strip()
    try:
        return conv(v) if v else defaut
    except ValueError:
        return defaut


# RECOUVREMENT DES TRAMES, ET IL A ÉTÉ MESURÉ.
#
# Un masque qui change d'une trame à l'autre module l'amplitude à la cadence des trames.
# Mesuré sur cinq morceaux du bac, chaque source contre SON témoin à masque fixe — le morceau
# filtré par le même profil mais par un gain qui ne bouge jamais, donc sans artefact possible :
#
#     recouvrement   cadence     t02    t04     t05    t09    t11   pire
#             50 %    93,8 Hz    7,7    2,0   103,4    5,2   11,2   x103
#             75 %   187,5 Hz    2,4    1,7     7,2    1,7    1,7     x7
#             88 %   375,0 Hz    4,5    2,4     2,2    2,2    1,4     x5
#
# **Le gain décisif est entre 50 et 75 %**, d'un facteur cinquante sur le pire morceau. Au-delà
# ce n'est plus tranché : 75 % gagne en médiane (1,7 contre 2,2) et 88 % gagne en pire cas
# (x4,5 contre x7,2). On garde 88 % pour la raison qui vaut partout ailleurs dans ce projet —
# **c'est le pire cas qui se voit**, trois passages moyens non.
#
# On aurait pu s'arrêter à 50 % en écoutant vite fait, et l'on aurait entendu une source
# bourdonner cent fois plus que son témoin en croyant que la séparation la hachait.
# QUATRE PAR DEFAUT, ET C'EST UN ARBITRAGE DIT. A huit (88 %), l'extraction d'un morceau
# prenait 94 s apres les 45 s du choix : le DJ fermait la fenetre avant d'avoir entendu une
# seule piste en solo, deux fois de suite. A quatre (75 %), le bourdonnement mesure passe de
# x5 a x7 — audible a l'oreille attentive, mais les pistes arrivent en une trentaine de
# secondes, et c'est leur raison d'etre : etre entendues pendant que le disque joue.
RECOUVREMENT = reglage("RECOUVREMENT", 4, int)

# Trames traitées d'un coup. À 88 % de recouvrement, un morceau de quatre-vingt-dix secondes
# en compte soixante-sept mille : garder tout le spectrogramme en mémoire demanderait un
# demi-gigaoctet par matrice. On avance donc par blocs — et c'est EXACT, non approché : les
# niveaux se calculent colonne par colonne à gabarits fixés, donc découper le temps ne
# change pas une virgule au résultat.
BLOC = 1024

# LISSAGE DU MASQUE, en trames. Mesuré à 88 % de recouvrement, pire source de chaque morceau :
#
#     lissage      t02    t04    t05    t09    t11   moyenne
#           0      5,6    2,6    4,2    2,1    1,3      3,2
#           8      4,5    2,4    2,2    2,2    1,4      2,5
#          16      3,7    2,5    1,9    2,2    1,3      2,3
#
# **Il aide, il ne sauve pas** — et c'est huit qui est retenu parce que c'est la valeur que le
# raisonnement désigne, pas celle qui donne le meilleur chiffre. Seize ne gagne que deux
# dixièmes et coûte de la finesse temporelle ; choisir seize pour ces deux dixièmes serait
# tirer des flèches jusqu'à ce que l'une aille au milieu.
LISSAGE = reglage("LISSAGE", 8, int)


EPS = 1e-9


def hann_periodique(n):
    """La fenêtre de Hann PÉRIODIQUE, et non symétrique.

    `np.hanning` rend la symétrique, dont les deux extrémités valent zéro : superposée à
    elle-même, elle ne somme pas à une constante et la reconstruction porte une ondulation
    lente. La différence tient à un échantillon, et elle s'entend.
    """
    return 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(n) / n)



def lisser(h, largeur):
    """Empêche le masque de changer plus vite que la fenêtre qui l'a estimé.

    LE RECOUVREMENT NE SUFFIT PAS, ET C'EST UNE CORRECTION DE PRINCIPE, PAS UN PANSEMENT.
    Chaque colonne d'activations est calculée indépendamment, et la factorisation rend des
    valeurs très piquées — le projet l'a déjà mesuré ailleurs : médiane nulle, une source à
    zéro plus d'une image sur deux. Un masque qui passe de zéro à un d'une trame à l'autre
    module l'amplitude à la cadence des trames, quel que soit le recouvrement.

    Or ce masque est estimé sur une fenêtre de mille vingt-quatre échantillons. Le faire
    varier tous les cent vingt-huit prétend une résolution temporelle qu'on n'a pas. On le
    lisse donc sur une longueur de fenêtre : c'est la résolution réelle de la mesure, et
    c'est aussi la cadence à laquelle le moteur, lui, calcule ses activations.
    """
    if largeur < 2:
        return h
    # UN FILTRE DE LONGUEUR IMPAIRE, CENTRE. La version paire rendait une colonne de trop
    # (n + 1) et decalait tout d'une demi-trame ; l'ancien decoupage par blocs le masquait.
    f = hann_periodique(2 * largeur + 1)[1:2 * largeur]
    f /= f.sum()
    marge = largeur - 1
    etendu = np.pad(h, ((0, 0), (marge, marge)), mode="edge")
    lisse = np.stack([np.convolve(ligne, f, mode="valid") for ligne in etendu])
    assert lisse.shape == h.shape, (lisse.shape, h.shape)
    return lisse



def axe_log(p, taux):
    """La projection des raies sur les cases logarithmiques, EXACTEMENT celle du moteur.

    Un triangle par case, large d'une case ou d'une raie, la plus grande des deux — voir
    `SourceSeparator.ConstruireProjection`. On rend aussi la projection inverse, qui ramene
    un masque de l'axe log sur les raies : chaque raie prend la moyenne des cases qui la
    couvrent, ponderee comme a l'aller.
    """
    nfft, cases, par_oct, f0 = p["fenetre"], p["cases"], p["parOctave"], p["f0"]
    flin = np.fft.rfftfreq(nfft, 1 / taux)
    F = np.zeros((cases, len(flin)))
    ratio = 2 ** (1 / par_oct) - 1
    for l in range(cases):
        fc = f0 * 2 ** (l / par_oct)
        demi = max(fc * ratio, taux / nfft)
        F[l] = np.clip(1 - np.abs(flin - fc) / demi, 0, None)
    F /= F.sum(1, keepdims=True) + EPS
    retour = F / (F.sum(0, keepdims=True) + EPS)
    return F, retour


def etaler(gabarits, cases, positions):
    """Chaque gabarit a chacune de ses positions : une colonne par (source, position)."""
    longueur, k = gabarits.shape
    A = np.zeros((cases, k * positions))
    for s in range(k):
        for q in range(positions):
            A[q:q + longueur, s * positions + q] = gabarits[:, s]
    return A


def activer_gabarits(A, v, it=15):
    """Les niveaux par position, gabarits fixes — la regle de Kullback-Leibler du moteur."""
    h = np.full((A.shape[1], v.shape[1]), 0.01)
    somme = A.sum(0)[:, None] + EPS
    for _ in range(it):
        h *= (A.T @ (v / (A @ h + EPS))) / somme
    return h


def separer_gabarits(x, p, hop, dire=print):
    """Rend les sons des gabarits, et le morceau reconstruit sans masque.

    LES GABARITS GLISSENT : une source n'est plus une colonne fixe mais un gabarit place a
    l'une de ses positions. Le masque d'une source est la part de sa reconstruction — toutes
    positions confondues — dans la reconstruction totale, case par case de l'axe log, puis
    ramenee sur les raies. Le partage reste exact : les masques somment a un.

    LA TRANSFORMEE SE FAIT PAR BLOCS, DEUX FOIS : une fois pour les niveaux, une fois pour
    les masques. Le morceau entier en complexe tenait deux giga-octets et se faisait tuer.
    """
    nfft, cases, positions = p["fenetre"], p["cases"], p["positions"]
    gabarits = np.array(p["gabarits"], np.float64).T          # longueur x sources
    sources = gabarits.shape[1]
    F, retour = axe_log(p, taux_de(p))
    A = etaler(gabarits, cases, positions)
    w = hann_periodique(nfft)
    w2 = w * w
    n = 1 + max(0, (len(x) - nfft) // hop)

    def bloc_spectres(i0, i1):
        depart = np.arange(i0, i1) * hop
        return np.fft.rfft(np.stack([x[d:d + nfft] for d in depart]) * w, axis=1), depart

    # Les niveaux, sur tout le morceau, gabarits fixes.
    v = np.zeros((cases, n))
    for i0 in range(0, n, BLOC):
        i1 = min(n, i0 + BLOC)
        spectres, _ = bloc_spectres(i0, i1)
        v[:, i0:i1] = F @ np.abs(spectres).T
    v += EPS
    h = lisser(activer_gabarits(A, v), LISSAGE)
    total = A @ h + EPS
    parts = []
    for s in range(sources):
        hs = np.zeros_like(h)
        hs[s * positions:(s + 1) * positions] = h[s * positions:(s + 1) * positions]
        parts.append((A @ hs) / total)                         # cases x n, entre 0 et 1

    sorties = [np.zeros(len(x) + nfft) for _ in range(sources)]
    brut = np.zeros(len(x) + nfft)
    poids = np.zeros(len(x) + nfft)
    for i0 in range(0, n, BLOC):
        i1 = min(n, i0 + BLOC)
        spectres, depart = bloc_spectres(i0, i1)
        rec = np.fft.irfft(spectres, nfft, axis=1) * w
        for k, d in enumerate(depart):
            brut[d:d + nfft] += rec[k]
            poids[d:d + nfft] += w2
        for s in range(sources):
            masque = (retour.T @ parts[s][:, i0:i1]).T          # trames x raies
            part = np.fft.irfft(spectres * masque, nfft, axis=1) * w
            cible = sorties[s]
            for k, d in enumerate(depart):
                cible[d:d + nfft] += part[k]

    bon = poids > 1e-8
    for s in range(sources):
        sorties[s][bon] /= poids[bon]
        sorties[s] = sorties[s][:len(x)]
    brut[bon] /= poids[bon]
    return sorties, brut[:len(x)], n


def taux_de(p):
    return int(p["taux"])


def ecrire_wav(chemin, x, taux):
    """Un WAV mono 16 bits. On normalise sur le morceau entier, jamais par source.

    Normaliser chaque source séparément la rendrait aussi forte que les autres, et l'on
    perdrait précisément ce qu'on veut entendre : si une source est faible, c'est une
    information.
    """
    d = np.clip(x, -1.0, 1.0)
    with wave.open(chemin, "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(taux)
        f.writeframes((d * 32767).astype("<i2").tobytes())


def extraire(chemin_gabarits, chemin_wav, dossier):
    """Les deux controles de chaine, puis une piste par source."""
    with open(chemin_gabarits, encoding="utf-8") as fh:
        p = json.load(fh)
    if not p.get("pret"):
        print("ATTENTION : le moteur n'avait rien appris, les gabarits ne decrivent que du bruit")
    x, taux = lire_mono(chemin_wav)
    x = x.astype(np.float64)
    if taux != p["taux"]:
        print(f"le morceau est a {taux} Hz, les gabarits ont ete appris a {p['taux']}")
    nfft = p["fenetre"]
    hop = max(1, nfft // RECOUVREMENT)
    sources = len(p["gabarits"])
    print(f"{os.path.basename(chemin_wav)} : {len(x) / taux:.0f} s, "
          f"recouvrement {100 * (1 - 1 / RECOUVREMENT):.0f} %, {sources} gabarits")
    sons, brut, n = separer_gabarits(x, p, hop)
    if n < 8:
        print("  morceau trop court")
        return 1
    marge = nfft

    def snr(y):
        err = y[marge:-marge] - x[marge:-marge]
        return 10 * math.log10(np.sum(x[marge:-marge] ** 2) / max(EPS, np.sum(err ** 2)))

    a = snr(brut)
    print(f"  reconstruction sans masque   {a:6.1f} dB", end="")
    if a <= 60:
        print("   ECHEC : la chaine est fausse, rien n'est ecrit")
        return 1
    print("   ok")
    b = snr(sum(sons))
    print(f"  somme des sources = morceau  {b:6.1f} dB", end="")
    print("   ok" if b > 25 else "   SUSPECT : les masques ne se partagent pas tout")

    os.makedirs(dossier, exist_ok=True)
    base = os.path.splitext(os.path.basename(chemin_wav))[0]
    total = max(EPS, sum(float(np.sum(y ** 2)) for y in sons))
    print(f"\n  {'source':>7s} {'part du son':>12s}")
    for s in range(sources):
        chemin = os.path.join(dossier, f"{base}-source{s + 1}.wav")
        ecrire_wav(chemin, sons[s], taux)
        print(f"  {s + 1:7d} {100 * float(np.sum(sons[s] ** 2)) / total:11.1f} %   {os.path.basename(chemin)}")
    return 0



if __name__ == "__main__":
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(1)
    sys.exit(extraire(*sys.argv[1:]))
