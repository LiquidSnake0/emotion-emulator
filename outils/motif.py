#!/usr/bin/env python3
"""Un motif se répète-t-il assez pour qu'on puisse le reconnaître ?

CE QUE CETTE MESURE DÉCIDE, ET POURQUOI ELLE EXISTE AVANT TOUT CODE.

Le DJ décrit une figure de piano — huit notes — qui revient plus tard dans le morceau, et
l'analogie qu'il en tire est exacte : « c'est comme si on analysait une chanson avec des
paroles, on arrive à reconnaître le refrain qui est en quatrain ; dès la première écoute on
a cet indice, puis quand on l'entend une deuxième fois on sait que c'est un refrain. »

Construire cela dans le moteur coûte cher. On mesure donc D'ABORD, hors ligne, en Python, et
sans toucher au moteur : si la répétition ne se voit pas ici, elle ne se verra pas là-bas.

LA QUESTION, PRÉCISÉMENT. En découpant le morceau en mesures et en comparant chaque mesure
à celle qui la suit L mesures plus loin, voit-on un L qui ressort ? Et une BANDE SEULE
ressort-elle mieux que le mélange — c'est-à-dire l'idée que le piano se répète même quand
le reste change ?

LE NIVEAU DE HASARD NE SE DEVINE PAS, IL SE TIRE. On rebat les mesures dans un ordre
quelconque et l'on recalcule : ce que donne un morceau dont on a détruit l'ordre EST le
hasard, sans hypothèse sur sa distribution. Deux cents tirages donnent un centile
utilisable.

UNE PREMIÈRE VERSION A PASSÉ SON CRITÈRE ET NE VALAIT RIEN. Elle décrivait chaque mesure
par la moyenne de douze bandes sur toute sa durée. Résultat : neuf morceaux sur dix
« passaient » — avec des scores de 0,96 à 0,996 pour un hasard de 0,93 à 0,994. Tout
ressemblait à tout, la marge valait cinq millièmes, et le décalage gagnant était presque
toujours UN, c'est-à-dire « une mesure ressemble à la suivante ».

    Moyenner sur la mesure détruit exactement ce qui fait un motif : sa forme DANS LE TEMPS.
    Deux mesures où l'on joue des choses différentes ont le même spectre moyen.

    Et le critère était trop facile à passer, ce qui est le défaut le plus dangereux d'une
    mesure : elle donnait raison sans rien prouver. Une métrique se vérifie comme un
    algorithme.

CE QUE CETTE VERSION FAIT AUTREMENT. La signature d'une mesure est une grille de seize pas
sur douze bandes — la double croche, l'unité où un motif se lit — centrée et normée. Le
score est rendu en ÉCARTS-TYPES au-dessus du hasard, ce qui est sans dimension et comparable
d'un morceau à l'autre. Et le décalage d'une mesure est écarté : un motif qui se répète à
chaque mesure ne se distingue pas d'une texture constante.

    CRITÈRE, FIXÉ AVANT LA MESURE
    Le score vrai doit dépasser le hasard de TROIS ÉCARTS-TYPES sur AU MOINS LA MOITIÉ des
    morceaux, à un décalage d'au moins deux mesures. En dessous, l'idée est morte et l'on
    n'écrit rien dans le moteur.

    python3 outils/motif.py
"""

import glob
import math
import os
import statistics
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from noter_detecteur import lire
from tempo_reference import HOP, NFFT, lire_mono

# Décalages examinés, en mesures. DEUX ET NON UN : un motif qui se répète à chaque mesure ne
# se distingue pas d'une texture constante, et c'est lui qui gagnait systématiquement dans la
# première version. Au-delà de huit on décrirait la section, ce que SectionTracker fait déjà.
LAGS = range(2, 9)

# Pas par mesure dans la signature. Seize : la double croche, l'unité où un motif se lit.
PAS = 16

# Temps par mesure. Le répertoire est en quatre.
PAR_MESURE = 4

# Tirages du hasard. Deux cents suffisent pour un 95e centile stable.
TIRAGES = 200

# Douze bandes logarithmiques, comme le moteur.
BANDES = 12


def signatures(chemin, periode):
    """Une signature par mesure : seize pas sur douze bandes, centrée.

    LA FORME DANS LE TEMPS, ET NON SEULEMENT LA COULEUR. Une moyenne sur la mesure entière
    dit ce qui sonne, jamais QUAND — et un motif est précisément un agencement dans le
    temps. Seize pas donnent la double croche, ce qui suffit à distinguer deux figures
    jouées avec le même instrument.

    Centrée, parce que ce qui compte est la forme et non le volume : deux mesures jouées à
    des niveaux différents doivent se ressembler si l'on y joue la même chose.
    """
    x, rate = lire_mono(chemin)
    f = np.hanning(NFFT).astype(np.float32)
    n = 1 + (len(x) - NFFT) // HOP
    tr = np.lib.stride_tricks.sliding_window_view(x, NFFT)[::HOP][:n]
    sp = np.abs(np.fft.rfft(tr * f, axis=1))
    sp = np.log1p(sp * 8.0)

    bornes = np.geomspace(30.0, 16_000.0, BANDES + 1) / (rate / NFFT)
    bandes = np.stack([sp[:, int(bornes[i]):max(int(bornes[i]) + 1, int(bornes[i + 1]))].mean(1)
                       for i in range(BANDES)], axis=1)

    taux = rate / HOP
    par_mesure = periode * PAR_MESURE
    mesures = int(len(bandes) / taux / par_mesure)
    if mesures < 12:
        return None

    sig = np.zeros((mesures, PAS * BANDES), np.float64)
    for m in range(mesures):
        grille = np.zeros((PAS, BANDES), np.float64)
        for k in range(PAS):
            i0 = int((m + k / PAS) * par_mesure * taux)
            i1 = max(i0 + 1, int((m + (k + 1) / PAS) * par_mesure * taux))
            if i1 <= len(bandes):
                grille[k] = bandes[i0:i1].mean(0)
        sig[m] = grille.reshape(-1)
    return sig


def ressemblance(sig, lag):
    """Ressemblance moyenne entre chaque mesure et celle qui la suit de `lag` mesures."""
    if len(sig) <= lag + 2:
        return 0.0
    a, b = sig[:-lag], sig[lag:]
    a = a - a.mean(1, keepdims=True)
    b = b - b.mean(1, keepdims=True)
    na = np.linalg.norm(a, axis=1)
    nb = np.linalg.norm(b, axis=1)
    bon = (na > 1e-9) & (nb > 1e-9)
    if bon.sum() < 4:
        return 0.0
    return float(((a[bon] * b[bon]).sum(1) / (na[bon] * nb[bon])).mean())


def meilleur(sig):
    """Le décalage qui ressort le plus, et son score."""
    scores = {l: ressemblance(sig, l) for l in LAGS}
    l = max(scores, key=scores.get)
    return l, scores[l], scores


def relief(scores):
    """De combien le décalage gagnant dépasse les AUTRES DÉCALAGES, en écarts-types.

    POURQUOI CE SECOND JUGE, ALORS QU'ON EN A DÉJÀ UN.

    Le brassage des mesures détruit tout ordre, y compris une dérive lente : un morceau qui
    monte régulièrement en énergie ressemblerait à lui-même à TOUS les décalages, et le
    brassage ferait tomber ce score. Le z serait alors élevé sans qu'aucun motif ne se
    répète — on aurait mesuré la dérive du morceau.

    Ce juge-ci est insensible à cela : une dérive élève tous les décalages ensemble et ne
    creuse donc aucun relief. Seul un décalage qui ressort DE SES VOISINS est un motif.

    Les deux ne se remplacent pas. Le premier dit « il y a de l'ordre », le second « cet
    ordre a une période ».
    """
    if len(scores) < 3:
        return 0.0
    valeurs = sorted(scores.values())
    sommet = valeurs[-1]
    autres = valeurs[:-1]
    m = statistics.mean(autres)
    e = statistics.pstdev(autres) + 1e-9
    return (sommet - m) / e


def hasard(sig, alea):
    """Le hasard, tiré : moyenne et écart-type de ce que donne l'ordre détruit.

    ON REND UN ÉCART-TYPE ET NON UN CENTILE. Un centile dit « au-dessus » ou « en dessous » ;
    un écart-type dit DE COMBIEN, ce qui est la seule façon de comparer deux morceaux dont
    les niveaux de ressemblance n'ont rien à voir. La première version comparait à un 95e
    centile et déclarait neuf victoires pour cinq millièmes de marge.
    """
    pires = []
    for _ in range(TIRAGES):
        melange = sig[alea.permutation(len(sig))]
        pires.append(max(ressemblance(melange, l) for l in LAGS))
    return float(np.mean(pires)), float(np.std(pires) + 1e-9)


def analyser(nom, chemin, periode, alea):
    sig = signatures(chemin, periode)
    if sig is None:
        return None

    # Le mélange : les douze bandes ensemble.
    l, score, scores = meilleur(sig)
    moy, ecart = hasard(sig, alea)
    z = (score - moy) / ecart
    rel = relief(scores)

    # Chaque bande seule, sur ses seize pas. C'est l'hypothèse du DJ : le piano se répète
    # même quand le reste change, donc une bande devrait ressortir mieux que le tout.
    par_bande = []
    for b in range(BANDES):
        colonnes = [k * BANDES + b for k in range(PAS)]
        seule = sig[:, colonnes]
        lb, sb, sc = meilleur(seule)
        mb, eb = hasard(seule, alea)
        par_bande.append((b, lb, (sb - mb) / eb, relief(sc)))

    # On retient la bande sur les DEUX juges, et non sur le premier seul : une bande qui
    # passerait l'un et pas l'autre ne prouve rien.
    mieux = max(par_bande, key=lambda x: min(x[2], x[3]))
    return {"nom": nom, "mesures": len(sig), "lag": l, "score": score, "z": z,
            "relief": rel, "bande": mieux}


if __name__ == "__main__":
    # LE BAC DE MESURE SE DONNE, IL NE SE DEVINE PAS. Un chemin de session fige ici ne
    # marchait que sur une machine et un jour donnes — et il n'a rien a faire dans un depot
    # public, ou il se lit comme un residu.
    S = os.environ.get("EMOTION_BAC") or (
        sys.argv[1] if len(sys.argv) > 1 else
        os.path.expanduser("~/.cache/emotion-emulator/bac"))
    if not os.path.isdir(S):
        print(f"bac introuvable : {S}\n"
              f"    EMOTION_BAC=/chemin/vers/les/wav python3 outils/motif.py")
        sys.exit(1)
    FICHIERS = [("macro", f"{S}/macro.wav")] + [
        (f"t{n:02d}", f"{S}/valid/t{n:02d}.wav") for n in (2, 3, 4, 5, 6, 8, 9, 10, 11)]

    alea = np.random.default_rng(20260909)
    print(f"{'morceau':8s} {'mes.':>5s}  {'--- les douze bandes ---':^24s}  "
          f"{'--- la meilleure bande ---':^26s}")
    print(f"{'':8s} {'':>5s}  {'lag':>4s} {'z':>6s} {'relief':>7s} {'':>4s}  "
          f"{'bande':>6s} {'lag':>4s} {'z':>6s} {'relief':>7s}")
    passe = passeb = total = 0
    for nom, chemin in FICHIERS:
        v = lire(f"{S}/verite/{nom}.txt")
        periode = statistics.median(b - a for a, b in zip(v, v[1:]))
        r = analyser(nom, chemin, periode, alea)
        if r is None:
            print(f"{nom:8s} trop court"); continue
        total += 1
        ok = r["z"] >= 3.0 and r["relief"] >= 3.0
        passe += ok
        b, lb, zb, rb = r["bande"]
        okb = zb >= 3.0 and rb >= 3.0
        passeb += okb
        print(f"{r['nom']:8s} {r['mesures']:5d}  {r['lag']:4d} {r['z']:6.1f} "
              f"{r['relief']:7.1f} {'OUI' if ok else 'non':>4s}  "
              f"{b:6d} {lb:4d} {zb:6.1f} {rb:7.1f} {'OUI' if okb else 'non'}")
    print(f"\nCRITERE : trois ecarts-types au-dessus du hasard ET trois de relief sur les "
          f"autres decalages,\n          sur au moins la moitie des morceaux.")
    print(f"  les douze bandes ensemble : {passe}/{total}")
    print(f"  la meilleure bande seule  : {passeb}/{total}")
