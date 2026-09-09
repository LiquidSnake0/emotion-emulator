#!/usr/bin/env python3
"""Les frappes du détecteur forment-elles un pouls À LA PÉRIODE DU CRATE ?

CE QUE CETTE MESURE A DE PARTICULIER, ET POURQUOI ELLE MANQUAIT.

`Emotion.Pulse` demande déjà si une suite d'instants forme un pouls — mais elle cherche
elle-même la période qui les concentre le mieux. Un détecteur qui tirerait régulièrement
sur les contretemps, ou une fois sur deux, y obtiendrait une excellente note : il pulse,
simplement pas au bon endroit. La question « est-ce un pouls » et la question « est-ce LE
pouls » sont deux questions différentes, et seule la seconde compte pour la projection.

Ici la période est imposée du dehors : celle du crate, affinée sur l'audio par
`verite_terrain`. Le détecteur ne peut donc plus choisir la question à laquelle il répond.

ET ELLE NE DEMANDE AUCUNE PHASE. C'est ce qui la rend robuste là où le rappel ne l'est
pas : dater le temps « juste » sur du barber beats — kicks étouffés, filtrés, noyés de
réverbération — reste ambigu à quelques dizaines de millisecondes près, et une vérité de
phase incertaine fabrique des rappels faux. La concentration, elle, ne regarde que la
dispersion autour d'une position quelconque.

LE NIVEAU DE HASARD SE CALCULE, il ne se devine pas : pour n instants tirés au hasard,
R vaut environ racine(pi) / (2 racine(n)). Il est toujours rendu à côté du résultat.

    python3 outils/concentration.py <dossier-verite> <dossier-frappes> [duree_s]
"""

import glob
import math
import os
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from noter_detecteur import lire


def concentration(instants, periode):
    """Rend (R, phase moyenne, niveau de hasard)."""
    n = len(instants)
    if n < 8:
        return 0.0, 0.0, 1.0
    sx = sum(math.cos(2 * math.pi * (t % periode) / periode) for t in instants)
    sy = sum(math.sin(2 * math.pi * (t % periode) / periode) for t in instants)
    r = math.hypot(sx, sy) / n
    phase = (math.atan2(sy, sx) % (2 * math.pi)) / (2 * math.pi) * periode
    return r, phase, math.sqrt(math.pi) / (2 * math.sqrt(n))


def banc(dossier_verite, dossier_frappes, duree=None, muet=False):
    lignes = []
    for chemin in sorted(glob.glob(f"{dossier_verite}/*.txt")):
        nom = os.path.basename(chemin)[:-4]
        frappes = f"{dossier_frappes}/{nom}-kicks.txt"
        if not os.path.exists(frappes):
            continue
        verite = lire(chemin)
        if len(verite) < 4:
            continue
        periode = statistics.median(b - a for a, b in zip(verite, verite[1:]))
        f = [t for t in lire(frappes) if duree is None or t <= duree]
        if len(f) < 8:
            continue
        r, _, hasard = concentration(f, periode)
        # Combien de frappes par temps : une concentration parfaite sur un dixième des
        # temps ne vaut rien pour un visuel, et R seul ne le dirait pas.
        par_temps = len(f) / len([t for t in verite if duree is None or t <= duree])
        lignes.append((nom, r, hasard, par_temps, len(f)))

    if not lignes:
        return None
    if not muet:
        print(f"{'morceau':8s} {'frappes':>8s} {'/temps':>7s} {'R':>7s} {'hasard':>7s}  au-dessus")
        for nom, r, h, pt, n in lignes:
            marque = "#" * int(max(0, r - h) * 60)
            print(f"{nom:8s} {n:8d} {pt:7.2f} {r:7.3f} {h:7.3f}  {marque}")
        print()
    moy_r = sum(l[1] for l in lignes) / len(lignes)
    moy_h = sum(l[2] for l in lignes) / len(lignes)
    moy_p = sum(l[3] for l in lignes) / len(lignes)
    gagnants = sum(1 for l in lignes if l[1] > 2 * l[2])
    if not muet:
        print(f"  R moyen {moy_r:.3f}   hasard {moy_h:.3f}   "
              f"frappes/temps {moy_p:.2f}   "
              f"{gagnants}/{len(lignes)} morceaux au-dela du double du hasard")
    return {"r": moy_r, "hasard": moy_h, "par_temps": moy_p,
            "gagnants": gagnants, "total": len(lignes)}


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    d = float(sys.argv[3]) if len(sys.argv) > 3 else None
    banc(sys.argv[1], sys.argv[2], d)
