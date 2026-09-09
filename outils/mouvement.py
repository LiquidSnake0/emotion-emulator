#!/usr/bin/env python3
"""Ce que le rendu calcule encore : la prédiction et l'interpolation.

POURQUOI CES DEUX PIÈCES VIVENT ICI, ET PAS DANS L'ANALYSE.

Tout le lissage est descendu dans `Emotion.Signal` : une raideur qui vit en deux endroits
finit toujours par vivre de deux façons. Il reste pourtant deux opérations que l'analyse
ne peut pas faire à notre place, parce qu'elles dépendent de la cadence de l'écran et non
de celle du signal.

  L'HORLOGE prédit le prochain temps au lieu de le constater.
  L'INTERPOLATION comble les paliers entre deux images d'analyse.

Ce sont deux choses distinctes, et il faut les deux — la leçon est écrite dans le
renderer web : le ressort masquait les paliers de 21 ms par accident, et le retirer sans
brancher l'interpolation a fait apparaître une saccade que personne n'avait introduite.
Elle avait toujours été là, cachée.
"""

import math


class Horloge:
    """Horloge de battement à verrouillage de phase.

    LE PROBLÈME. Toute la chaîne ajoute du retard : la fenêtre d'analyse, la séparation,
    la recherche de sommet, l'interpolation, la boucle d'affichage, la dalle elle-même.
    Une centaine de millisecondes au total, et l'œil décroche vers quarante. Chaque étage
    pris isolément est justifié, et pourtant l'ensemble arrive en retard.

    LA SOLUTION N'EST PAS D'ALLER PLUS VITE. C'est de ne plus réagir au kick, mais de
    l'attendre. Un morceau a un tempo : le prochain temps est prévisible, et on peut donc
    le déclencher au moment où il tombe plutôt qu'après l'avoir constaté. Chaque détection
    ne sert plus à déclencher mais à corriger doucement la phase.

    SUR QUOI ELLE SE CALE, ET POURQUOI CE N'EST PLUS LE KICK. Elle se calait sur les
    drapeaux de frappe, comme le faisait le renderer web. Mesuré sur un morceau du bac :
    les kicks arrivent tous les 962 ms pour un temps de 688, soit sept temps marqués sur
    dix, et les manques ne sont pas réguliers. Une horloge ne verrouille pas sur un train
    troué — la fiabilité restait à 0,00 sur tout le morceau et la prédiction ne partait
    jamais. Toute la stratégie de latence du projet était inerte.

    Le moteur tient pourtant déjà cette phase : `BeatGrid` l'accumule, la corrige, et son
    origine ne bouge pas à chaque coup entendu. Elle est publiée dans le paquet, avec le
    degré de certitude qui va avec. L'horloge se cale donc dessus, et n'a plus à redécider
    ici ce qui a été décidé en amont — c'est la règle du projet : une grandeur ne se
    calcule qu'à un seul endroit.

    CE QU'ELLE FAIT ENCORE, ET QUE L'AMONT NE PEUT PAS FAIRE. La phase arrive par paliers
    de 21 ms ; l'écran en demande une toutes les 16,7. Entre deux images d'analyse,
    l'horloge continue seule à la cadence du tempo, et c'est elle qui déclenche — en
    avance de ce qu'on lui demande, ce que l'analyse ne saurait faire puisqu'elle ignore
    quand l'écran se réveille.

    CE QU'ELLE NE FAIT PAS. Elle ne remplace pas la détection : un clap irrégulier, une
    voix qui entre, un break n'ont pas de grille et doivent rester réactifs. Elle ne sert
    qu'à ce qui est PÉRIODIQUE, c'est-à-dire le kick. Prédire l'imprévisible donnerait un
    visuel qui invente des événements, ce qui est pire qu'un visuel en retard.
    """

    # LE SEUIL SORT DE LA DISTRIBUTION MESURÉE, PAS D'UN CHIFFRE ROND.
    #
    # Sur 70 s d'un morceau du bac, l'accord de la grille se répartit en deux modes — un
    # bas vers 0,3 (955 images), un haut vers 0,65 (1715 images) — séparés par un creux
    # entre 0,4 et 0,5 (282 images). Le seuil se pose dans le creux : c'est là qu'il
    # sépare deux régimes réellement distincts plutôt que de couper une population au
    # milieu. Sur ce morceau, l'horloge prédit un quart du temps.
    #
    # ET C'EST L'ACCORD, PAS LA CERTITUDE DU TEMPS FORT. Celle-ci dit si l'on sait QUEL
    # temps est le « 1 » ; prédire un kick n'en a pas besoin — il suffit de savoir quand
    # le temps suivant tombe. S'y verrouiller laissait la prédiction inerte : médiane
    # 0,20, jamais au-dessus de 0,45.
    SEUIL = 0.5

    def __init__(self):
        self.phase = 0.0
        self.temps_ms = 690.0
        self.verrouille = False
        self.fiabilite = 0.0
        self.vient_de_tirer = False
        self._tire = False
        self._dernier_ecart = 0.0

    def pas(self, dt_ms, bpm, avance_ms=0.0):
        """Avance l'horloge d'un pas de rendu."""
        if bpm and 40.0 < bpm < 200.0:
            # La période suit le tempo mesuré, mais lentement : un tempo qui saute d'une
            # image à l'autre ferait sursauter toute la grille.
            cible = 60_000.0 / bpm
            self.temps_ms += (cible - self.temps_ms) * 0.05

        self.phase += dt_ms / self.temps_ms

        # L'AVANCE S'APPLIQUE AU DÉCLENCHEMENT, PAS À LA PHASE. Avancer la phase elle-même
        # décalerait aussi le recalage : chaque frappe détectée corrigerait vers une
        # position fausse, et la grille finirait par courir après son propre biais. On
        # garde donc la phase vraie — celle sur laquelle `caler` travaille — et l'on tire
        # simplement plus tôt dans le temps, une fois par temps.
        #
        # LA MOITIÉ D'UNE IMAGE, ET CE N'EST PAS UN DÉTAIL. On ne peut tirer qu'aux
        # instants où la boucle de rendu se réveille : à soixante images par seconde, une
        # toutes les 16,7 ms. Le seuil est donc franchi quelque part entre deux réveils, et
        # l'on tire au suivant — en moyenne une demi-image trop tard, soit 8,3 ms perdues
        # sur une avance qui en vise 30. Mesuré avant correction : 23,1 ms d'avance réelle
        # pour 30 demandées, 50,9 pour 60. On anticipe donc d'une demi-image, ce qui centre
        # l'erreur sur zéro au lieu de la laisser toujours du même côté.
        demi_image = dt_ms / 2.0 / self.temps_ms
        avance = min(0.4, max(0.0, avance_ms) / self.temps_ms + demi_image)

        self.vient_de_tirer = False
        if not self._tire and self.phase >= 1.0 - avance:
            self._tire = True
            self.vient_de_tirer = self.verrouille   # on ne prédit que si la grille tient

        if self.phase >= 1.0:
            self.phase -= math.floor(self.phase)
            self._tire = False
        return self.phase

    def caler(self, phase_grille, accord):
        """La grille du moteur vient de dire où elle en est. On s'y range.

        On ne s'y aligne pas d'un coup : sauter à chaque image d'analyse rendrait la
        prédiction aussi nerveuse que ce qu'elle remplace, et l'on perdrait la douceur qui
        fait tout son intérêt. Corriger un cinquième de l'écart laisse l'horloge converger
        en quelques temps tout en absorbant une image isolée qui tombe à côté.

        LA CONFIANCE VIENT DE L'AMONT, ELLE NE SE REDÉDUIT PAS ICI. `accord` est
        `GridAgreement` : les familles de frappes, formées sur le timbre sans jamais
        consulter le tempo, battent-elles à des rapports francs de celui-ci. C'est la seule
        vérification du projet qui vienne d'ailleurs que de l'estimateur lui-même — une
        confiance calculée par celui qu'on veut vérifier ne vérifie rien.

        CE QU'ELLE NE PROMET PAS. Que la grille soit *alignée sur la musique*. Elle dit que
        la période est la bonne, pas que le « 1 » tombe au bon endroit ; l'alignement, lui,
        ne vient que des frappes détectées, et il reste le problème ouvert du projet — le
        défaut est dans `OnsetDetector`, pas dans `BeatGrid`. Ce qui est gagné ici est
        réel mais borné : l'horloge suit désormais une grille continue au lieu d'un train
        de frappes troué, et elle comble les temps que la détection manque.
        """
        ecart = self.phase - phase_grille
        # Ramené dans [-0,5 ; 0,5] : être à 0,95 quand la grille dit 0,02, c'est être en
        # avance de 0,07 sur le tour suivant, pas en retard de 0,93 sur le précédent.
        if ecart > 0.5:
            ecart -= 1.0
        elif ecart < -0.5:
            ecart += 1.0

        self._dernier_ecart = ecart
        self.phase -= ecart * 0.20
        self.phase %= 1.0

        self.fiabilite = max(0.0, min(1.0, accord))
        self.verrouille = self.fiabilite > self.SEUIL

    def perdre(self):
        """Perd le verrouillage : changement de disque, ou silence."""
        self.fiabilite = 0.0
        self.verrouille = False
        self._tire = False

    @property
    def ecart_ms(self):
        """Écart de la dernière détection à la grille. Diagnostic."""
        return self._dernier_ecart * self.temps_ms


class Interpolation:
    """Garde les deux dernières images d'analyse et rend leur interpolation.

    C'est ce qui supprime les paliers de 21 ms. ON NE DEVINE RIEN : on se place entre deux
    mesures réelles, jamais au-delà de la plus récente — extrapoler inventerait du
    mouvement qui n'existe pas dans le son, et cela se verrait au premier silence.
    """

    def __init__(self):
        self.avant = None
        self.courant = None
        self._avant_a = 0.0
        self._courant_a = 0.0

    def pousser(self, paquet, maintenant):
        if self.courant is not None and paquet.sequence == self.courant.sequence:
            return
        self.avant, self._avant_a = self.courant, self._courant_a
        self.courant, self._courant_a = paquet, maintenant

    def alpha(self, maintenant):
        """Position entre les deux dernières images, 0 à 1. Bornée : jamais au-delà."""
        if self.avant is None or self.courant is None:
            return 1.0
        ecart = self._courant_a - self._avant_a
        if ecart <= 0:
            return 1.0
        return min(1.0, (maintenant - self._courant_a) / ecart + 1.0)

    def valeur(self, prendre, maintenant):
        """Interpole un scalaire entre les deux images."""
        if self.courant is None:
            return 0.0
        if self.avant is None:
            return prendre(self.courant)
        a = self.alpha(maintenant)
        p, c = prendre(self.avant), prendre(self.courant)
        return p + (c - p) * a

    def bandes(self, maintenant):
        """Interpole les douze bandes."""
        if self.courant is None:
            return [0.0] * 12
        if self.avant is None:
            return list(self.courant.bandes)
        a = self.alpha(maintenant)
        return [p + (c - p) * a
                for p, c in zip(self.avant.bandes, self.courant.bandes)]
