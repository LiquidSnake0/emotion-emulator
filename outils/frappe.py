#!/usr/bin/env python3
"""Entendre ce que le moteur retient comme frappe — ses erreurs comprises.

    python3 outils/frappe.py <morceau.wav> <dossier-cache> [bpm-de-la-fiche]

L'IDÉE EST DU DJ, ET ELLE COMBLE UN TROU QU'ON N'AVAIT PAS VU.

> « Pourquoi je peux isoler les sources mais pas la frappe qui fixe les BPM ? »

Elle n'était pas isolable parce qu'elle n'est pas une des six sources. Le kick ne sort pas de
la factorisation par timbre : il est détecté à part, par `OnsetDetector`, sur le registre
grave — c'est une **suite d'instants**, pas un timbre. Il n'avait donc aucune piste audio.

Or c'est la pièce dont tout dépend : le tempo, la grille, la phase. Et c'est aussi celle dont
le projet sait le moins de choses — « moins d'une frappe détectée sur deux tombe sur un
multiple du temps », et le plafond mesuré reste loin.

CE QUE CETTE PISTE FAIT ENTENDRE.

Le morceau, filtré sur le registre du kick, **coupé partout sauf aux instants que le détecteur
a retenus**. Ce n'est pas la batterie du morceau : c'est ce que le moteur en retient.

- un kick manqué s'entend comme un **trou** dans un train régulier ;
- une frappe inventée s'entend comme un **coup posé sur rien**, ou sur un bout de basse ;
- un décalage systématique s'entend comme un train qui traîne derrière la musique.

Aucun chiffre ne dit ça aussi vite. C'est le pendant exact de la piste « batterie » de la
référence : l'une donne ce qui frappe vraiment, l'autre ce que le moteur croit voir, et
basculer de l'une à l'autre est la mesure la plus directe qu'on ait jamais eue sur le
détecteur.
"""

import os
import subprocess
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tempo_reference import lire_mono

# Le registre du kick, tel que le moteur le regarde. Cinq bandes, 30 a 410 Hz : la valeur est
# mesuree et documentee dans CLAUDE.md (R de 0,238 a 0,286 en passant de trois a cinq bandes).
GRAVE_HZ, AIGU_HZ = 30.0, 410.0

# La porte autour de chaque instant. Un kick dure de cent a deux cents millisecondes ; on
# prend large avant pour ne pas raboter l'attaque — c'est elle qu'on veut juger — et l'on
# ferme vite apres.
AVANT_S, APRES_S = 0.020, 0.130
FONDU_S = 0.004          # de quoi eviter un clic a chaque ouverture, et rien de plus

EPS = 1e-9


def instants(morceau, dossier, bpm=None, dire=print):
    """Les instants de frappe du moteur, par la sonde — qui n'ouvre aucun port.

    On analyse le morceau ENTIER et non les quatre-vingt-dix premieres secondes : une piste
    qui s'arreterait au milieu ferait croire a un decrochage du detecteur.
    """
    base = os.path.splitext(os.path.basename(morceau))[0]
    prefixe = os.path.join(dossier, base)
    fichier = f"{prefixe}-kicks.txt"
    if os.path.exists(fichier):
        dire("instants deja en cache")
    else:
        os.makedirs(dossier, exist_ok=True)
        with wave.open(morceau) as f:
            duree = f.getnframes() / f.getframerate()
        cmd = ["dotnet", "run", "-c", "Release", "--no-build",
               "--project", "tools/Emotion.Probe", "--",
               morceau, "0", f"{duree + 1:.0f}"]
        if bpm:
            cmd.append(f"fiche={bpm}")
        cmd.append(f"instants={prefixe}")
        dire("la sonde releve les frappes du moteur…")
        r = subprocess.run(cmd, capture_output=True, text=True,
                           cwd=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        if r.returncode != 0 or not os.path.exists(fichier):
            dire(f"la sonde a echoue : {(r.stderr or r.stdout).strip().splitlines()[-1:]}")
            return None
    with open(fichier, encoding="utf-8") as fh:
        return [float(l) for l in fh if l.strip()]


def registre(x, taux):
    """Le registre du kick, a phase nulle.

    A PHASE NULLE, ET CE N'EST PAS UN LUXE. Un filtre recursif deplacerait ce qu'il mesure —
    le projet s'est deja fait prendre a soixante-dix-sept millisecondes pres en repliant
    l'energie d'une grosse caisse. Ici on juge des instants : un retard introduit par le
    filtre serait pris pour un retard du detecteur.
    """
    sp = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / taux)
    # Des flancs doux plutot qu'une coupe franche : une coupe rectangulaire en frequence
    # etale le temps, et l'on entendrait sonner ce qui devrait claquer.
    gain = np.clip((f - GRAVE_HZ / 2) / max(EPS, GRAVE_HZ / 2), 0, 1)
    gain *= np.clip((AIGU_HZ * 1.5 - f) / max(EPS, AIGU_HZ / 2), 0, 1)
    return np.fft.irfft(sp * gain, len(x))


def porte(n, taux, temps):
    """Ouvert autour de chaque instant, ferme partout ailleurs."""
    g = np.zeros(n)
    avant, apres = int(AVANT_S * taux), int(APRES_S * taux)
    fondu = max(1, int(FONDU_S * taux))
    montee = np.linspace(0, 1, fondu)
    for t in temps:
        i = int(t * taux)
        a, b = max(0, i - avant), min(n, i + apres)
        if b <= a:
            continue
        g[a:b] = 1.0
        g[a:a + min(fondu, b - a)] = montee[:min(fondu, b - a)]
        g[max(a, b - fondu):b] = montee[:b - max(a, b - fondu)][::-1]
    return g


def piste(morceau, dossier, bpm=None, dire=print):
    """Écrit la piste et rend son chemin."""
    base = os.path.splitext(os.path.basename(morceau))[0]
    sortie = os.path.join(dossier, f"{base}-frappe.wav")
    if os.path.exists(sortie):
        dire("piste de frappe deja en cache")
        return sortie

    temps = instants(morceau, os.path.join(dossier, "instants"), bpm, dire=dire)
    if not temps:
        dire("aucune frappe relevee")
        return None

    x, taux = lire_mono(morceau)
    y = registre(x.astype(np.float64), taux) * porte(len(x), taux, temps)

    # ON REMONTE LE NIVEAU, ET L'ON DIT POURQUOI. Le registre du kick seul, porte a dix pour
    # cent du temps, s'entend beaucoup plus bas que les six sources : a fader egal on croirait
    # que le detecteur ne trouve rien. On normalise donc CETTE piste-la sur sa propre crete —
    # c'est la seule du lot, et c'est assume : ce qu'on juge ici est un rythme, pas un niveau.
    crete = np.max(np.abs(y))
    if crete > EPS:
        y *= 0.9 / crete

    with wave.open(sortie, "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(taux)
        f.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes())
    dire(f"{len(temps)} frappes, une toutes les {len(x) / taux / max(1, len(temps)):.2f} s")
    return sortie


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    ok = piste(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
    sys.exit(0 if ok else 1)
