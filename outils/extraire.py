#!/usr/bin/env python3
"""Entendre ce que chaque source entend : un WAV par source.

POURQUOI CET OUTIL EXISTE.

Toutes les mesures de ce projet disent si une source est RÉGULIÈRE. Aucune ne dit si elle
contient ce qu'elle prétend contenir — et c'est pourtant la seule question qui compte quand
on affiche six formes en prétendant qu'elles suivent six instruments.

L'idée est du DJ : « on pourrait faire en sorte que tu extraies ce qu'entend chaque source ;
par exemple `sourceXpiano.wav`, je l'écoute et je regarde si c'est vraiment le piano, si
c'est en rythme avec le piano réel ». L'oreille tranche en dix secondes ce qu'aucun chiffre
n'a su dire.

COMMENT.

Le moteur apprend six **profils spectraux** — la couleur de chaque source — et les exporte
(`tools/Emotion.Probe … profils=…json`). Ici on refait la transformée du morceau, on retrouve
combien chaque profil est actif à chaque instant, et l'on répartit le spectre entre les six
au prorata. Chaque part est retransformée en son, avec la phase d'origine.

    dotnet run -c Release --project tools/Emotion.Probe -- morceau.wav 0 90 \\
        fiche=90.92 profils=profils.json
    python3 outils/extraire.py profils.json morceau.wav sources/

LE PIÈGE QU'IL FALLAIT ÉVITER, ET IL ÉTAIT GRAVE.

L'analyse du moteur avance par blocs de 1024 échantillons **sans recouvrement**.
Reconstruire sur cette grille produirait une coupure franche toutes les 21 ms — un
bourdonnement à 47 Hz sur les six fichiers. On entendrait un artefact de reconstruction et
on l'attribuerait à la séparation : une fausse preuve à charge contre une pièce qui n'y peut
rien, ce qui est pire qu'aucune mesure.

L'extraction fait donc **sa propre transformée**, très recouverte et fenêtrée de Hann des
deux côtés. Un profil est fréquentiel : il se transporte tel quel d'une grille temporelle à
une autre, gratuitement.

**Et le recouvrement de moitié n'a pas suffi.** Mesuré, il laissait un bourdonnement à
soixante-douze fois le plancher — non plus la coupure des blocs, mais le masque lui-même qui
module d'une trame à l'autre. Il a fallu monter à quatre-vingt-huit pour cent pour passer
sous le bruit du morceau. L'outil VÉRIFIE ces deux choses à chaque passage plutôt que de les
affirmer : que la chaîne reconstruit exactement, et que la somme des six est le morceau.

CE QUE LA SOMME DES SIX DOIT VALOIR.

Les six parts se partagent le spectre au prorata : leur somme est le spectre entier, donc
leur somme resynthétisée est le morceau d'origine. Si ce n'est pas le cas, quelque chose est
faux dans la chaîne et il vaut mieux le savoir avant d'écouter. C'est le premier contrôle,
et il échouerait bruyamment.
"""

import itertools
import json
import math
import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tempo_reference import lire_mono

# Itérations pour retrouver les activations, profils fixés. Au-delà, elles ne bougent plus.
ITERATIONS = 40

def reglage(nom, defaut, conv=float):
    """Un reglage lu dans l'environnement, qui ne fait jamais tomber le programme.

    UNE VARIABLE VIDE N'EST PAS UNE VARIABLE ABSENTE, et la difference a coute deux pannes le
    meme jour. Un script qui ecrit `EMOTION_AVANCE_MS=` quand il n'a rien a dire produit une
    chaine vide, et `float("")` leve. Le reglage est FACULTATIF : ne pas le donner ne doit
    jamais empecher le programme de demarrer.
    """
    v = os.environ.get(nom, "").strip()
    try:
        return conv(v) if v else defaut
    except ValueError:
        return defaut


# RECOUVREMENT DES TRAMES, ET IL A ÉTÉ MESURÉ.
#
# Un masque qui change d'une trame à l'autre module l'amplitude à la cadence des trames.
# Mesuré sur cinq morceaux du bac, chaque source contre SON témoin à masque fixe — le morceau
# filtré par le même profil mais par un gain qui ne bouge jamais, donc sans artefact possible :
#
#     recouvrement   cadence     t02    t04     t05    t09    t11   pire
#             50 %    93,8 Hz    7,7    2,0   103,4    5,2   11,2   x103
#             75 %   187,5 Hz    2,4    1,7     7,2    1,7    1,7     x7
#             88 %   375,0 Hz    4,5    2,4     2,2    2,2    1,4     x5
#
# **Le gain décisif est entre 50 et 75 %**, d'un facteur cinquante sur le pire morceau. Au-delà
# ce n'est plus tranché : 75 % gagne en médiane (1,7 contre 2,2) et 88 % gagne en pire cas
# (x4,5 contre x7,2). On garde 88 % pour la raison qui vaut partout ailleurs dans ce projet —
# **c'est le pire cas qui se voit**, trois passages moyens non.
#
# On aurait pu s'arrêter à 50 % en écoutant vite fait, et l'on aurait entendu une source
# bourdonner cent fois plus que son témoin en croyant que la séparation la hachait.
RECOUVREMENT = reglage("RECOUVREMENT", 8, int)

# Trames traitées d'un coup. À 88 % de recouvrement, un morceau de quatre-vingt-dix secondes
# en compte soixante-sept mille : garder tout le spectrogramme en mémoire demanderait un
# demi-gigaoctet par matrice. On avance donc par blocs — et c'est EXACT, non approché : les
# activations se calculent colonne par colonne à profils fixés, donc découper le temps ne
# change pas une virgule au résultat.
BLOC = 1024

# LISSAGE DU MASQUE, en trames. Mesuré à 88 % de recouvrement, pire source de chaque morceau :
#
#     lissage      t02    t04    t05    t09    t11   moyenne
#           0      5,6    2,6    4,2    2,1    1,3      3,2
#           8      4,5    2,4    2,2    2,2    1,4      2,5
#          16      3,7    2,5    1,9    2,2    1,3      2,3
#
# **Il aide, il ne sauve pas** — et c'est huit qui est retenu parce que c'est la valeur que le
# raisonnement désigne, pas celle qui donne le meilleur chiffre. Seize ne gagne que deux
# dixièmes et coûte de la finesse temporelle ; choisir seize pour ces deux dixièmes serait
# tirer des flèches jusqu'à ce que l'une aille au milieu.
LISSAGE = reglage("LISSAGE", 8, int)

# COMMENT LES SIX SE PARTAGENT LE SPECTRE, ET C'EST UN CHOIX, PAS UNE EVIDENCE.
#
# « partage » — le masque historique. Les six masques somment a 1 sur chaque raie : la somme
# des six EST le morceau, exactement. Mais une source qui monte PREND la part des autres, et
# cela s'entend : mesure au moment des frappes, des sources tombent a 0,43 et 0,51 de leur
# niveau d'avant. Le DJ l'a decrit ainsi : « ils sont tous affectes par le kick, le son de la
# source est rendu muet au moment ou le kick arrive ».
#
# « independant » — l'idee est du DJ, et elle vient des ecouteurs a reduction de bruit :
# « source 1 c'est le son plus l'inverse de tout le reste ». Chaque source est mise en
# balance avec TOUT LE RESTE pris ensemble, sans contrainte de somme : elle garde ce qui lui
# ressemble sans que personne ne le lui retire.
#
# On perd alors l'exactitude — la somme des six n'est plus le morceau — et c'est le prix.
MASQUE = os.environ.get("MASQUE", "partage")

EPS = 1e-9


def hann_periodique(n):
    """La fenêtre de Hann PÉRIODIQUE, et non symétrique.

    `np.hanning` rend la symétrique, dont les deux extrémités valent zéro : superposée à
    elle-même, elle ne somme pas à une constante et la reconstruction porte une ondulation
    lente. La différence tient à un échantillon, et elle s'entend.
    """
    return 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(n) / n)


def activations(v, wprof):
    """Combien chaque profil est actif à chaque instant, profils fixés.

    Mise à jour multiplicative : elle ne peut pas rendre de valeur négative, ce qui est la
    raison d'être de cette factorisation — une source ne joue jamais « moins que rien ».
    """
    h = np.full((wprof.shape[1], v.shape[1]), 0.5, np.float64)
    wt = wprof.T
    wtw = wt @ wprof
    wtv = wt @ v
    for _ in range(ITERATIONS):
        h *= wtv / (wtw @ h + EPS)
    return h


def lisser(h, largeur):
    """Empêche le masque de changer plus vite que la fenêtre qui l'a estimé.

    LE RECOUVREMENT NE SUFFIT PAS, ET C'EST UNE CORRECTION DE PRINCIPE, PAS UN PANSEMENT.
    Chaque colonne d'activations est calculée indépendamment, et la factorisation rend des
    valeurs très piquées — le projet l'a déjà mesuré ailleurs : médiane nulle, une source à
    zéro plus d'une image sur deux. Un masque qui passe de zéro à un d'une trame à l'autre
    module l'amplitude à la cadence des trames, quel que soit le recouvrement.

    Or ce masque est estimé sur une fenêtre de mille vingt-quatre échantillons. Le faire
    varier tous les cent vingt-huit prétend une résolution temporelle qu'on n'a pas. On le
    lisse donc sur une longueur de fenêtre : c'est la résolution réelle de la mesure, et
    c'est aussi la cadence à laquelle le moteur, lui, calcule ses activations.
    """
    if largeur < 2:
        return h
    f = hann_periodique(2 * largeur + 1)[1:]
    f /= f.sum()
    marge = len(f) // 2
    etendu = np.pad(h, ((0, 0), (marge, marge)), mode="edge")
    return np.stack([np.convolve(ligne, f, mode="valid") for ligne in etendu])


def separer(x, wprof, nfft, hop, sources):
    """Rend les six sons, et le morceau reconstruit sans masque.

    Le second sert de contrôle : si la chaîne transformée/reconstruction n'est pas exacte,
    tout ce qui suit est faux, et l'on entendrait un défaut d'outil en croyant entendre un
    défaut de séparation.
    """
    bins = wprof.shape[0]
    w = hann_periodique(nfft)
    w2 = w * w
    n = 1 + max(0, (len(x) - nfft) // hop)

    sorties = [np.zeros(len(x) + nfft) for _ in range(sources)]
    brut = np.zeros(len(x) + nfft)
    poids = np.zeros(len(x) + nfft)

    for i0 in range(0, n, BLOC):
        i1 = min(n, i0 + BLOC)
        # ON DÉBORDE DU BLOC DES DEUX CÔTÉS. Le lissage du masque a besoin des trames
        # voisines : sans ce débord, il verrait un bord à chaque limite de bloc et
        # rétablirait exactement la coupure périodique que tout ce fichier cherche à éviter.
        j0 = max(0, i0 - LISSAGE)
        j1 = min(n, i1 + LISSAGE)
        depart = np.arange(j0, j1) * hop
        trames = np.stack([x[d:d + nfft] for d in depart]) * w
        spectres = np.fft.rfft(trames, axis=1)

        mag = np.abs(spectres[:, :bins]).T
        h = lisser(activations(mag, wprof), LISSAGE)

        # On ne garde que le bloc ; le débord n'était là que pour nourrir le lissage.
        a, b = i0 - j0, i1 - j0
        h = h[:, a:b]
        spectres = spectres[a:b]
        depart = depart[a:b]
        modele = wprof @ h + EPS

        # Le morceau sans masque, pour le contrôle.
        rec = np.fft.irfft(spectres, nfft, axis=1) * w
        for k, d in enumerate(depart):
            brut[d:d + nfft] += rec[k]
            poids[d:d + nfft] += w2

        for s in range(sources):
            masque = np.ones((i1 - i0, spectres.shape[1]))
            part = np.outer(wprof[:, s], h[s])
            if MASQUE == "independant":
                # CETTE SOURCE CONTRE TOUT LE RESTE, comme un casque met le bruit en balance
                # avec ce qu'il veut garder. Aucune normalisation croisee : ce que la source
                # prend ne se retire a personne.
                reste = modele - part
                masque[:, :bins] = (part ** 2 / (part ** 2 + reste ** 2 + EPS)).T
            else:
                masque[:, :bins] = (part / modele).T
            part = np.fft.irfft(spectres * masque, nfft, axis=1) * w
            cible = sorties[s]
            for k, d in enumerate(depart):
                cible[d:d + nfft] += part[k]

    bon = poids > 1e-8
    for s in range(sources):
        sorties[s][bon] /= poids[bon]
        sorties[s] = sorties[s][:len(x)]
    brut[bon] /= poids[bon]
    return sorties, brut[:len(x)], n


def ecrire_wav(chemin, x, taux):
    """Un WAV mono 16 bits. On normalise sur le morceau entier, jamais par source.

    Normaliser chaque source séparément la rendrait aussi forte que les autres, et l'on
    perdrait précisément ce qu'on veut entendre : si une source est faible, c'est une
    information.
    """
    d = np.clip(x, -1.0, 1.0)
    with wave.open(chemin, "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(taux)
        f.writeframes((d * 32767).astype("<i2").tobytes())


def extraire(chemin_profils, chemin_wav, dossier, chemin_bis=None):
    with open(chemin_profils, encoding="utf-8") as fh:
        p = json.load(fh)
    if not p.get("pret"):
        print("ATTENTION : le moteur n'avait rien appris, les profils ne decrivent que du bruit")

    wprof = np.array(p["profils"], np.float64).T          # bins x sources
    bins, sources = wprof.shape
    nfft = p["fenetre"]
    if bins != nfft // 2:
        print(f"profils a {bins} bins pour une fenetre de {nfft} : incoherent")
        return 1

    x, taux = lire_mono(chemin_wav)
    x = x.astype(np.float64)
    if taux != p["taux"]:
        print(f"le morceau est a {taux} Hz, les profils ont ete appris a {p['taux']}")

    hop = max(1, nfft // RECOUVREMENT)
    print(f"{os.path.basename(chemin_wav)} : {len(x) / taux:.0f} s, "
          f"recouvrement {100 * (1 - 1 / RECOUVREMENT):.0f} %, {sources} sources")

    sons, brut, n = separer(x, wprof, nfft, hop, sources)
    if n < 8:
        print("  morceau trop court")
        return 1

    # Les bords ne sont couverts que par une partie des trames : les juger reviendrait a
    # mesurer un effet de bord et non la chaine.
    marge = nfft

    def snr(y):
        err = y[marge:-marge] - x[marge:-marge]
        return 10 * math.log10(np.sum(x[marge:-marge] ** 2) / max(EPS, np.sum(err ** 2)))

    # ---- CONTROLE 1 : la chaine transformee / reconstruction est-elle exacte ? ----
    #
    # Sans lui, une erreur de fenetre ou de normalisation s'entendrait comme un defaut de la
    # separation. On le fait sur le spectre entier, avant tout masque.
    a = snr(brut)
    print(f"  reconstruction sans masque   {a:6.1f} dB", end="")
    if a <= 60:
        print("   ECHEC : la chaine est fausse, rien n'est ecrit")
        return 1
    print("   ok")

    # ---- CONTROLE 2 : les six parts se partagent tout le spectre ----
    somme = np.zeros(len(x))
    for y in sons:
        somme += y
    b = snr(somme)
    print(f"  somme des six = le morceau   {b:6.1f} dB", end="")
    print("   ok" if b > 25 else "   SUSPECT : les masques ne se partagent pas tout")

    # ---- CONTROLE 3 : le bourdonnement a la cadence des trames ----
    #
    # C'est le controle qui a fait passer le recouvrement de moitie a huit sur neuf. On le
    # garde en place : un jour ou l'autre quelqu'un changera un parametre, et il vaut mieux
    # que l'outil le dise que l'oreille.
    cadence = taux / hop

    # UN TEMOIN A LA FOIS, ET C'EST UNE CORRECTION DE MEMOIRE, PAS DE STYLE.
    #
    # Les six etaient calcules d'un coup et gardes jusqu'a la fin. Sur un morceau de deux
    # cent quatre-vingts secondes cela fait six copies de treize millions d'echantillons en
    # double precision, plus les six sources deja presentes : le systeme a tue le processus
    # (code 137) juste apres le deuxieme controle. Les morceaux courts passaient, les longs
    # non — donc le defaut ne se voyait que sur les vrais disques.
    #
    # Chaque temoin sert a trois choses (le bourdonnement, l'ecart au filtre, le WAV) : on
    # les fait toutes dans la foulee, puis on le laisse partir.
    ecarts, proches = [], []
    ou = os.path.join(dossier, "temoin")
    os.makedirs(ou, exist_ok=True)
    base_temoin = os.path.splitext(os.path.basename(chemin_wav))[0]
    for s in range(sources):
        t = temoin_fixe(x, wprof, s, taux, nfft)
        ecarts.append(bourdonnement(sons[s], taux, cadence)
                      / max(EPS, bourdonnement(t, taux, cadence)))
        proches.append(correlation(sons[s], t))
        ecrire_wav(os.path.join(ou, f"{base_temoin}-temoin{s + 1}.wav"), t, taux)
        del t
    pire = max(ecarts)
    # TROIS VERDICTS, ET LE SEUIL DE DEUX N'A PAS ETE DEPLACE. Il avait ete fixe avant la
    # mesure ; il n'est pas atteint partout, et c'est ecrit tel quel. Ce que la mesure sait
    # vraiment distinguer est un facteur vingt, pas un facteur trois — au-dela de dix
    # l'artefact est franc, en dessous de deux il n'y en a pas, et entre les deux l'outil dit
    # qu'il ne sait pas plutot que de rendre un verdict qu'il ne peut pas soutenir.
    verdict = ("   ok" if pire < 2.0 else
               "   ARTEFACT : ne pas se fier a l'oreille" if pire > 10.0 else
               "   INDETERMINE : juger le geste, pas le grain")
    print(f"  bourdonnement a {cadence:5.0f} Hz   "
          f"pire source x{pire:4.1f} de son temoin "
          f"({'  '.join(f'{e:.1f}' for e in ecarts)})", end="")
    print(verdict)

    # ---- CONTROLE 4 : les six sont-elles seulement differentes ? ----
    #
    # Un masque doux partage le spectre, il ne le decoupe pas : rien n'empeche deux profils
    # de se ressembler au point que leurs deux fichiers portent le meme son. Ce serait une
    # information capitale AVANT d'ecouter — sinon on chercherait pendant dix minutes ce qui
    # distingue deux sources qui n'ont rien a distinguer.
    pires = []
    for i in range(sources):
        for j in range(i + 1, sources):
            c = correlation(sons[i], sons[j])
            pires.append((c, i + 1, j + 1))
    c, i, j = max(pires)
    print(f"  les six sont distinctes      sources {i} et {j} se ressemblent a {c:4.2f}", end="")
    print("   ok" if c < 0.5 else "   ATTENTION : deux fichiers portent presque le meme son")

    # ---- CONTROLE 5 : LA SEPARATION FAIT-ELLE MIEUX QU'UN FILTRE ? ----
    #
    # C'EST LA QUESTION DU PROJET, et cet outil est le premier a pouvoir y repondre. Le
    # temoin est le morceau passe dans un filtre fixe taille sur le meme profil : il ne sait
    # rien du temps, il ne fait que couper des frequences. Si une source lui ressemble a un,
    # la factorisation n'a rien trouve qu'un banc de filtres n'aurait trouve — et alors
    # « la source du piano » n'est qu'une bande de frequences a laquelle on a donne un nom.
    print(f"  au-dela d'un simple filtre   {'  '.join(f'{c:4.2f}' for c in proches)}", end="")
    print("   ok" if min(proches) < 0.95 else "   la NMF n'ajoute rien au filtrage")

    # ---- CONTROLE 6 : deux apprentissages du meme morceau donnent-ils les memes sources ? ----
    if chemin_bis and os.path.exists(chemin_bis):
        with open(chemin_bis, encoding="utf-8") as fh:
            wbis = np.array(json.load(fh)["profils"], np.float64).T
        cos, tenus = reproductibilite(wprof, wbis)
        print(f"  memes sources deux fois      profils {cos:4.2f}   "
              f"rangs conserves {tenus}/{sources}", end="")
        print("   ok" if tenus >= sources - 1 else
              "   LES NUMEROS NE VEULENT RIEN DIRE D'UNE LECTURE A L'AUTRE")

    os.makedirs(dossier, exist_ok=True)
    base = os.path.splitext(os.path.basename(chemin_wav))[0]
    total = max(EPS, sum(float(np.sum(y ** 2)) for y in sons))

    print()
    print(f"  {'source':>7s} {'couleur':>10s} {'part du son':>12s}")
    for s in range(sources):
        chemin = os.path.join(dossier, f"{base}-source{s + 1}.wav")
        ecrire_wav(chemin, sons[s], taux)
        centre = float((wprof[:, s] * np.arange(bins)).sum()
                       / max(EPS, wprof[:, s].sum())) * taux / nfft
        part = 100 * float(np.sum(sons[s] ** 2)) / total
        print(f"  {s + 1:7d} {centre:9.0f} Hz {part:11.1f} %   {os.path.basename(chemin)}")

    print()
    print("  La « couleur » est le centre de gravite du profil : elle dit dans quel registre")
    print("  la source ecoute, pas ce qu'elle contient. C'est l'oreille qui tranche.")
    print()
    print(f"  temoin/ contient le meme morceau passe dans un simple filtre fixe, un par")
    print(f"  source. ECOUTER LES DEUX : si sourceN et temoinN sonnent pareil, la separation")
    print(f"  n'a fait que couper des frequences, et la source ne « suit » aucun instrument.")
    return 0


def reproductibilite(wa, wb):
    """Deux apprentissages du MEME morceau trouvent-ils les memes sources, dans le meme ordre ?

    IL FAUT REPONDRE AVANT D'ECOUTER, ET LA REPONSE N'EST PAS CELLE QU'ON CROIT. La question
    n'a l'air que de propreté : elle décide en réalité de ce que l'oreille peut conclure. Si
    « la source 3 » ne désigne pas le même objet d'une lecture à l'autre, alors entendre le
    piano dans le fichier 3 n'apprend rien sur ce que la case 3 montrera à l'écran demain.

    Deux chiffres, parce que ce sont deux propriétés distinctes et qu'elles ne vont pas
    ensemble :

      profils   les six OBJETS sont-ils les memes, quel que soit leur rang — cosinus, sous
                le meilleur appariement des six aux six
      rangs     l'appariement est-il l'identite — l'ordre du grave a l'aigu tient-il

    Six sources font sept cent vingt appariements : on les essaie tous, ce qui coute moins
    qu'un algorithme d'affectation a ecrire et a verifier.
    """
    a = wa / (np.linalg.norm(wa, axis=0, keepdims=True) + EPS)
    b = wb / (np.linalg.norm(wb, axis=0, keepdims=True) + EPS)
    m = a.T @ b
    n = m.shape[0]
    ordre = max(itertools.permutations(range(n)), key=lambda q: sum(m[i, q[i]] for i in range(n)))
    return float(np.mean([m[i, ordre[i]] for i in range(n)])), sum(i == ordre[i] for i in range(n))


def correlation(a, b):
    """Corrélation de Pearson entre deux sons, sur le morceau entier."""
    a = a - a.mean()
    b = b - b.mean()
    return float(abs(a @ b) / max(EPS, math.sqrt((a @ a) * (b @ b))))


def temoin_fixe(x, wprof, s, taux, nfft):
    """Le morceau filtre par le profil de la source, MASQUE CONSTANT DANS LE TEMPS.

    C'EST LE SEUL TEMOIN HONNETE, et il a fallu deux essais pour s'en apercevoir. Comparer
    une source au bourdonnement du morceau ENTIER n'a pas de sens : une source aigue n'a
    presque pas d'energie basse frequence, donc son enveloppe a un bruit de fond plus bas, et
    le rapport monte sans qu'aucun artefact n'existe. Mesure faite ainsi, la source 4 — un
    demi pour cent du son — sortait a x6,4 pendant que le morceau donnait x1,3.

    On filtre donc le morceau avec le MEME profil, mais par un gain qui ne bouge jamais : le
    contenu spectral est celui de la source, et la modulation du masque n'y est pas. Ce que la
    source a de plus que ce temoin-la est l'artefact, et rien d'autre.

    Le filtre est a phase nulle et se pose en une seule transformee sur le morceau entier :
    aucune trame, donc aucune cadence de trame a laquelle il pourrait battre par construction.
    """
    bins = wprof.shape[0]
    gain = wprof[:, s] / (wprof.sum(axis=1) + EPS)
    sp = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / taux)
    b = np.clip((f * nfft / taux).astype(int), 0, bins - 1)
    g = np.where(f * nfft / taux < bins, gain[b], 1.0 / wprof.shape[1])
    return np.fft.irfft(sp * g, len(x))


def bourdonnement(y, taux, cadence, duree=30.0):
    """La hauteur de la raie à la cadence des trames, dans le bruit de fond de l'enveloppe.

    TROIS JUGES ONT ÉTÉ ESSAYÉS, ET C'EST LE PREMIER QUI RESTE. Il faut dire pourquoi, parce
    que la conclusion n'est pas « celui-ci est bon » mais « aucun ne sait mesurer ce qu'on
    voulait mesurer ensuite ».

      fond large (celui-ci)   stable, mais un fond large n'est pas un fond local
      fond local à la raie    piégé par la musique : sur t04, un pic musical à 360,5 Hz,
                              plus fort dans le témoin que dans la source, tenait lieu de fond
      démodulation exacte     sans moyennage, dénominateur qui s'annule : x1,0 devenait x23,9
                              sur la même source, au même recouvrement

    Les trois s'accordent sur un facteur vingt et se contredisent sur un facteur trois. C'est
    exactement ce qu'il fallait retenir : **la mesure avait la résolution de trancher le
    recouvrement, elle n'a pas celle de juger ce qui reste.** On garde donc le plus stable, et
    l'appelant rend trois verdicts au lieu de deux — dont un qui dit « je ne sais pas ».
    """
    e = np.abs(y[:int(duree * taux)])
    if len(e) < 4096:
        return 0.0
    e = e - e.mean()
    sp = np.abs(np.fft.rfft(e * np.hanning(len(e))))
    f = np.fft.rfftfreq(len(e), 1 / taux)
    fond = np.median(sp[(f > 20) & (f < 400)])
    return max(float(sp[np.argmin(np.abs(f - k * cadence))] / max(EPS, fond))
               for k in (1, 2, 3) if k * cadence < f[-1])



if __name__ == "__main__":
    if len(sys.argv) not in (4, 5):
        print(__doc__)
        sys.exit(1)
    sys.exit(extraire(*sys.argv[1:]))
