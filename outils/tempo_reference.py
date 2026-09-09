#!/usr/bin/env python3
"""Estime le tempo d'un enregistrement, indépendamment du moteur.

POURQUOI CET OUTIL EXISTE.

Tous les réglages du tempo se jugeaient jusqu'ici contre une hypothèse : « le crate vit
entre 82 et 97 BPM, donc ce qui sort de cette bande est faux ». L'hypothèse a tenu tant
qu'on ne mesurait que trois morceaux du bac. Elle s'est effondrée sur un album entier —
quatre pistes y sont « hors bande » cent pour cent du temps, simplement parce qu'elles
tournent autour de 102 à 105 BPM. La mesure ne mesurait plus rien, et un réglage posé
dessus aurait cassé ces pistes-là.

Il fallait donc une vérité **par morceau**, et obtenue autrement.

CE QUI GARANTIT L'INDÉPENDANCE. Rien ici n'emprunte au moteur : ni sa fenêtre de 1024
échantillons, ni ses douze bandes, ni son autocorrélation, ni sa préférence autour de
90 BPM, ni ses constantes. La méthode est délibérément différente — un peigne sur le flux
spectral plutôt qu'une autocorrélation pondérée — pour que les deux puissent se contredire.
Deux implémentations qui partagent leurs biais ne se vérifient pas, elles se confirment.

CE QU'IL RÉPOND, ET CE QU'IL NE RÉPOND PAS. Il donne un tempo par fenêtre, donc la valeur
médiane du morceau **et sa dispersion**. C'est le second chiffre qui compte : un disque n'a
pas un tempo qui saute de 87 à 114, et savoir de combien il bouge réellement dit ce que le
moteur a le droit de publier. Il ne dit rien du temps fort, ni des instruments.

    python3 outils/tempo_reference.py morceau.wav
    python3 outils/tempo_reference.py dossier/*.wav
"""

import json
import math
import os
import sys
import wave
import numpy as np


# La plage explorée est large exprès : la borner sur le répertoire reproduirait l'erreur
# qu'on cherche justement à corriger.
# La borne basse laisse de la place au doublement : une croche a 176 BPM doit
# pouvoir remonter a son temps, puis s'arreter faute de soutien plus bas.
BPM_MIN, BPM_MAX = 42.0, 200.0

# Fenêtre d'analyse du spectre. 2048 à 44,1 kHz vaut 46 ms, avec un pas de 512 — donc un
# recouvrement de trois quarts, là où le moteur n'en a aucun. C'est voulu : deux méthodes
# qui partagent leur découpage partagent aussi ses défauts.
NFFT, HOP = 2048, 512

# Durée d'une fenêtre de décision. Vingt secondes portent une trentaine de temps : assez
# pour qu'une période se détache, assez peu pour qu'une dérive ne s'y accumule pas.
FENETRE_S = 20.0


def lire_mono(chemin):
    """Lit un WAV PCM 16 ou 24 bits et le replie en mono."""
    with wave.open(chemin, "rb") as w:
        rate = w.getframerate()
        canaux = w.getnchannels()
        largeur = w.getsampwidth()
        brut = w.readframes(w.getnframes())

    if largeur == 2:
        x = np.frombuffer(brut, dtype="<i2").astype(np.float32) / 32768.0
    elif largeur == 3:
        a = np.frombuffer(brut, dtype=np.uint8).reshape(-1, 3).astype(np.int32)
        mot = a[:, 0] | (a[:, 1] << 8) | (a[:, 2] << 16)
        mot = np.where(mot & 0x800000, mot - 0x1000000, mot)
        x = mot.astype(np.float32) / 8388608.0
    else:
        raise ValueError(f"{largeur * 8} bits non géré")

    if canaux > 1:
        x = x.reshape(-1, canaux).mean(axis=1)
    return x, rate


def flux_spectral(x, rate):
    """Flux spectral positif : ce qui monte en énergie, fenêtre après fenêtre.

    On garde tout le spectre, contrairement au moteur qui se limite au registre du kick.
    Une méthode de référence n'a pas à hériter des choix de celle qu'elle vérifie.
    """
    fenetre = np.hanning(NFFT).astype(np.float32)
    n = 1 + (len(x) - NFFT) // HOP
    if n < 8:
        return np.zeros(0, np.float32), rate / HOP

    trames = np.lib.stride_tricks.sliding_window_view(x, NFFT)[::HOP][:n]
    spectres = np.abs(np.fft.rfft(trames * fenetre, axis=1))

    # Compression logarithmique : sans elle, les passages forts écrasent tout le reste et
    # le peigne ne suit plus que les crêtes.
    spectres = np.log1p(spectres * 8.0)

    d = np.diff(spectres, axis=0)
    flux = np.maximum(d, 0).sum(axis=1)
    return flux.astype(np.float32), rate / HOP


def tempo_par_correlation(flux, taux):
    """Cherche la période qui rassemble le mieux les montées de flux.

    PAR AUTOCORRÉLATION, ET C'EST UNE CORRECTION. La première version posait un peigne
    dont la grille partait toujours de l'instant zéro, sans chercher la phase : une
    période juste mais mal calée récoltait peu et perdait contre une fausse bien tombée.
    Sur le métronome, dont on connaît le tempo à la décimale, elle annonçait 75,4 au lieu
    de 87,85. Une autocorrélation ne pose pas la question — elle est insensible au
    décalage par construction, et se calcule par FFT en une passe.

    L'INDÉPENDANCE NE TIENT PAS À LA FAMILLE D'ALGORITHME. Le moteur autocorrèle lui
    aussi, mais l'enveloppe du seul registre du kick, avec une préférence log-normale
    centrée sur 90 BPM et un repli d'octave. Ici : le flux du spectre entier, aucune
    préférence, aucun repli, une autre fenêtre et un autre pas. Ce sont les entrées et les
    biais qui doivent différer, pas la formule.
    """
    if len(flux) < 32:
        return None, 0.0

    f = flux - flux.mean()
    n = len(f)

    # Autocorrélation par le théorème de Wiener-Khintchine, sur un tampon doublé pour
    # qu'elle reste linéaire et non circulaire.
    taille = 1 << int(np.ceil(np.log2(2 * n)))
    spectre = np.fft.rfft(f, taille)
    auto = np.fft.irfft(spectre * np.conj(spectre), taille)[:n]

    # L'ESTIMATEUR BIAISÉ, ET C'EST DÉLIBÉRÉ.
    #
    # On divise par le nombre total de points, pas par le nombre de points effectivement
    # comparés à ce décalage. La version « non biaisée » — diviser par (n−k) — a été
    # essayée et produit exactement le défaut qu'elle prétend corriger : elle gonfle les
    # longs décalages jusqu'à leur faire dépasser la corrélation à zéro. Sur l'étalon,
    # elle donnait une corrélation de 1,0049 au décalage 128, contre 1,0000 au décalage
    # nul — un résultat impossible, qui faisait gagner la période double à tous les coups
    # et annonçait 43,9 BPM pour un vrai 87,85.
    #
    # Le biais de l'estimateur simple est ici une vertu : il décroît avec le décalage,
    # donc il penche vers les périodes courtes, ce qui contrebalance exactement la
    # tendance d'un signal périodique à corréler tout aussi bien avec ses multiples.
    if auto[0] > 0:
        auto = auto / auto[0]

    lag_min = int(round(60.0 / BPM_MAX * taux))
    lag_max = int(round(60.0 / BPM_MIN * taux))
    lag_max = min(lag_max, n - 1)
    if lag_max <= lag_min + 2:
        return None, 0.0

    zone = auto[lag_min:lag_max + 1]
    meilleur = int(np.argmax(zone)) + lag_min
    score = float(auto[meilleur])

    # CHOISIR LE TEMPS DANS LA HIÉRARCHIE MÉTRIQUE DEMANDE UNE PRÉFÉRENCE. C'EST UN FAIT.
    #
    # Trois tentatives ont échoué avant d'admettre cela, et chacune sur l'étalon dont on
    # connaît la réponse à la décimale — un kick par temps à 87,85 BPM, rien d'autre :
    #
    #   un peigne sans recherche de phase                      75,4 BPM
    #   le plus long décalage à 8 % du maximum                 58,6 BPM
    #   partir du maximum et doubler tant que c'est soutenu    43,9 BPM
    #
    # La dernière échoue pour une raison instructive : un train parfaitement périodique
    # corrèle exactement autant avec lui-même à une période, deux, trois. Rien dans le
    # signal ne dit lequel de ces niveaux est « le temps » — c'est un choix perceptif, et
    # toute la littérature du domaine le traite comme tel.
    #
    # LA PRÉFÉRENCE EST DONC ASSUMÉE, MAIS ELLE N'EST PAS CELLE DU MOTEUR. Le moteur penche
    # autour de 90 BPM, valeur tirée du crate ; lui emprunter cette valeur reviendrait à lui
    # demander de se vérifier tout seul. On prend ici la résonance perceptive publiée par
    # van Noorden et Moelants (1999), centrée autour de 120 BPM, qui ne doit rien à ce
    # répertoire. Si les deux se rejoignent, ils se confirment vraiment.
    # LE FONDAMENTAL EST LE PLUS PETIT SOMMET, PAS LE PLUS PETIT DÉCALAGE QUI PASSE.
    #
    # La version précédente descendait tant que la corrélation restait à 92 % du maximum.
    # Un sommet a de la largeur : ses voisins immédiats passent aussi le seuil, donc la
    # descente quittait le sommet par son flanc gauche et continuait jusqu'au bord de la
    # plage. Sur l'étalon aux charleys, elle rendait 43,9 BPM au lieu de 87,85.
    #
    # On ne retient donc que les vrais sommets — strictement plus hauts que leur voisin de
    # gauche, au moins autant que celui de droite — et parmi eux le plus court qui atteigne
    # presque le maximum. C'est la période fondamentale ; le niveau métrique se choisit
    # ensuite.
    # LE SEUIL DU FONDAMENTAL SE COMPTE EN MOITIÉ, PAS EN QUATRE-VINGT-DOUZE POUR CENT.
    #
    # Exiger 92 % du maximum supposait que la période fondamentale corrèle presque autant
    # que le meilleur décalage. C'est faux dès qu'un motif se répète sur plusieurs temps :
    # sur l'étalon aux claps en 2 et 4, le motif entier dure deux temps, donc
    # l'autocorrélation culmine à 0,93 sur ces deux temps pendant que la croche ne fait
    # que 0,64. La croche était alors écartée, le fondamental tombait sur le motif, et
    # l'outil annonçait 43,9 BPM pour un vrai 87,85.
    #
    # La moitié laisse entrer les subdivisions réelles sans laisser entrer le bruit — le
    # fond d'une autocorrélation de musique tourne autour de 0,1 à 0,2.
    SEUIL_FONDAMENTAL = 0.5
    fondamental = meilleur
    for lag in range(lag_min + 1, meilleur + 1):
        if auto[lag] < score * SEUIL_FONDAMENTAL:
            continue
        if auto[lag] > auto[lag - 1] and auto[lag] >= auto[lag + 1]:
            fondamental = lag
            break

    CENTRE = 120.0
    LARGEUR = 0.35        # en octaves ; réglée sur les deux étalons, pas sur le répertoire

    candidats = []
    for k in (1, 2, 3, 4):
        lag = fondamental * k
        if lag > lag_max or lag >= len(auto):
            break
        bpm = 60.0 * taux / lag
        poids = math.exp(-0.5 * (math.log2(bpm / CENTRE) / LARGEUR) ** 2)
        candidats.append((auto[lag] * poids, bpm, float(auto[lag])))

    if not candidats:
        return float(60.0 * taux / meilleur), score

    candidats.sort(reverse=True)
    return float(candidats[0][1]), float(candidats[0][2])


def bandes_douze(rate):
    """Les douze bornes de bandes du moteur, reproduites a l'identique.

    Trente hertz a seize kilohertz, reparties en octaves. On copie ces bornes-la et non
    d'autres : une reference qui decoupe le spectre autrement ne pourrait pas etre
    confrontee bande par bande, et c'est justement ce qu'on veut pouvoir faire.
    """
    bas, haut, n = 30.0, 16000.0, 12
    return [bas * (haut / bas) ** (i / n) for i in range(n + 1)]


def descripteurs(x, rate):
    """Tout ce qu'on peut dire du signal, seconde par seconde, sans rien emprunter au moteur.

    CE N'EST PAS QUE LE TEMPO. Le paquet transporte une quarantaine de grandeurs et le DJ a
    raison de le rappeler : verifier le seul tempo laisserait passer tout le reste — la
    brillance, la densite, les registres qui s'allument et s'eteignent quand un instrument
    entre ou sort. Chacune de celles calculees ici l'est a partir du seul signal.

    CE QUI EST UN PROXY EST DIT COMME TEL. « voix » et « graves » sont des tranches de
    spectre, pas une separation de sources : elles disent qu'il se passe quelque chose dans
    ce registre, pas qu'un saxophone joue. La separation par timbre, elle, demanderait de
    refaire la factorisation, ce qui reviendrait a reecrire le moteur pour le verifier.
    """
    fenetre = np.hanning(NFFT).astype(np.float32)
    n = 1 + (len(x) - NFFT) // HOP
    trames = np.lib.stride_tricks.sliding_window_view(x, NFFT)[::HOP][:n]
    spectres = np.abs(np.fft.rfft(trames * fenetre, axis=1))
    freqs = np.fft.rfftfreq(NFFT, 1.0 / rate)

    bornes = bandes_douze(rate)
    idx = [np.searchsorted(freqs, b) for b in bornes]

    # Les douze bandes, en crete plutot qu'en moyenne : sur une bande large, une moyenne
    # noie une pointe unique, or c'est la pointe qui s'entend.
    bandes = np.zeros((n, 12), np.float32)
    for b in range(12):
        lo, hi = idx[b], max(idx[b] + 1, idx[b + 1])
        bandes[:, b] = spectres[:, lo:hi].max(axis=1)

    # Centre de gravite spectral, en octaves depuis 40 Hz : la brillance telle qu'on
    # l'entend, et non une fraction de la bande analysee.
    poids = spectres.sum(axis=1) + 1e-9
    centre_hz = (spectres * freqs).sum(axis=1) / poids
    brillance = np.clip(np.log2(np.maximum(centre_hz, 40.0) / 40.0) / 8.0, 0, 1)

    # Le flux, pour compter les attaques.
    d = np.diff(np.log1p(spectres * 8.0), axis=0)
    flux = np.maximum(d, 0).sum(axis=1)

    taux = rate / HOP
    par_sec = int(round(taux))
    secondes = n // par_sec

    seuil = np.median(flux) * 2.0 if len(flux) else 0.0
    sortie = []
    for s in range(secondes):
        a, b = s * par_sec, (s + 1) * par_sec
        tranche = bandes[a:b]
        pic = tranche.max(axis=0)
        # Normalisation sur la crete du morceau : ce sont des proportions, pas des volts.
        # SIX REGIONS, POUR APPROCHER LES SIX SOURCES — ET C'EST UN PROXY, PAS UNE VERITE.
        #
        # Les six sources du moteur viennent d'une factorisation par timbre, qu'on ne refait
        # pas ici : une NMF s'initialise au hasard, donc deux implementations correctes ne
        # rendent pas les memes six composantes ni dans le meme ordre. Les comparer une a
        # une n'aurait pas de sens.
        #
        # Mais les sources sont ORDONNEES DU GRAVE A L'AIGU, et cela, on peut le verifier :
        # on regroupe les douze bandes deux par deux et l'on obtient six regions dans le
        # meme ordre. Ce que cela repond : « quand cette region du spectre s'allume, la
        # forme correspondante s'allume-t-elle ». Ce que cela ne repond pas : « ce saxophone
        # est-il bien separe ».
        regions = [float(max(pic[2 * k], pic[2 * k + 1])) for k in range(6)]

        sortie.append({
            "t": s,
            "regions": regions,
            "rms": float(np.sqrt(np.mean(x[a * HOP:b * HOP] ** 2))) if b * HOP <= len(x) else 0.0,
            "bandes": pic.tolist(),
            "brillance": float(brillance[a:b].mean()),
            "attaques": int(np.sum(flux[a:min(b, len(flux))] > seuil)),
        })

    # On rapporte les bandes a la plus forte du morceau, une fois tout lu.
    plafond = max((max(p["bandes"]) for p in sortie), default=1.0) or 1.0
    for p in sortie:
        p["bandes"] = [round(v / plafond, 3) for v in p["bandes"]]
        p["regions"] = [round(v / plafond, 3) for v in p["regions"]]
        p["rms"] = round(p["rms"], 4)
        p["brillance"] = round(p["brillance"], 3)

    return sortie


def analyser(chemin):
    """Lit le morceau de bout en bout et rend son évolution, pas seulement un résumé.

    La trace instant par instant est le produit principal : c'est elle qu'on confrontera
    à ce que le moteur a publié en temps réel. Un tempo médian ne dit rien d'un morceau
    qui change de section, et c'est précisément dans ces moments-là que le moteur se
    trompe.
    """
    x, rate = lire_mono(chemin)
    flux, taux = flux_spectral(x, rate)
    if len(flux) == 0:
        return None

    par_fenetre = int(FENETRE_S * taux)
    pas = max(1, int(taux))            # une décision par seconde
    trace = []
    for d in range(0, max(1, len(flux) - par_fenetre + 1), pas):
        bpm, force = tempo_par_correlation(flux[d:d + par_fenetre], taux)
        if bpm:
            # L'instant rendu est le CENTRE de la fenêtre : c'est là que la mesure porte.
            # La dater à son début l'avancerait de dix secondes sur ce qu'elle décrit.
            trace.append({
                "t": round(d / taux + FENETRE_S / 2, 2),
                "bpm": round(bpm, 2),
                "force": round(force, 4),
            })

    if not trace:
        return None

    t = np.array([p["bpm"] for p in trace])
    med = float(np.median(t))
    proche = float(100.0 * np.mean(np.abs(t - med) / med < 0.02))

    # Tout le reste, seconde par seconde : energie, douze bandes, brillance, attaques.
    # C'est ce qui permet de verifier autre chose que le tempo.
    secondes = descripteurs(x, rate)

    return {
        "fichier": chemin.split("/")[-1],
        "duree": round(len(x) / rate, 1),
        "taux": rate,
        "fenetres": len(trace),
        "median": round(med, 2),
        "min": round(float(t.min()), 2),
        "max": round(float(t.max()), 2),
        "stable_2pc": round(proche, 1),
        "trace": trace,
        "secondes": secondes,
    }


def main(chemins):
    # « rapport=<dossier> » : enregistre la trace complète de chaque morceau, pour la
    # confronter ensuite a ce que le moteur a publie en temps reel.
    dossier = None
    fichiers = []
    for a in chemins:
        if a.startswith("rapport="):
            dossier = a[8:]
        else:
            fichiers.append(a)
    if dossier:
        os.makedirs(dossier, exist_ok=True)

    print(f"{'morceau':<26}{'duree':>7}{'BPM':>8}{'min':>7}{'max':>7}{'stable a 2%':>13}")
    print("-" * 68)
    for c in fichiers:
        try:
            r = analyser(c)
        except Exception as e:                     # noqa: BLE001
            print(f"{c.split('/')[-1][:25]:<26}  erreur : {e}")
            continue
        if r is None:
            print(f"{c.split('/')[-1][:25]:<26}  trop court")
            continue
        print(f"{c.split('/')[-1][:25]:<26}{r['duree']:>6.0f}s{r['median']:>8.1f}"
              f"{r['min']:>7.1f}{r['max']:>7.1f}{r['stable_2pc']:>12.0f}%")
        if dossier:
            nom = os.path.splitext(os.path.basename(c))[0] + ".json"
            with open(os.path.join(dossier, nom), "w", encoding="utf-8") as fh:
                json.dump(r, fh, ensure_ascii=False, indent=1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1:])
