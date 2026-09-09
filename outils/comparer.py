#!/usr/bin/env python3
"""Confronte ce que le moteur a publié en temps réel à ce que l'analyse hors ligne trouve.

POURQUOI CE FICHIER EXISTE.

Toutes les mesures du moteur se jugeaient contre lui-même. Ses indicateurs internes
comparent les frappes à une grille calée sur ces mêmes frappes ; sa sonde et son serveur
partagent le même code, donc les mêmes erreurs. Un défaut commun aux deux leur est
invisible par construction — c'est ainsi qu'un retard systématique d'une fenêtre a survécu
des semaines, et qu'un tempo faux treize pour cent du temps est passé pour un transitoire.

Ce fichier apporte le point de vue extérieur : `tempo_reference.py` relit le morceau avec
une autre implémentation, d'autres fenêtres, une autre préférence, en Python plutôt qu'en
C#. Il passe ses deux étalons — un métronome à kick seul et un métronome avec claps et
charleys, tous deux à 87,85 BPM — donc il a le droit de contredire.

CE QU'IL COMPARE. Le tempo publié par le moteur, instant par instant, contre le tempo lu
hors ligne au même instant. On ne compare pas deux médianes : c'est exactement l'erreur qui
a coûté une soirée, une médiane masquant treize pour cent d'excursions.

    dotnet run -c Release --project tools/Emotion.Probe -- morceau.wav 0 300 bpmtrace=moteur.txt
    python3 outils/tempo_reference.py morceau.wav rapport=reference/
    python3 outils/comparer.py reference/morceau.json moteur.txt
"""

import json
import sys


# Écart au-delà duquel on considère que le moteur s'est trompé, en pour cent.
#
# Deux pour cent valent 1,8 BPM à 88, soit un peu plus que le pas de publication du moteur
# (un BPM entier). En dessous, on mesurerait sa quantification et non ses erreurs.
TOLERANCE = 0.02


def lire_moteur(chemin):
    """Lit la trace du moteur : un instant en secondes et un tempo par ligne."""
    points = []
    with open(chemin, encoding="utf-8") as fh:
        for ligne in fh:
            morceaux = ligne.split()
            if len(morceaux) != 2:
                continue
            try:
                t = float(morceaux[0])
            except ValueError:
                continue
            bpm = None
            try:
                bpm = float(morceaux[1])
            except ValueError:
                bpm = None
            points.append((t, bpm))
    return points


def reference_a(trace, t):
    """Le tempo de référence à un instant, par le point le plus proche."""
    if not trace:
        return None
    meilleur = min(trace, key=lambda p: abs(p["t"] - t))
    # Au-delà d'une demi-fenêtre, la référence ne décrit plus cet instant-là.
    return meilleur["bpm"] if abs(meilleur["t"] - t) <= 10.0 else None


def comparer(chemin_ref, chemin_moteur):
    with open(chemin_ref, encoding="utf-8") as fh:
        ref = json.load(fh)

    moteur = lire_moteur(chemin_moteur)
    if not moteur:
        print("trace du moteur vide")
        return 1

    trace = ref["trace"]
    accord = desaccord = muet = sans_ref = 0
    episodes = []
    courant = None

    for t, bpm in moteur:
        cible = reference_a(trace, t)
        if cible is None:
            sans_ref += 1
            continue
        if bpm is None:
            muet += 1
            continue

        # On accepte les relations d'octave : annoncer la moitié ou le double d'un tempo
        # est une erreur de niveau métrique, pas une erreur de lecture, et elle ne se
        # corrige pas au même endroit. On la compte à part.
        ecarts = [abs(bpm - cible * k) / (cible * k) for k in (0.5, 1.0, 2.0)]
        if ecarts[1] <= TOLERANCE:
            accord += 1
            if courant:
                episodes.append(courant)
                courant = None
        else:
            desaccord += 1
            octave = min(ecarts[0], ecarts[2]) <= TOLERANCE
            if courant is None:
                courant = {"debut": t, "fin": t, "moteur": bpm,
                           "reference": cible, "octave": octave}
            else:
                courant["fin"] = t
    if courant:
        episodes.append(courant)

    juges = accord + desaccord
    print(f"{ref['fichier']}  —  {ref['duree']:.0f} s")
    print(f"  reference hors ligne : {ref['median']:.1f} BPM, "
          f"{ref['stable_2pc']:.0f} % des fenetres a 2 %")
    print()
    if juges:
        print(f"  accord      {100 * accord / juges:5.1f} %   ({accord} instants)")
        print(f"  desaccord   {100 * desaccord / juges:5.1f} %   ({desaccord} instants)")
    print(f"  moteur muet {muet} instants   ·   hors portee de la reference {sans_ref}")

    longs = [e for e in episodes if e["fin"] - e["debut"] >= 1.0]
    if longs:
        print(f"\n  {len(longs)} episodes de desaccord tenus plus d'une seconde :")
        for e in sorted(longs, key=lambda x: x["debut"])[:12]:
            marque = "  (octave)" if e["octave"] else ""
            print(f"    {e['debut']:6.1f} a {e['fin']:6.1f} s   "
                  f"moteur {e['moteur']:6.1f}   reference {e['reference']:6.1f}{marque}")
    elif juges:
        print("\n  aucun episode de desaccord tenu plus d'une seconde")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    sys.exit(comparer(sys.argv[1], sys.argv[2]))
