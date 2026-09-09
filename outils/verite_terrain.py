#!/usr/bin/env python3
"""Fabrique une vérité terrain pour le détecteur d'attaques.

POURQUOI CE FICHIER EXISTE.

Le blocage du détecteur n'est pas un algorithme, c'est l'absence de vérité terrain : on
ne peut pas mesurer une précision ni un rappel sans savoir où sont les vrais kicks. Tous
les indicateurs internes comparent les frappes à une grille calée sur ces mêmes frappes,
et un défaut commun aux deux leur est invisible par construction.

CE QUI REND UNE VÉRITÉ POSSIBLE AUJOURD'HUI, ET NE L'ÉTAIT PAS AVANT.

Un morceau de studio a un tempo constant. Sa grille de temps est donc entièrement décrite
par deux nombres : la période, et une phase. Or

  - la PÉRIODE vient du crate, c'est-à-dire du DJ lui-même. Mesurée contre l'album entier,
    l'amorce par la fiche porte la justesse du tempo de 43 % à 99 %. C'est une vérité
    extérieure au système, et calibrée à l'oreille sur les platines.

  - la PHASE vient de l'ÉNERGIE elle-même, repliée sur cette période. On empile toutes
    les périodes du morceau les unes sur les autres et l'on regarde où l'énergie
    d'attaque s'accumule : c'est le temps. Aucun détecteur n'intervient — ni le nôtre, ni
    celui d'un autre. Deux étapes : un repli grossier sur le spectrogramme, qui trouve la
    bonne période du tour, puis un resserrement sur une enveloppe à phase nulle, qui date
    l'attaque à l'échantillon.

CETTE CHAÎNE SE VÉRIFIE, ET ELLE L'A ÉTÉ. Sur `etalon-kick.wav` — des grosses caisses
seules, à 87,85 BPM, dont les clics se relèvent exactement dans le signal — elle rend la
phase à **0,2 ms** près. L'étape grossière seule s'y trompait de 77 ms : c'est la mesure
qui a imposé le raffinement, et non l'inverse.

UNE PREMIÈRE VERSION PRENAIT LA PHASE D'`aubiotrack`, ET ELLE ÉCHOUAIT POUR UNE RAISON
QU'IL FAUT RETENIR. L'idée était qu'un suiveur au double du tempo tombe quand même sur les
vrais temps. C'est vrai, mais repliés sur un tour ces instants forment DEUX PAQUETS
OPPOSÉS, dont la somme vectorielle est nulle : le R de Rayleigh valait 0,005 — le niveau
du hasard exactement — sur neuf morceaux sur dix. La statistique rejetait précisément le
cas qu'elle devait accepter.

LA CONCENTRATION SE MESURE, ELLE NE SE SUPPOSE PAS. On rend le rapport du sommet du profil
replié à sa moyenne : à 1, l'énergie est étale et il n'y a pas de temps à trouver ; le
morceau est alors écarté plutôt que de fabriquer une fausse vérité. Une vérité terrain
douteuse est pire qu'aucune — elle donne une note à des réglages qui ne la méritent pas.

    python3 outils/verite_terrain.py morceau.wav 87.89 [sortie.txt]
"""

import sys

import numpy as np

sys.path.insert(0, __file__.rsplit("/", 1)[0])
from tempo_reference import HOP, NFFT, flux_spectral, lire_mono

# En dessous, le profil replié est trop plat pour désigner un temps. Un profil parfaitement
# étale vaut 1,0 ; on exige que le sommet dépasse la moyenne de moitié.
RELIEF_MINIMUM = 1.5

# Résolution du profil replié, en cases par période. Cent cases donnent 6,9 ms à 87 BPM,
# soit un tiers du pas d'analyse du moteur — assez fin pour ne pas être le facteur
# limitant, assez grossier pour que chaque case reçoive de quoi être lisible.
CASES = 100


def retard_du_flux(rate):
    """Le décalage entre l'indice d'une case de flux et l'instant qu'elle décrit.

    IL SE DÉRIVE, IL NE SE RÈGLE PAS. La trame i couvre les échantillons [i·HOP, i·HOP+NFFT)
    et décrit donc son centre, i·HOP + NFFT/2. Le flux étant une différence entre deux
    trames voisines, il décrit le milieu de leurs deux centres, soit un demi-saut plus
    loin. Total : (NFFT/2 + HOP/2)/taux — 26,7 ms à 48 kHz avec nos 2048 et 512.

    L'ignorer plaçait toute la grille 26,7 ms trop tôt. Le balayage le montrait sans
    ambiguïté : le rappel du détecteur passait de 2,4 % à l'alignement nul à 17,7 % pour un
    décalage de +60 ms. **On corrige ce qui se dérive, et rien d'autre** — ajuster jusqu'à
    ce que la vérité tombe d'accord avec le détecteur reviendrait à la lui faire écrire, et
    c'est exactement la circularité que ce fichier existe pour rompre.

    ELLE NE SUFFIT PAS, ET `etalon-kick.wav` LE DIT SANS APPEL. Sur ce signal fabriqué de
    grosses caisses seules, dont les clics sont dans le signal et se relèvent à
    l'échantillon près, la phase grossière ainsi corrigée tombe encore **77 ms trop tard** :
    pour une grosse caisse, le flux spectral culmine bien après le début de l'attaque,
    parce que le corps grave met des dizaines de millisecondes à s'installer. D'où l'étape
    de raffinement, qui ramène l'erreur à 0,2 ms.
    """
    return (NFFT / 2 + HOP / 2) / rate


def profil_replie(flux, taux, periode):
    """Empile toutes les périodes du morceau et rend le profil moyen d'énergie."""
    profil = np.zeros(CASES, np.float64)
    compte = np.zeros(CASES, np.float64)
    for i, v in enumerate(flux):
        case = int(((i / taux) % periode) / periode * CASES) % CASES
        profil[case] += v
        compte[case] += 1
    return profil / np.maximum(compte, 1)


# Le registre du kick. Au-dessus, on prendrait des charleys et des voix, dont l'attaque ne
# tombe pas au même endroit que celle de la grosse caisse.
COUPURE_HZ = 160.0

# Lissage de l'enveloppe redressée, en millisecondes. Assez pour lisser une période de
# 60 Hz (16,7 ms), assez court pour ne pas étaler l'attaque.
LISSAGE_MS = 8.0

# Fenêtre de raffinement autour de la phase grossière, en fraction de période. Au-delà on
# risquerait d'attraper le contretemps ; en deçà on s'interdirait de corriger l'erreur que
# la résolution du spectrogramme laisse passer.
RAFFINEMENT = 0.15

# De combien la période peut s'écarter de la fiche, en fraction.
#
# LA FICHE DONNE LE VOISINAGE, PAS LE CHIFFRE — c'est la doctrine du projet, et elle vaut
# ici plus qu'ailleurs. Le DJ relève un tempo à deux décimales ; deux dixièmes de pour cent
# d'écart suffisent à décaler la grille de 180 ms au bout de quatre-vingt-dix secondes,
# c'est-à-dire d'un quart de temps, et tout ce qu'on mesurerait ensuite serait du bruit.
#
# Un pour cent laisse corriger cela sans jamais permettre de changer de niveau métrique :
# le plus proche voisin franc est à 50 % ou 100 %. La fiche décide donc toujours QUEL
# tempo, et l'audio seulement sa valeur exacte.
DERIVE_MAX = 0.01
PAS_PERIODE = 400


def enveloppe_sans_retard(x, rate):
    """L'enveloppe d'attaque du registre grave, SANS AUCUN RETARD DE GROUPE.

    POURQUOI CETTE ÉTAPE EXISTE. La phase grossière vient d'un spectrogramme à 2048
    points : une trame couvre 42,7 ms, et l'instant qu'une case de flux décrit n'est
    connu qu'à une vingtaine de millisecondes près, quoi qu'on dérive. Or c'est
    précisément l'ordre de grandeur qu'on veut mesurer sur le détecteur — une vérité
    terrain moins précise que le défaut cherché ne peut pas le voir.

    Un filtre récursif ne conviendrait pas : il déplace ce qu'il filtre, et l'on
    remplacerait une incertitude par un biais. On filtre donc dans le domaine fréquentiel
    sur le signal entier, ce qui est exactement à phase nulle par construction : chaque
    composante ressort sans décalage, et l'enveloppe garde la date de l'attaque.
    """
    n = len(x)
    spectre = np.fft.rfft(x)
    freqs = np.fft.rfftfreq(n, 1 / rate)
    # Un flanc doux plutôt qu'un mur : une coupure raide sonne comme un pré-écho, et un
    # pré-écho AVANCE l'attaque — c'est-à-dire le défaut qu'on cherche à ne pas commettre.
    spectre = spectre * np.exp(-(freqs / COUPURE_HZ) ** 4)
    grave = np.fft.irfft(spectre, n).astype(np.float32)

    redresse = np.abs(grave)
    largeur = max(3, int(LISSAGE_MS / 1000 * rate))
    noyau = np.ones(largeur, np.float32) / largeur
    # « same » centre le noyau : le lissage n'ajoute donc pas de retard non plus.
    lisse = np.convolve(redresse, noyau, mode="same")
    return np.maximum(np.diff(lisse, prepend=lisse[0]), 0)


def energie_a(env, rate, periode, phase):
    """Ce que l'enveloppe du grave accumule à une phase donnée, sur tout le morceau."""
    largeur = max(1, int(0.03 * rate))          # ±30 ms autour du temps
    total = 0.0
    k = 0
    while True:
        i = int((phase + k * periode) * rate)
        if i + largeur >= len(env):
            break
        if i - largeur >= 0:
            total += float(env[i - largeur:i + largeur].sum())
        k += 1
    return total


def affiner(x, rate, periode, phase, env=None):
    """Resserre la phase grossière sur l'enveloppe à résolution d'échantillon."""
    if env is None:
        env = enveloppe_sans_retard(x, rate)
    demi = RAFFINEMENT * periode
    pas = 1 / rate
    cases = int(2 * demi / pas)
    if cases < 8:
        return phase
    profil = np.zeros(cases, np.float64)
    depart = phase - demi
    k = 0
    while True:
        centre = depart + k * periode
        i0 = int(centre * rate)
        if i0 + cases >= len(env):
            break
        if i0 >= 0:
            profil += env[i0:i0 + cases]
        k += 1
    return depart + (int(profil.argmax()) + 0.5) * pas


def grille(chemin, bpm):
    """La grille de temps du morceau. Rend (instants, relief, période)."""
    periode = 60.0 / bpm
    x, rate = lire_mono(chemin)
    flux, taux = flux_spectral(x, rate)
    duree = len(x) / rate
    if len(flux) < 8:
        return [], 0.0, periode

    # On cherche la période qui rend le profil le plus net. Une grille qui dérive étale
    # l'énergie sur tout le tour ; la bonne période la concentre en un point.
    meilleur = (periode, 0.0, None)
    for i in range(PAS_PERIODE + 1):
        p = periode * (1 - DERIVE_MAX + 2 * DERIVE_MAX * i / PAS_PERIODE)
        pr = profil_replie(flux, taux, p)
        m = pr.mean()
        r = float(pr.max() / m) if m > 0 else 0.0
        if r > meilleur[1]:
            meilleur = (p, r, pr)
    periode, relief, profil = meilleur
    if profil is None:
        return [], 0.0, periode

    # Le sommet du profil est le temps. On le prend au centre de sa case : le prendre au
    # bord biaiserait toute la grille d'une demi-case, soit 3,5 ms à 87 BPM — assez pour
    # se voir sur une mesure de justesse à plus ou moins 60.
    phase = ((int(profil.argmax()) + 0.5) / CASES * periode
             + retard_du_flux(rate)) % periode

    # L'AMBIGUITE DU DEMI-TEMPS N'EST PAS LEVEE, ET ON NE PRETEND PAS LE FAIRE.
    #
    # Le profil a deux sommets qui se ressemblent : ce répertoire pose autant de matière
    # entre les temps que dessus, et la basse tombe souvent APRÈS le kick. Sur deux
    # morceaux du bac, cette grille se pose donc exactement à un demi-temps du vrai temps.
    # On le sait parce que les frappes du moteur, fortement concentrées, y tombent à 0,48
    # temps : un train serré ne se décale pas d'un demi-temps par accident.
    #
    # UNE TENTATIVE A ETE FAITE ET RETIREE. Départager les deux sommets par l'énergie
    # d'attaque du grave — le temps est là où frappe la grosse caisse — en réparait deux et
    # en cassait trois. Continuer à régler ce départage jusqu'à ce qu'il donne raison au
    # moteur aurait été exactement la circularité que ce fichier existe pour rompre.
    #
    # Ce qui reste vrai malgré tout : la PÉRIODE est juste, et la phase est juste À UN DEMI-
    # TEMPS PRÈS. `noter_detecteur` rend donc les deux chiffres — l'écart brut, qui inclut
    # cette ambiguïté, et l'écart replié sur un demi-temps, qui mesure la précision sans
    # elle. Lever l'ambiguïté demande une oreille : celle du DJ, qui sait où est le « 1 ».
    phase = affiner(x, rate, periode, phase) % periode
    n = int((duree - phase) / periode)
    return [phase + k * periode for k in range(max(0, n) + 1)], relief, periode


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    chemin, bpm = sys.argv[1], float(sys.argv[2])
    temps, relief, periode = grille(chemin, bpm)
    verdict = "utilisable" if relief >= RELIEF_MINIMUM else "REJETE — profil trop plat"
    print(f"{chemin}  {bpm:.2f} BPM (crate)  periode {1000*periode:.1f} ms")
    print(f"  relief du profil replie  {relief:.2f}   (1,0 = etale)   {verdict}")
    print(f"  {len(temps)} temps, de {temps[0]:.3f} a {temps[-1]:.1f} s")
    if len(sys.argv) > 3 and relief >= RELIEF_MINIMUM:
        with open(sys.argv[3], "w", encoding="utf-8") as fh:
            fh.write("\n".join(f"{t:.3f}" for t in temps) + "\n")
        print(f"  ecrit dans {sys.argv[3]}")
