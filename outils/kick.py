#!/usr/bin/env python3
"""Le « boom » et le « tchak », separes, depuis la batterie de reference.

    python3 outils/kick.py <morceau.wav> <dossier-cache> [bpm]

POURQUOI ON COMMENCE PAR LA.

> « On va faire ça : tu m'extrais d'abord le kick. "boom tchak boom tchak" = 4 temps ?
>   Les sonorités viennent après. »

Oui : boom-tchak-boom-tchak fait quatre temps — le kick sur 1 et 3, le tchak sur 2 et 4.
C'est une mesure complète, et c'est la seule chose du morceau dont la place soit connue
d'avance.

CE QUI REND CETTE EXTRACTION-LA DIFFERENTE DES SIX SOURCES.

Les six sources sortent d'une factorisation qui cherche des objets sans savoir ce qu'elle
cherche — et la journee a montre ce que ca donne sur ce repertoire. Le kick, lui, n'a pas
besoin d'etre trouve : on sait ou il vit (sous cent cinquante hertz), on sait comment il
sonne (une attaque suivie d'une chute), et un juge exterieur nous donne deja la batterie
seule. Il n'y a plus qu'a la couper en deux.

  boom    la batterie sous 150 Hz     — le kick, ce qui fixe le tempo
  tchak   la batterie au-dessus       — le clap, la caisse claire, les charleys

Les deux se recomposent exactement : leur somme est la batterie de depart.

LE FILTRE EST A PHASE NULLE, et ce n'est pas un detail. Un filtre recursif deplacerait ce
qu'il mesure — le projet s'est deja fait prendre a soixante-dix-sept millisecondes pres en
repliant l'energie d'une grosse caisse. Ici on juge des INSTANTS : un retard introduit par le
filtre serait pris pour un retard du morceau.
"""

import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# La coupure entre le boom et le tchak. Cent cinquante hertz : c'est la borne que tout ce
# projet emploie pour le registre du kick, et sur ce repertoire la batterie de reference y
# met soixante-seize pour cent de son energie.
COUPURE_HZ = 150.0
PENTE_HZ = 60.0          # la largeur du flanc : franc en frequence sonne en temps

EPS = 1e-12


def lire(chemin):
    with wave.open(chemin) as f:
        return (np.frombuffer(f.readframes(f.getnframes()), "<i2").astype(np.float64),
                f.getframerate())


def ecrire(chemin, x, taux):
    crete = np.max(np.abs(x))
    y = x / crete * 0.9 if crete > EPS else x
    with wave.open(chemin, "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(taux)
        f.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes())


def couper(x, taux):
    """Rend (grave, aigu), a phase nulle, dont la somme est exactement l'entree."""
    sp = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / taux)
    bas = np.clip((COUPURE_HZ + PENTE_HZ / 2 - f) / PENTE_HZ, 0.0, 1.0)
    return np.fft.irfft(sp * bas, len(x)), np.fft.irfft(sp * (1.0 - bas), len(x))


def periode(x, taux, bpm=None):
    """A quel intervalle ce signal frappe, par autocorrelation de son enveloppe.

    COMPTER LES SOMMETS NE MARCHE PAS, ET LA MESURE L'A DIT TOUT DE SUITE. Premiere version :
    on relevait les debuts de plage forte avec un ecart minimal d'un huitieme de seconde. Elle
    rendait « un coup toutes les 0,49 temps » — exactement la moitie, c'est-a-dire que chaque
    frappe etait comptee deux fois, son attaque puis son rebond.

    L'autocorrelation ne compte rien : elle demande a quel decalage l'enveloppe ressemble le
    plus a elle-meme. Une frappe qui rebondit ressemble a elle-meme a la periode du rythme,
    pas a celle du rebond. C'est le meme raisonnement qui a fait abandonner le vote sur les
    ecarts entre attaques consecutives dans `TempoTracker` — « une frappe manquee double
    l'ecart, une frappe parasite le coupe en deux ».
    """
    pas = max(1, taux // 200)
    e = np.abs(x[:len(x) // pas * pas]).reshape(-1, pas).mean(1)
    e = e - e.mean()
    if len(e) < 400:
        return None, None
    n = len(e)
    sp = np.fft.rfft(e, 2 * n)
    ac = np.fft.irfft(sp * np.conj(sp))[:n]
    ac /= max(EPS, ac[0])

    # On cherche entre un quart de seconde et quatre secondes : de la double croche rapide a
    # la mesure entiere d'un morceau lent.
    lo, hi = int(0.25 * 200), min(n - 1, int(4.0 * 200))
    fenetre = ac[lo:hi]
    if len(fenetre) < 8:
        return None, None
    i = int(np.argmax(fenetre)) + lo
    return i / 200.0, float(ac[i])


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    morceau, cache = sys.argv[1], sys.argv[2]
    bpm = float(sys.argv[3]) if len(sys.argv) > 3 else None
    base = os.path.splitext(os.path.basename(morceau))[0]

    batterie = os.path.join(cache, "reference", f"{base}-ref-drums.wav")
    if not os.path.exists(batterie):
        print(f"pas de batterie de reference pour {base}\n"
              f"    ./outils/preparer.sh <la piste>   la calcule (Demucs, cinq minutes)")
        return 1

    x, taux = lire(batterie)
    boom, tchak = couper(x, taux)

    dossier = os.path.join(cache, "kick")
    os.makedirs(dossier, exist_ok=True)
    for nom, y in (("boom", boom), ("tchak", tchak)):
        ecrire(os.path.join(dossier, f"{base}-{nom}.wav"), y, taux)

    # CONTROLE : les deux moities se recomposent-elles ?
    err = (boom + tchak) - x
    snr = 10 * np.log10(np.sum(x ** 2) / max(EPS, np.sum(err ** 2)))
    print(f"  boom + tchak = la batterie   {snr:.0f} dB")

    part = 100 * np.sum(boom ** 2) / max(EPS, np.sum(boom ** 2) + np.sum(tchak ** 2))
    print(f"  le boom pese {part:.0f} % de la batterie")

    print()
    for nom, y in (("boom", boom), ("tchak", tchak)):
        p, force = periode(y, taux, bpm)
        if p is None:
            print(f"  {nom:6s} trop court pour conclure")
            continue
        ligne = f"  {nom:6s} frappe toutes les {p:.3f} s   (ressemblance {force:.2f})"
        if bpm:
            temps = 60.0 / bpm
            # EN TEMPS, ET NON EN SECONDES. C'est la seule facon de voir si le coup tombe sur
            # la mesure : « toutes les 1,38 s » ne dit rien, « tous les deux temps » dit tout.
            n = p / temps
            nom_musical = {0.5: "la croche", 1.0: "le temps", 2.0: "deux temps",
                           4.0: "la mesure entiere"}.get(round(n * 2) / 2, "")
            ligne += f"   soit {n:.2f} temps a {bpm:g} BPM"
            if nom_musical:
                ligne += f" — {nom_musical}"
        print(ligne)
    print(f"\n  {dossier}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
