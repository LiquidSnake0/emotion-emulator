#!/usr/bin/env python3
"""De combien la main tape après ce qu'elle entend.

CE CHIFFRE MANQUE À TOUTE MESURE FAITE À LA MAIN, ET IL NE SE DEVINE PAS.

Une main tape APRÈS avoir entendu — cinquante à cent cinquante millisecondes selon la
personne, l'heure et le morceau. Ce retard est systématique : il ne gêne pas une mesure de
PÉRIODE, où il s'annule entre deux frappes, mais il fausse entièrement une mesure de PHASE.
Or c'est justement la phase qui manque au projet.

COMMENT ON LE MESURE SANS RIEN SUPPOSER.

On tape sur `etalon-kick.wav` : des grosses caisses seules, dont les clics sont dans le
fichier et se relèvent à l'échantillon près, sans détecteur ni seuil. L'écart médian entre
la main et les clics EST la latence. Quatre-vingt-dix secondes suffisent.

    python3 outils/taper.py etalon.json        (en jouant etalon-kick.wav)
    python3 outils/latence_main.py etalon.json <chemin>/etalon-kick.wav

ON REND AUSSI LA DISPERSION, et elle compte autant que la médiane. Une main régulière à dix
millisecondes près donne une vérité utilisable ; une main qui varie de cent ne dira jamais où
tombe un temps, quelle que soit la correction qu'on lui applique. Le chiffre dit donc à la
fois de combien corriger, et si la correction vaut la peine.
"""

import json
import statistics
import sys

import numpy as np

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from tempo_reference import lire_mono


def clics(chemin):
    """Les vrais clics, sans aucun traitement.

    Le signal est silencieux entre eux : le premier échantillon qui dépasse un dixième du
    maximum EST l'attaque. Aucun filtre, donc aucun retard de groupe à corriger — ce serait
    ironique de mesurer une latence avec un outil qui en ajoute une.
    """
    x, rate = lire_mono(chemin)
    seuil = 0.10 * np.abs(x).max()
    fort = np.abs(x) > seuil
    debuts = np.flatnonzero(fort & ~np.concatenate(([False], fort[:-1])))
    gardes = []
    for i in debuts:
        if not gardes or i - gardes[-1] > 0.2 * rate:
            gardes.append(i)
    return [i / rate for i in gardes]


def mesurer(chemin_notes, chemin_wav):
    with open(chemin_notes, encoding="utf-8") as fh:
        notes = json.load(fh)["notes"]
    vrais = clics(chemin_wav)
    if len(vrais) < 8:
        print("pas de clics nets dans ce fichier")
        return 1

    ecarts = []
    for n in notes:
        # L'instant d'une note tapée est son enfoncement : le relâchement dépend de la
        # nervosité du doigt, pas de ce qu'on a entendu.
        t = n["debut"]
        proche = min(vrais, key=lambda v: abs(v - t))
        d = t - proche
        # Au-delà d'un tiers de seconde, la main a manqué un clic ou en a tapé un de trop :
        # ce n'est plus une latence, c'est une erreur, et la moyenner la masquerait.
        if abs(d) < 0.35:
            ecarts.append(d * 1000)

    if len(ecarts) < 8:
        print(f"seulement {len(ecarts)} notes appariees : trop peu pour conclure")
        return 1

    med = statistics.median(ecarts)
    disp = statistics.pstdev(ecarts)
    print(f"{len(ecarts)} notes appariees sur {len(notes)}")
    print(f"  latence de la main   {med:+7.1f} ms")
    print(f"  dispersion           {disp:7.1f} ms")
    print()
    if disp < 25:
        print("  main tres reguliere : la phase tapee vaut une verite terrain.")
    elif disp < 60:
        print("  main reguliere : utilisable pour la phase apres correction.")
    else:
        print("  main dispersee : la periode reste utilisable, la phase non.")
    print(f"\n  a retrancher de tous les instants tapes : {med:.0f} ms")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    sys.exit(mesurer(sys.argv[1], sys.argv[2]))
