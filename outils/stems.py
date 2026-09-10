#!/usr/bin/env python3
"""Les six pistes d'un morceau, taillées sur les profils de la session en cours.

    python3 outils/stems.py <morceau> <dossier-cache> [url-du-moteur]

POURQUOI LES PROFILS VIENNENT DU MOTEUR QUI TOURNE, ET NON D'UNE EXTRACTION FAITE LA VEILLE.

On pourrait extraire l'album une fois pour toutes. Ce serait faux, et la mesure le dit : deux
apprentissages du même morceau trouvent bien les mêmes six objets — cosinus 0,77 à 0,91 sous
le meilleur appariement — mais **un à trois rangs sur six seulement sont conservés**. L'ordre
est celui des centres de gravité spectraux ; il suffit que deux sources voisines se croisent
pour que tout glisse d'un cran.

Autrement dit, la « source 3 » d'un fichier extrait hier n'est pas la case 3 que l'écran
montre aujourd'hui. On entendrait une source en en jugeant une autre, **sans que rien ne le
signale** — exactement la fausse preuve qu'un outil de validation ne doit jamais produire.

D'où `/profils` : le moteur rend ce que CETTE session a appris, et les six pistes portent
alors les mêmes numéros que les six cases.

CE QU'IL FAUT ATTENDRE, ET POURQUOI ON NE PEUT PAS L'ÉVITER.

La séparation apprend en écoutant : ses profils ne valent rien tant qu'elle n'a pas entendu
assez de musique (`pret` reste faux). On attend donc que le moteur le dise, puis on extrait.
Pendant ce temps le morceau joue déjà — l'attente est masquée, pas supprimée.
"""

import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import extraire

MOTEUR = "http://localhost:5099"

# Combien de temps on laisse la separation apprendre avant d'abandonner. Elle se declare
# prete apres deux cents images ou la source joue, soit quelques secondes de musique reelle
# — mais un morceau qui demarre par du silence peut en demander bien plus.
ATTENTE_MAX_S = 90.0


def profils(url=MOTEUR, attente=ATTENTE_MAX_S, dire=print):
    """Les six profils de la session, une fois que le moteur a appris quelque chose."""
    debut = time.time()
    annonce = False
    while time.time() - debut < attente:
        try:
            with urllib.request.urlopen(f"{url}/profils", timeout=2) as r:
                d = json.load(r)
        except (urllib.error.URLError, OSError, ValueError):
            time.sleep(1.0)
            continue
        if d.get("pret"):
            return d
        if not annonce:
            dire("la separation apprend encore…")
            annonce = True
        time.sleep(1.0)
    return None


def mono48(chemin, vers):
    """Le morceau en mono 48 kHz, qui est la grille du moteur.

    On passe par ffmpeg plutôt que de lire l'AIFF nous-mêmes : le bac est en AIFF stéréo, et
    le module `aifc` a disparu de Python. Décoder une fois coûte deux secondes et évite
    d'écrire un lecteur de plus.
    """
    if chemin.lower().endswith(".wav"):
        with wave.open(chemin) as f:
            if f.getframerate() == 48_000 and f.getnchannels() == 1:
                return chemin
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", chemin,
                    "-ac", "1", "-ar", "48000", vers], check=True)
    return vers


def stems(chemin_morceau, dossier, url=MOTEUR, dire=print):
    """Écrit les six pistes. Rend leur liste, ou None si la session n'a rien appris.

    Le mode de masque (`MASQUE`) traverse jusqu'a `extraire` : « partage » garantit que la
    somme des six est le morceau, « independant » supprime le creusement mutuel et perd cette
    exactitude. Les deux jeux doivent aller dans des dossiers distincts, sinon le cache de
    l'un sert a l'autre et les deux colonnes de la comparaison sont la meme.
    """
    os.makedirs(dossier, exist_ok=True)
    base = os.path.splitext(os.path.basename(chemin_morceau))[0]
    # AUTANT DE PISTES QUE LE MOTEUR PUBLIE DE SOURCES, et ce n'est plus six : la
    # separation decouvre son nombre par disque. Le nombre se lit dans les profils.
    nb = 6

    # DEJA FAIT, DEJA BON — mais seulement pour CETTE session. Le fichier temoin porte la
    # signature des profils employes : si le moteur a rappris entre-temps, les rangs ont pu
    # glisser et le cache ne vaut plus rien.
    marque = os.path.join(dossier, f"{base}.profils.json")

    p = profils(url, dire=dire)
    if p is None:
        dire("le moteur n'a rien appris : pas de pistes")
        return None
    nb = len(p["profils"])
    if nb < 1:
        dire("aucune source active : le moteur n'a pas fini de choisir")
        return None
    sorties = [os.path.join(dossier, f"{base}-{s + 1}.wav") for s in range(nb)]
    dire(f"{nb} sources publiees par le moteur")

    signature = json.dumps(p["profils"], sort_keys=True)[:2000]
    if all(os.path.exists(s) for s in sorties) and os.path.exists(marque):
        with open(marque, encoding="utf-8") as fh:
            if fh.read() == signature:
                dire("pistes deja en cache")
                # ET L'ON COMPARE QUAND MEME. Ce retour anticipe sautait la comparaison :
                # des la seconde ouverture d'un morceau, le verdict du juge n'etait plus
                # calcule, et la fenetre lisait un fichier que personne n'ecrivait. Le cache
                # porte sur les PISTES, pas sur ce qu'on en conclut.
                comparer(chemin_morceau, dossier, sorties, dire=dire)
                return sorties

    wprof = np.array(p["profils"], np.float64).T
    nfft = p["fenetre"]
    if wprof.shape[0] != nfft // 2:
        dire(f"profils incoherents : {wprof.shape[0]} bins pour une fenetre de {nfft}")
        return None

    depart = time.time()
    x, taux = extraire.lire_mono(mono48(chemin_morceau, os.path.join(dossier, f"{base}.48k.wav")))
    x = x.astype(np.float64)
    if taux != p["taux"]:
        dire(f"le morceau est a {taux} Hz, les profils ont ete appris a {p['taux']}")

    hop = max(1, nfft // extraire.RECOUVREMENT)
    sons, _, n = extraire.separer(x, wprof, nfft, hop, nb)
    if n < 8:
        dire("morceau trop court")
        return None

    for s in range(nb):
        extraire.ecrire_wav(sorties[s], sons[s], taux)
    with open(marque, "w", encoding="utf-8") as fh:
        fh.write(signature)

    dire(f"{nb} pistes en {time.time() - depart:.0f} s")
    comparer(chemin_morceau, dossier, sorties, dire=dire)
    return sorties


def comparer(chemin_morceau, dossier, sorties, dire=print):
    """Confronte les six sources au juge exterieur, si celui-ci a deja tourne.

    C'EST ICI QUE LA COMPARAISON DOIT SE FAIRE, ET NULLE PART AILLEURS. Le pre-calcul
    (`preparer.sh`) produit les quatre pistes nommees mais ne peut pas comparer : les six
    sources du moteur n'existent pas encore, puisqu'elles dependent des profils de LA SESSION
    qui les ecoutera. Le seul moment ou les deux cotes sont la est celui-ci, juste apres
    l'extraction — et l'on est deja dans un processus separe, donc le calcul ne dispute le
    verrou global a personne.

    Sans cet appel, la fenetre lisait un fichier que personne n'ecrivait : le verdict du juge
    n'apparaissait jamais, sans que rien ne le signale. C'est le genre de trou qui ne se voit
    qu'en lancant la commande.
    """
    base = os.path.splitext(os.path.basename(chemin_morceau))[0]
    refs = {p: os.path.join(dossier, "reference", f"{base}-ref-{p}.wav")
            for p in ("drums", "bass", "vocals", "other")}
    if not all(os.path.exists(c) for c in refs.values()):
        return None
    try:
        import reference
    except ImportError:
        return None
    dire("comparaison au juge exterieur…")
    try:
        verdict = reference.correspondances(sorties, refs, dire=dire)
    except Exception as e:                       # noqa: BLE001 — un juge absent n'arrete rien
        dire(f"comparaison impossible : {e}")
        return None
    with open(os.path.join(dossier, f"{base}.correspondances.json"), "w",
              encoding="utf-8") as fh:
        json.dump({"morceau": base, "correspondances": verdict}, fh, ensure_ascii=False)
    return verdict


def toutes(chemin_morceau, dossier, url=MOTEUR, dire=print):
    """Les six sources, PLUS les deux pistes de percussion si elles ont ete precalculees.

    LES DEUX DERNIERES NE SONT PAS DES SOURCES, et elles ne se calculent pas ici. La batterie
    de reference demande Demucs (cinq minutes) et les frappes demandent une passe de sonde sur
    le morceau entier : c'est du pre-calcul, il se fait a part (`outils/preparer.sh`), et son
    absence ne doit rien empecher. Un set n'est pas determine — un bonus track tombe sans
    prevenir, et l'on joue alors les six sources sans les deux autres.
    """
    six = stems(chemin_morceau, dossier, url, dire)
    if six is None:
        return None
    base = os.path.splitext(os.path.basename(chemin_morceau))[0]
    en_plus = [os.path.join(dossier, "reference", f"{base}-ref-drums.wav"),
               os.path.join(dossier, f"{base}-frappe.wav")]
    return six + [c for c in en_plus if os.path.exists(c)]


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    ok = stems(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else MOTEUR)
    sys.exit(0 if ok else 1)
