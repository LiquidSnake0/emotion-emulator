#!/usr/bin/env python3
"""Fabrique un metronome de reference, tempo exact, pour verifier la chaine.

POURQUOI IL EXISTE. Toutes les mesures internes du projet comparent les frappes a la
grille, laquelle se cale sur ces memes frappes : un defaut commun aux deux leur est
invisible. Un signal dont on connait la verite est la seule facon de le voir — c'est
ainsi qu'un retard systematique de 17 ms a ete trouve apres des semaines.

    python3 outils/metronome.py metronome.wav 87.85 90
"""
import math
import random
import struct
import sys
import wave

RATE = 48_000


def fabriquer(sortie="metronome.wav", bpm=87.85, duree=90.0):
    """Ecrit le metronome et rend son chemin.

    TOUT CECI ETAIT AU NIVEAU DU MODULE, et l'importer ecrivait un fichier. Le controle de
    fumee l'a trouve en essayant simplement `import metronome` : un module qui produit un
    WAV rien qu'en etant charge ne peut ni se relire ni se reutiliser, et surprend celui qui
    l'importe pour une seule de ses fonctions.
    """
    temps = 60.0 / bpm
    n = int(RATE * duree)
    buf = [0.0] * n
    alea = random.Random(3)

    def poser(a, freq, ms, amp, bruit=0.0):
        d = int(RATE * ms / 1000)
        for k in range(d):
            if a + k >= n:
                break
            t = k / RATE
            env = min(1.0, k / (RATE * 0.0008)) * math.exp(-t * (1000.0 / ms))
            s = math.sin(2 * math.pi * freq * t)
            if bruit:
                s = s * (1 - bruit) + (alea.random() * 2 - 1) * bruit
            buf[a + k] += s * env * amp

    b = 0
    while b * temps < duree - 1:
        a = int(b * temps * RATE)
        poser(a, 58.0, 90, 0.85)                                  # kick sur chaque temps
        if b % 4 in (1, 3):
            poser(a, 220.0, 60, 0.55, bruit=0.75)                 # clap sur 2 et 4
        poser(a + int(temps * RATE / 2), 9000.0, 25, 0.20, bruit=0.9)   # charley aux croches
        b += 1

    pic = max(abs(x) for x in buf) or 1.0
    with wave.open(sortie, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(x / pic * 30000)) for x in buf))
    print(f"{sortie} — {bpm} BPM, temps de {temps * 1000:.2f} ms, {duree} s")
    return sortie


if __name__ == "__main__":
    fabriquer(sys.argv[1] if len(sys.argv) > 1 else "metronome.wav",
              float(sys.argv[2]) if len(sys.argv) > 2 else 87.85,
              float(sys.argv[3]) if len(sys.argv) > 3 else 90.0)
