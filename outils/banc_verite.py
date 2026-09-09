#!/usr/bin/env python3
"""Passe un jeu de frappes sur toute la vérité terrain et rend la moyenne.

    python3 outils/banc_verite.py <dossier-verite> <dossier-frappes> [duree_s]

La durée borne la vérité à ce que la sonde a réellement analysé : elle s'arrête à 90 s
alors que certains morceaux durent cinq minutes, et compter comme manqués des temps qu'on
n'a jamais écoutés donnerait un rappel faux — dans le sens défavorable, mais faux quand
même.
"""

import glob
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from noter_detecteur import TOLERANCES, lire, noter


def banc(dossier_verite, dossier_frappes, duree=None, muet=False):
    lignes, cumul = [], {t: {"rappel": [], "precision": [], "hasard": []} for t in TOLERANCES}
    for chemin in sorted(glob.glob(f"{dossier_verite}/*.txt")):
        nom = os.path.basename(chemin)[:-4]
        frappes = f"{dossier_frappes}/{nom}-kicks.txt"
        if not os.path.exists(frappes):
            continue

        verite = [t for t in lire(chemin) if duree is None or t <= duree]
        borne = f"/tmp/verite-{nom}.txt"
        with open(borne, "w", encoding="utf-8") as fh:
            fh.write("\n".join(f"{t:.3f}" for t in verite) + "\n")

        r = noter(borne, frappes, muet=True)
        os.remove(borne)
        if r is None:
            continue
        lignes.append((nom, r))
        for t in TOLERANCES:
            for k in cumul[t]:
                cumul[t][k].append(r[t][k])

    if not lignes:
        print("aucun morceau apparie")
        return None

    if not muet:
        print(f"{'morceau':10s} {'frappes':>8s} {'/temps':>7s}"
              f"   {'rappel30':>9s} {'prec30':>7s}   {'rappel60':>9s} {'prec60':>7s}")
        for nom, r in lignes:
            print(f"{nom:10s} {r['frappes']:8d} {r['frappes']/r['temps']:7.2f}"
                  f"   {100*r[0.030]['rappel']:8.1f}% {100*r[0.030]['precision']:6.1f}%"
                  f"   {100*r[0.060]['rappel']:8.1f}% {100*r[0.060]['precision']:6.1f}%")
        print()
    moy = {}
    for t in TOLERANCES:
        moy[t] = {k: sum(v) / len(v) for k, v in cumul[t].items()}
        f = 2 * moy[t]["rappel"] * moy[t]["precision"] / max(
            1e-9, moy[t]["rappel"] + moy[t]["precision"])
        moy[t]["f"] = f
        if not muet:
            print(f"  +/-{1000*t:3.0f} ms   rappel {100*moy[t]['rappel']:5.1f} %   "
                  f"precision {100*moy[t]['precision']:5.1f} %   F {100*f:5.1f} %   "
                  f"(hasard {100*moy[t]['hasard']:.0f} %)   sur {len(lignes)} morceaux")
    return moy


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    d = float(sys.argv[3]) if len(sys.argv) > 3 else None
    banc(sys.argv[1], sys.argv[2], d)
