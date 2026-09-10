#!/usr/bin/env python3
"""Apprendre les six profils sur le morceau ENTIER, et non sur 2,7 secondes glissantes.

    python3 outils/profils_entier.py <morceau.wav> <profils.json>

L'INTUITION EST DU DJ, ET ELLE DESIGNE LA CAUSE.

> « Il semblerait que tu ne marches pas façon machine learning en te passant tout un album ;
>   toi tu mélanges tout en même temps et tu m'extrais un truc bon, le reste c'est n'importe
>   quoi. »

Vérifié : `SourceSeparator.Memoire` vaut 128 images, soit **2,7 secondes**. La factorisation
du moteur apprend ses six profils sur une fenêtre glissante de deux secondes et demie. Elle
ne voit jamais le morceau — c'est un instantané permanent, pas un apprentissage.

Cela explique tout ce que la journée a mesuré :

  une source prend tout          sur 2,7 s, ce qui joue fort monopolise le budget
  les rangs bougent              deux fenetres differentes, deux jeux d'objets differents
  aucun instrument reconnu       un instrument se definit sur la duree, pas sur trois secondes
  le kick creuse les autres      sur une fenetre si courte, il est l'evenement dominant

ET C'EST DEFENDABLE DANS LE MOTEUR. Le temps réel n'a pas le choix : il doit rendre des
activations à chaque image de 21 ms, sans connaître la suite, et une factorisation sur le
morceau entier demanderait de l'avoir entendu en entier. La fenêtre courte est le prix du
direct.

MAIS ELLE N'EST PAS UNE FATALITE POUR LA VALIDATION. Ici, hors ligne, on a le morceau entier
sous la main. Ce fichier apprend donc les profils comme un système hors ligne le ferait, et
les écrit AU MEME FORMAT que `/profils` — si bien que `extraire.py` et `stems.py` les
consomment sans un mot de plus, et que la comparaison est ligne à ligne.

Si les six sources deviennent alors reconnaissables, la cause est établie et le débat devient
celui du cue : le moteur peut-il prendre le temps d'écouter un disque au casque avant de le
laisser passer au master ?
"""

import json
import os
import sys
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tempo_reference import lire_mono

SOURCES = 6
NFFT = 1024
HOP = 512                 # deux fois plus fin que le moteur : on peut se le permettre ici
ITERATIONS = 200
EPS = 1e-9


def hann_periodique(n):
    return 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(n) / n)


def spectrogramme(x, nfft=NFFT, hop=HOP):
    w = hann_periodique(nfft)
    n = 1 + max(0, (len(x) - nfft) // hop)
    return np.stack([np.abs(np.fft.rfft(x[i * hop:i * hop + nfft] * w))[:nfft // 2]
                     for i in range(n)]).T          # bins x trames


def factoriser(v, sources=SOURCES, iterations=ITERATIONS, graine=20260910):
    """La factorisation de Lee et Seung, sur tout le morceau.

    MULTIPLICATIVE, DONC POSITIVE PAR CONSTRUCTION — une source ne joue jamais « moins que
    rien », et c'est la raison d'être de cette famille de méthodes. Le moteur emploie la même
    règle ; la seule différence est ce qu'on lui donne à regarder.

    Deux cents itérations sur un morceau entier au lieu de quarante sur deux secondes et
    demie : c'est le même calcul, avec le temps de converger et la matière pour le faire.
    """
    alea = np.random.default_rng(graine)
    bins, trames = v.shape
    w = alea.random((bins, sources)) * 0.9 + 0.1
    h = alea.random((sources, trames)) * 0.9 + 0.1
    for _ in range(iterations):
        wh = w @ h + EPS
        h *= (w.T @ v) / (w.T @ wh + EPS)
        wh = w @ h + EPS
        w *= (v @ h.T) / (wh @ h.T + EPS)
        # ON NORMALISE LES PROFILS, PAS LES ACTIVATIONS. Sans cela, W et H derivent
        # ensemble — l'un grandit pendant que l'autre retrecit — et les profils finissent a
        # des echelles incomparables entre eux.
        norme = np.sqrt((w ** 2).sum(axis=0)) + EPS
        w /= norme
        h *= norme[:, None]
    return w, h


def ordonner(w, taux, nfft=NFFT):
    """Du grave a l'aigu, par centre de gravite — le meme ordre que le moteur.

    IL SE PREND SUR L'ENERGIE ET NON SUR L'AMPLITUDE. Pondere par l'amplitude, un centre de
    gravite est tire vers le haut par le souffle des aigus : c'est ce qui avait fait annoncer
    « bass n'est pas une basse » sur un stem qui etait a 99,6 % sous 150 Hz.
    """
    bins = w.shape[0]
    f = np.arange(bins) * taux / nfft
    e = w ** 2
    centres = (e * f[:, None]).sum(axis=0) / (e.sum(axis=0) + EPS)
    return np.argsort(centres), centres


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    chemin, sortie = sys.argv[1], sys.argv[2]

    x, taux = lire_mono(chemin)
    x = x.astype(np.float64)
    depart = time.time()
    v = spectrogramme(x)
    print(f"  {os.path.basename(chemin)[:34]:34s} {len(x) / taux:5.0f} s, "
          f"{v.shape[1]} trames — apprentissage sur le morceau entier")

    w, h = factoriser(v)
    ordre, centres = ordonner(w, taux)
    w = w[:, ordre]
    h = h[ordre]

    parts = (h.sum(axis=1) * np.sqrt((w ** 2).sum(axis=0)))
    parts = 100 * parts / max(EPS, parts.sum())
    print(f"  {'source':>7s} {'couleur':>10s} {'part':>7s}")
    for s in range(SOURCES):
        print(f"  {s + 1:7d} {centres[ordre[s]]:9.0f} Hz {parts[s]:6.1f} %")

    with open(sortie, "w", encoding="utf-8") as fh:
        json.dump({
            "fichier": os.path.basename(chemin),
            "taux": taux,
            "bins": w.shape[0],
            "fenetre": NFFT,
            "pret": True,
            "appris_sur": "le morceau entier",
            "profils": [w[:, s].tolist() for s in range(SOURCES)],
        }, fh)
    print(f"  {time.time() - depart:.0f} s — profils ecrits vers {sortie}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
