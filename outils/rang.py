#!/usr/bin/env python3
"""Combien d'objets la factorisation trouve-t-elle vraiment ?

    python3 outils/rang.py <dossier-des-pistes> [base…]

LA QUESTION VIENT DE L'OREILLE, ET LE CHIFFRE LUI DONNE RAISON.

> « Tout semble être dans la source 1, le reste c'est des minuscules bruits. »

On demande six sources au séparateur. Rien ne garantit qu'il en trouve six : il peut mettre
tout le son dans deux et laisser les quatre autres ramasser des miettes. Or l'écran, lui,
affiche six cases quoi qu'il arrive — quatre d'entre elles montreraient alors du bruit avec
la même conviction que les deux vraies.

LE RANG EFFECTIF, ET POURQUOI CETTE FORME-LA.

C'est l'exponentielle de l'entropie de la répartition d'énergie. Elle répond à : « si ces six
sources se partageaient le son également, combien y en aurait-il ? »

    six sources égales          rang 6,0
    une seule qui prend tout    rang 1,0
    deux qui portent 95 %       rang ≈ 2,4

On aurait pu compter les sources au-dessus d'un seuil. C'eût été un seuil de plus à défendre,
et il aurait rendu un entier là où la question demande une nuance : une source à 4 % n'est ni
présente ni absente. L'entropie ne demande aucun réglage.

CE QU'ON MESURE À CÔTÉ, ET POURQUOI.

Le rang seul ne dit pas OÙ va l'énergie. Sur ce répertoire, les deux sources dominantes se
sont révélées être deux tranches du même grave — ce que le rang ne pouvait pas montrer. On
rend donc aussi la couleur de chaque source et son facteur de crête, qui distingue ce qui
frappe de ce qui tient.
"""

import glob
import os
import re
import sys
import wave

import numpy as np

# Les quatre registres du projet : le kick sous 150, la basse et le bas medium jusqu'a 500,
# les accords et la voix jusqu'a 2000, le souffle au-dessus.
BANDES = ((0, 150), (150, 500), (500, 2000), (2000, 20_000))
EPS = 1e-12


def lire(chemin):
    with wave.open(chemin) as f:
        return (np.frombuffer(f.readframes(f.getnframes()), "<i2").astype(np.float64),
                f.getframerate())


def rang_effectif(parts):
    """L'exponentielle de l'entropie d'une repartition, en « nombre de sources »."""
    q = np.asarray(parts, np.float64)
    q = q / max(EPS, q.sum())
    q = q[q > 0]
    return float(np.exp(-(q * np.log(q)).sum()))


def couleur(x, taux, secondes=60):
    """Ou vit cette source, en pourcentage d'energie par registre."""
    x = x[:taux * secondes]
    if len(x) < 4096:
        return [0.0] * len(BANDES)
    sp = np.abs(np.fft.rfft(x)) ** 2
    f = np.fft.rfftfreq(len(x), 1 / taux)
    t = sp.sum() + EPS
    return [100 * sp[(f >= a) & (f < b)].sum() / t for a, b in BANDES]


def crete(x, taux):
    """Le facteur de crete de l'enveloppe : ce qui frappe le porte haut, ce qui tient bas.

    IL SE PREND SUR L'ENVELOPPE ET NON SUR L'ONDE. Sur l'onde, un seul echantillon fixe le
    resultat — mesure, cela donnait mille trois cent quatre-vingt-trois sur un morceau, ce
    qui ne veut rien dire. Sur une enveloppe a deux cents hertz, la valeur decrit la forme du
    geste : six ou sept pour une nappe, trente ou cinquante pour un train de frappes.
    """
    pas = max(1, taux // 200)
    e = np.abs(x[:len(x) // pas * pas]).reshape(-1, pas).mean(1)
    return float(np.max(e) / max(EPS, e.mean())) if len(e) else 0.0


def mesurer(dossier, base):
    """Les six pistes d'un morceau, et ce qu'elles se partagent."""
    # AUTANT DE PISTES QU'IL Y EN A. Le moteur publie un nombre de sources qu'il decouvre
    # par disque ; exiger six ici renvoyait « aucune piste » sur un morceau qui en avait
    # quatre, et l'on prenait ce vide d'outil pour un vide de moteur.
    pistes = []
    for i in range(1, 9):
        c = os.path.join(dossier, f"{base}-{i}.wav")
        if not os.path.exists(c):
            break
        pistes.append(c)
    if len(pistes) < 1:
        return None
    sons = [lire(c) for c in pistes]
    taux = sons[0][1]
    energies = [float(np.sum(x ** 2)) for x, _ in sons]
    total = max(EPS, sum(energies))
    parts = [100 * e / total for e in energies]
    return {
        "base": base,
        "parts": parts,
        "rang": rang_effectif(parts),
        "couleurs": [couleur(x, t) for x, t in sons],
        "cretes": [crete(x, t) for x, t in sons],
        "taux": taux,
    }


def bases(dossier):
    return sorted({re.sub(r"-[1-8]\.wav$", "", os.path.basename(c))
                   for c in glob.glob(os.path.join(dossier, "*-[1-8].wav"))})


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    dossier = sys.argv[1]
    voulus = sys.argv[2:] or bases(dossier)

    resultats = [r for b in voulus if (r := mesurer(dossier, b))]
    if not resultats:
        print(f"aucune piste dans {dossier}")
        return 1

    print(f"{'morceau':24s} {'part de chaque source, du grave a l aigu':^47s} {'rang':>6s}")
    for r in resultats:
        print(f"{r['base'][:24]:24s} " + " ".join(f"{v:6.1f}%" for v in r["parts"])
              + f"   {len(r['parts'])} sources  rang {r['rang']:.2f}")

    rangs = [r["rang"] for r in resultats]
    print(f"\n  rang effectif : median {sorted(rangs)[len(rangs) // 2]:.2f}   "
          f"pire {min(rangs):.2f}   meilleur {max(rangs):.2f}   sur {len(rangs)} morceaux")

    # OU VA L'ENERGIE, TOUS MORCEAUX CONFONDUS. C'est ce qui a explique le rang bas : les
    # sources qui pesent sont celles du grave, et le grave porte les trois quarts du son.
    poids = np.zeros(len(BANDES))
    for r in resultats:
        for part, coul in zip(r["parts"], r["couleurs"]):
            poids += np.array(coul) * part / 100
    poids = 100 * poids / max(EPS, poids.sum())
    print("  l energie des sources par registre : "
          + "   ".join(f"{a}-{b} Hz {v:.0f} %" for (a, b), v in zip(BANDES, poids)))

    # CE QUI FRAPPE PESE-T-IL QUELQUE CHOSE ? Une source dont la crete depasse vingt frappe ;
    # en dessous de dix, elle tient. C'est la coupure que les mesures ont fait apparaitre —
    # 6-7 pour les nappes dominantes, 34-52 pour les sources de frappe minuscules.
    frappe = sum(p for r in resultats for p, c in zip(r["parts"], r["cretes"]) if c > 20)
    tient = sum(p for r in resultats for p, c in zip(r["parts"], r["cretes"]) if c <= 10)
    n = len(resultats)
    print(f"  ce qui FRAPPE (crete > 20) pese {frappe / n:.1f} %   "
          f"ce qui TIENT (crete <= 10) pese {tient / n:.1f} %")
    return 0


if __name__ == "__main__":
    sys.exit(main())
