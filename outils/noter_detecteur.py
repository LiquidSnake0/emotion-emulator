#!/usr/bin/env python3
"""Note le détecteur d'attaques contre une vérité terrain extérieure.

CE QU'IL RÉPOND, ET QUE RIEN NE RÉPONDAIT.

Tous les indicateurs du détecteur comparent ses frappes à une grille calée sur ces mêmes
frappes. Ce fichier compare ses frappes à une grille qu'il n'a pas produite —
`verite_terrain.py` la fabrique de la période du crate et du repli d'énergie du morceau.

    rappel      part des vrais temps qui portent une frappe   « en manque-t-on »
    precision   part des frappes qui tombent sur un vrai temps « en invente-t-on »

LE NIVEAU DE HASARD EST TOUJOURS RENDU, et c'est une leçon payée : une tolérance de
±0,12 temps sur une grille de croches couvre 96 % de l'espace, et l'on annonçait alors
fièrement « 97 % sur la grille ». Une métrique se vérifie comme un algorithme.

    python3 outils/noter_detecteur.py verite/t05.txt banc/xxx/t05-kicks.txt
"""

import statistics
import sys


# Deux tolérances, et pas une. La fenêtre d'analyse fait 21,3 ms et le repérage du
# transitoire descend à 2,7 ms ; ±30 ms mesure donc la justesse fine, ±60 la couverture
# — un temps peut être vu et mal daté, ce sont deux défauts distincts.
TOLERANCES = (0.030, 0.060)


def lire(chemin):
    with open(chemin, encoding="utf-8") as fh:
        return [float(l) for l in fh if l.strip()]


def apparier(verite, frappes, tol):
    """Chaque vrai temps prend au plus une frappe, et chaque frappe au plus un temps.

    Sans cette exclusivité, un détecteur qui tire cinq fois de suite au même endroit
    obtiendrait cinq fois la même bonne réponse. Une frappe consommée ne l'est plus.
    """
    libres = sorted(frappes)
    pris = [False] * len(libres)
    ecarts, vus = [], 0
    for t in verite:
        meilleur, ecart = -1, tol
        for i, f in enumerate(libres):
            if pris[i]:
                continue
            if f < t - tol:
                continue
            if f > t + tol:
                break
            if abs(f - t) <= ecart:
                meilleur, ecart = i, abs(f - t)
        if meilleur >= 0:
            pris[meilleur] = True
            vus += 1
            ecarts.append(libres[meilleur] - t)
    return vus, sum(pris), ecarts


def noter(chemin_verite, chemin_frappes, muet=False):
    verite = lire(chemin_verite)
    frappes = lire(chemin_frappes)
    if len(verite) < 4:
        return None
    periode = statistics.median(b - a for a, b in zip(verite, verite[1:]))

    res = {"temps": len(verite), "frappes": len(frappes), "periode": periode}
    for tol in TOLERANCES:
        vus, apparies, ecarts = apparier(verite, frappes, tol)
        rappel = vus / len(verite)
        precision = apparies / max(1, len(frappes))
        # Le hasard : une frappe posée n'importe où tombe dans la fenêtre 2·tol/periode
        # du temps le plus proche.
        hasard = min(1.0, 2 * tol / periode)
        res[tol] = {
            "rappel": rappel, "precision": precision, "hasard": hasard,
            "biais": statistics.median(ecarts) if ecarts else float("nan"),
            "dispersion": statistics.pstdev(ecarts) if len(ecarts) > 1 else float("nan"),
        }
    if not muet:
        print(f"{chemin_frappes.split('/')[-1]}   {len(verite)} temps de "
              f"{1000*periode:.0f} ms · {len(frappes)} frappes "
              f"({len(frappes)/len(verite):.2f} par temps)")
        for tol in TOLERANCES:
            r = res[tol]
            print(f"   +/-{1000*tol:3.0f} ms   rappel {100*r['rappel']:5.1f} %   "
                  f"precision {100*r['precision']:5.1f} %   "
                  f"(hasard {100*r['hasard']:.0f} %)   "
                  f"biais {1000*r['biais']:+6.1f} ms   "
                  f"dispersion {1000*r['dispersion']:5.1f} ms")
    return res


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    noter(sys.argv[1], sys.argv[2])
