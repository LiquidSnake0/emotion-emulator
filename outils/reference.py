#!/usr/bin/env python3
"""Ce que le morceau contient vraiment, dit par un algorithme qui n'est pas le nôtre.

    python3 outils/reference.py <morceau.wav> <dossier-cache>

POURQUOI UNE RÉFÉRENCE EXTÉRIEURE, ET POURQUOI CELLE-CI.

Le projet sait mesurer si une source est régulière. Il ne sait pas dire **ce qu'elle
contient**, et la raison est écrite depuis longtemps dans `outils/LISEZMOI.md` : contrôler la
séparation demanderait de réécrire la même factorisation, donc de vérifier le moteur avec le
moteur. C'est la circularité que ce projet passe son temps à fuir.

Demucs en sort. C'est un modèle entraîné, d'une famille d'algorithmes totalement différente,
et il rend quatre pistes **nommées** : batterie, basse, voix, reste. Exactement le rôle
qu'`aubioonset` joue déjà pour les attaques — un juge qu'on n'a pas écrit.

> « Je voulais que tu analyses un son de bout en bout, que tu sépares en différentes sources,
>   et que la fenêtre du temps réel se compare à ce qui a été calculé au préalable. »

CE QUE CETTE RÉFÉRENCE N'EST PAS.

**Elle ne fait pas tourner le moteur.** Un set n'est pas déterminé : un bonus track jamais
analysé peut tomber à n'importe quel moment, et le temps réel doit marcher sans rien savoir
de lui. `SourceSeparator` apprend ses profils en écoutant, en quelques secondes, et n'a
jamais besoin d'un pré-calcul. Ce fichier ne sert qu'à **noter** ce que le moteur trouve,
sur les disques qu'on a pris le temps d'analyser.

**Elle n'est pas non plus la vérité.** Demucs est entraîné sur de la pop et de la soul ; le
barber beats étouffe et filtre tout, et rien ne garantit que sa « batterie » soit celle qu'on
entend. On rend donc les pistes ET les correspondances, et c'est l'oreille qui tranche en cas
de désaccord — c'est à ça que sert la touche espace de la fenêtre, en dernier recours.

LA COMPARAISON EST DIRECTE, ET C'EST UNE CHANCE.

Nos six sources et les quatre stems sont tirés du **même** morceau : ils en partagent la
phase, échantillon par échantillon. On peut donc les corréler dans le temps sans rien aligner
— pas d'enveloppe, pas de fenêtre, pas de tolérance à choisir. Une corrélation de Pearson
suffit, et elle dit tout : deux signaux qui portent le même instrument montent et descendent
ensemble, ceux qui n'ont rien à voir donnent zéro.
"""

import json
import os
import subprocess
import sys
import wave

import numpy as np

# LE VENV D'A COTE, ET IL N'EST PAS UNE DEPENDANCE DU PROJET.
#
# PyTorch pese pres d'un gigaoctet. Le mettre dans les dependances du depot pour un outil de
# mesure hors ligne serait faux : le moteur, la fenetre et le rendu n'en ont aucun besoin, et
# `outils/LISEZMOI.md` promet trois dependances. Il vit donc dans un venv du cache, comme
# `aubio` vit dans le systeme — dehors.
VENV = os.path.expanduser("~/.cache/emotion-emulator/venv-reference")

# Le modele. `htdemucs` est celui par defaut : quatre pistes, et il tourne sur processeur.
MODELE = "htdemucs"
PISTES = ("drums", "bass", "vocals", "other")

# Les noms qu'on emploie ici. « other » ne veut rien dire a l'oreille d'un DJ ; « le reste »
# non plus, mais au moins il ne pretend pas nommer un instrument.
NOMS = {"drums": "batterie", "bass": "basse", "vocals": "voix", "other": "le reste"}

EPS = 1e-9


def dispo():
    """Le venv existe-t-il et sait-il faire tourner Demucs ?"""
    py = os.path.join(VENV, "bin", "python")
    if not os.path.exists(py):
        return False
    r = subprocess.run([py, "-c", "import demucs"], capture_output=True)
    return r.returncode == 0


def separer(morceau, dossier, dire=print):
    """Les quatre pistes nommées, en mono 48 kHz, mises en cache.

    Le mono et les 48 kHz ne sont pas un détail : c'est la grille du moteur, et comparer deux
    signaux qui ne sont pas sur la même grille demanderait un rééchantillonnage, donc une
    interpolation, donc une différence qu'on aurait fabriquée nous-mêmes.
    """
    base = os.path.splitext(os.path.basename(morceau))[0]
    sorties = {p: os.path.join(dossier, f"{base}-ref-{p}.wav") for p in PISTES}
    if all(os.path.exists(c) for c in sorties.values()):
        dire("reference deja en cache")
        return sorties

    if not dispo():
        dire("demucs absent : pas de reference")
        return None

    os.makedirs(dossier, exist_ok=True)
    brut = os.path.join(dossier, "demucs")
    dire("separation de reference (plusieurs minutes, une seule fois par disque)…")
    r = subprocess.run(
        [os.path.join(VENV, "bin", "python"), "-m", "demucs",
         "-n", MODELE, "-o", brut, "--filename", "{stem}.{ext}", morceau],
        capture_output=True, text=True)
    if r.returncode != 0:
        dire(f"demucs a echoue : {(r.stderr or r.stdout).strip().splitlines()[-1:]}")
        return None

    ou = os.path.join(brut, MODELE)
    for p in PISTES:
        src = os.path.join(ou, f"{p}.wav")
        if not os.path.exists(src):
            dire(f"piste {p} manquante")
            return None
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", src,
                        "-ac", "1", "-ar", "48000", sorties[p]], check=True)
        os.remove(src)
    dire("reference prete")
    return sorties


def lire(chemin):
    with wave.open(chemin) as f:
        x = np.frombuffer(f.readframes(f.getnframes()), "<i2").astype(np.float64)
    return x


def correspondances(sources, references, dire=print):
    """Pour chaque source du moteur, quelle PART d'elle chaque piste nommée explique.

    UNE CORRELATION SIMPLE NE MARCHE PAS ICI, ET LA MESURE L'A MONTRE TOUT DE SUITE.

    Premier essai : pour chaque source, le stem le mieux correle. Verdict rendu : « source 1
    basse, source 6 basse » — alors que la source 6 est la plus AIGUE du lot.

    LA CAUSE N'EST PAS CELLE QU'ON A CRU D'ABORD, et le premier diagnostic etait faux. On
    avait conclu que « `bass` n'est pas une basse » sur la foi d'un centre de gravite a
    452 Hz. C'etait NOTRE mesure qui se trompait : un centre pondere par l'amplitude est tire
    vers le haut par le souffle des aigus. La repartition d'ENERGIE dit l'inverse —

        bass    99,6 % sous 150 Hz      other    63,6 % entre 500 et 2000
        drums   78,6 % sous 150 Hz      vocals   96,9 % entre 500 et 2000

    — et sur `etalon-kick.wav`, des grosses caisses seules, Demucs met 99,9 % dans `drums`.
    **Il separe proprement ce repertoire, et ses noms tiennent.**

    La vraie cause est ailleurs : **76,8 % de l'energie de ce morceau est sous 150 Hz.** Une
    comparaison en energie demande donc surtout « cette source contient-elle du grave », et le
    grave ecrase tout le reste. Le resultat penche vers `bass` par construction.

    CE BIAIS EST CONNU ET NON CORRIGE. Une comparaison bande par bande le leverait ; ecrite
    une premiere fois, elle demandait un gigaoctet et demi de memoire et s'est fait tuer. Elle
    reste a faire. En attendant, **les verdicts de sources graves sont a lire avec cette
    reserve** — et c'est ecrit ici plutot que tu decouvert plus tard.

    ON REGRESSE DONC AU LIEU DE CORRELER. On cherche les coefficients qui reconstruisent au
    mieux la source a partir des quatre pistes, et l'on regarde ce que chacune apporte. Un
    stem qui contient tout n'y gagne rien : ce qui compte est ce qu'il explique **en plus**
    des autres. C'est la meme correction que celle du relief dans `MotifTracker` — un rival
    qui ressemble a tout le monde n'est pas un rival.
    """
    noms = list(references)
    refs = [lire(references[n]) for n in noms]
    n = min(len(r) for r in refs)

    # La matrice de Gram des references, calculee une fois : c'est elle qui porte le fait que
    # deux pistes se recouvrent, et donc qui empeche l'une de reclamer ce qui est a l'autre.
    R = np.stack([r[:n] - r[:n].mean() for r in refs])
    G = R @ R.T
    G += np.eye(len(noms)) * (np.trace(G) / len(noms)) * 1e-6   # rien de singulier

    resultat = {}
    for rang, chemin in enumerate(sources, start=1):
        s = lire(chemin)
        m = min(n, len(s))
        y = s[:m] - s[:m].mean()
        coef = np.linalg.solve(G[:, :][:, :], R[:, :m] @ y)

        # LA PART EXPLIQUEE, PISTE PAR PISTE. Le produit du coefficient par la covariance :
        # c'est ce que cette piste-la apporte a la reconstruction, rapporte a l'energie de la
        # source. Une piste qui n'apporte rien rend zero, meme si elle contient le morceau.
        apport = coef * (R[:, :m] @ y)
        total = float(y @ y)
        parts = {noms[i]: max(0.0, float(apport[i]) / max(EPS, total)) for i in range(len(noms))}

        gagnant = max(parts, key=parts.get)
        ordonnes = sorted(parts.values(), reverse=True)
        resultat[rang] = {
            "ressemble": gagnant,
            "nom": NOMS[gagnant],
            "parts": {k: round(v, 3) for k, v in parts.items()},
            "explique": round(sum(parts.values()), 3),
            # LA MARGE DIT SI LE VERDICT VAUT QUELQUE CHOSE. Une source qui tient autant de la
            # batterie que de la basse n'a pas ete separee : elle en contient deux, et
            # annoncer la premiere serait mentir par arrondi.
            "marge": round(ordonnes[0] - ordonnes[1], 3),
        }
        dire(f"  source {rang} : {NOMS[gagnant]:9s} {100*parts[gagnant]:5.1f} %  "
             f"(marge {100*resultat[rang]['marge']:+5.1f} pts, "
             f"{100*resultat[rang]['explique']:.0f} % explique)")

    # LA LECTURE INVERSE, ET C'EST ELLE QUI REPOND A LA QUESTION DU DJ. « La source qui
    # s'occupe du piano gere-t-elle bien le piano » ne se demande pas source par source mais
    # instrument par instrument : quelle case suit la batterie ?
    dire("")
    for nom in noms:
        meilleur = max(resultat, key=lambda r: resultat[r]["parts"][nom])
        part = resultat[meilleur]["parts"][nom]
        dire(f"  {NOMS[nom]:9s} -> source {meilleur} ({100*part:.0f} %)"
             + ("" if part > 0.15 else "   — personne ne la porte vraiment"))
    return resultat


def noter(morceau, dossier_sources, dossier_ref, dire=print):
    """Sépare la référence, compare, écrit le verdict à côté des pistes."""
    refs = separer(morceau, dossier_ref, dire=dire)
    if refs is None:
        return None
    base = os.path.splitext(os.path.basename(morceau))[0]
    sources = [os.path.join(dossier_sources, f"{base}-{i + 1}.wav") for i in range(6)]
    if not all(os.path.exists(c) for c in sources):
        dire("les six pistes du moteur ne sont pas la")
        return None

    verdict = correspondances(sources, refs, dire=dire)
    chemin = os.path.join(dossier_sources, f"{base}.correspondances.json")
    with open(chemin, "w", encoding="utf-8") as fh:
        json.dump({"morceau": base, "correspondances": verdict,
                   "references": {k: os.path.basename(v) for k, v in refs.items()}},
                  fh, ensure_ascii=False, indent=1)
    return verdict


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    cache = sys.argv[2]
    ok = noter(sys.argv[1], cache, os.path.join(cache, "reference"))
    sys.exit(0 if ok else 1)
