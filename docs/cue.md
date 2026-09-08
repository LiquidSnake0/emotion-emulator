# Ce que le cue donne, et pourquoi ca change l'architecture

## Le principe : la partition et l'interpretation

Aujourd'hui tout est **reactif**. Le systeme entend, analyse, conclut, envoie, et le
renderer dessine. Chaque etage ajoute son retard, et l'on passe son temps a les
raccourcir.

Le cue renverse le probleme. Un disque prepare au casque a deja ete entendu : sa grille,
ses attaques, ses sections sont **connues avant** qu'une seule note n'atteigne la salle.
Il n'y a plus rien a calculer au moment ou il arrive — seulement a informer.

> **Le cue donne la partition. Le direct ne donne que l'interpretation.**

La partition est ce que le disque contient : ou tombent les temps, ou entre la basse, ou
la section change. Elle ne depend pas du DJ. L'interpretation est ce que le DJ en fait :
la position des faders, l'EQ, le filtre, le moment du fondu. Elle ne peut venir que du
direct.

Separer les deux n'est pas une commodite : c'est ce qui sort l'analyse du chemin critique.

---

## Ce que le cue peut donner

### 1. Constantes du disque — un en-tete, envoye une fois

| Donnee | Origine | Remarque |
|---|---|---|
| Tempo de reference | mesure au casque | le pitch fader le decale ensuite : c'est une reference, pas une verite |
| Longueur de phrase | `SectionTracker` | **c'est ici qu'elle est mesurable** — au casque le morceau est seul |
| Profil timbral moyen | 12 bandes moyennees | la couleur d'ensemble du disque |
| Registre de la basse | bandes 0-3 | jusqu'ou descend ce disque, et a quel point il occupe le grave |
| Dynamique | facteur de crete | compresse et plat, ou aere |
| Tonalite, famille, pochette | **le crate** | jamais detectees : elles sont saisies a la main et justes |

### 2. Carte temporelle — une partition, envoyee une fois

Tout ceci est mesurable au casque et **immuable** ensuite.

| Donnee | Ce qu'elle permet a l'ecran |
|---|---|
| Position de chaque temps fort | l'accent de mesure, sans rien detecter en direct |
| Frontieres de phrase | changer de motif **sur** la frontiere, pas apres |
| Entrees et sorties de basse | le cas du DJ : la basse du cue est deja connue |
| Montees et ruptures | la scene se tend avant que le drop n'arrive |
| Attaques : kicks, claps, charleys | les formes partent a l'instant juste, pas 43 ms plus tard |
| Changements d'accord | la teinte tourne sur l'accord, pas apres l'avoir constate |

### 3. Ce que le cue ne donnera jamais

| Donnee | Pourquoi |
|---|---|
| Position des faders et de l'EQ | c'est le geste, il n'existe qu'au moment ou il est fait |
| Ouverture du filtre | idem — et c'est le geste le plus frequent d'un set |
| Correlation master/cue | elle porte sur la <i>somme</i>, qui n'existe que dans la salle |
| Ce que le public entend | deux disques superposes ne sont dans aucun des deux |

C'est aussi ce qui explique pourquoi la structure longue ne se mesure pas sur le master :
**deux morceaux superposes ne peuvent pas se correler avec eux-memes.**

---

## Comment stocker : en position musicale, jamais en millisecondes

Un vinyle se joue a vitesse variable, jusqu'a ±16 %. Une carte horodatee en millisecondes
serait fausse des le premier coup de pitch, et de plus en plus fausse ensuite.

Tout evenement est donc repere par sa **position musicale** — mesure et fraction de temps
— et non par sa date. La conversion en temps reel se fait au dernier moment, avec le
tempo courant. La meme carte reste juste que le disque tourne a 87 ou a 97.

```
en-tete
  tempo de reference, longueur de phrase, profil timbral, registre de basse

evenements, tries par position
  mesure  temps  fraction   type            intensite
  0       0      0.00       kick            0.9
  0       1      0.50       hat             0.3
  0       2      0.00       kick            0.8
  4       0      0.00       phrase          —
  8       0      0.00       bass-in         0.7
  15      3      0.75       drop            1.0
```

Trois consequences valent d'etre notees.

**La carte survit au pitch.** C'est la raison d'etre du choix, et elle suffirait seule.

**Le GPU peut la recevoir en avance.** Rien n'oblige a envoyer un evenement au moment ou
il tombe : la carte entiere peut partir au moment du cue, et le renderer n'a plus besoin
que d'une position. Le flux temps reel se reduit alors a une horloge et aux gestes.

**Ce qui n'est pas dans la carte reste reactif.** Le filtre, les faders, le fondu
continuent d'arriver image par image. Les deux regimes coexistent, et c'est voulu :
tenter de tout precalculer reviendrait a projeter un disque plutot qu'un set.

---

## Ce que ca fait gagner

Chaine actuelle, telle que mesuree :

| Etage | Retard |
|---|---|
| capture PulseAudio | 20 ms |
| fenetre d'analyse, separation, recherche de sommet | **43 ms** (`SpectrumAnalyzer.LatencyMs`) |
| ecriture vers l'anneau partage | 1,5 µs |
| rendu et affichage | ~17 ms a 60 Hz, plus la dalle |

Pour tout ce qui figure dans la partition, **les deux premiers etages sortent du chemin
critique** : l'evenement n'est plus constate, il est attendu. Reste l'exactitude de
l'horloge — `BeatClock` mesure aujourd'hui 4,5 ms d'ecart a la grille.

Soixante millisecondes de gagnees sur les attaques, et surtout la fin d'une contrainte :
il devient inutile de rogner la fenetre d'analyse ou d'affaiblir la separation pour
gagner vingt millisecondes. **La precision de l'analyse cesse de se payer en retard**,
puisqu'elle se fait au casque, hors du temps de la salle.

---

## Le cue n'est pas une lecture, c'est un aller-retour

Une hypothese de ce document etait fausse et il faut la corriger : rien n'est joue
lineairement au casque.

> Je replace le cue au debut, je lance, je beatmatch. Si le beat n'est physiquement pas
> matche, j'ai tendance a revenir, et revenir, et revenir.

Le disque preparé est donc rejoué en boucle sur ses premieres mesures, avec des retours
en arriere constants. **On ne peut pas construire une partition lineaire pendant ce
geste-la**, et une analyse qui supposerait une lecture continue produirait une carte
absurde — des sections qui se repetent, un tempo hache par les arrets.

Trois facons de s'en sortir, par ordre de solidite :

| Approche | Ce qu'elle vaut |
|---|---|
| **Analyser le disque hors session**, une fois, et garder la carte dans le crate | La plus sure : le disque est lu une fois d'un bout a l'autre, sans geste par-dessus. Un disque joue cent fois n'est analyse qu'une |
| Accumuler par passages, en reconnaissant ou l'on est | Marche sans preparation prealable, mais demande de savoir se localiser dans un disque a partir de quelques mesures |
| Analyser au cue en direct | Le plus simple et le moins fiable, pour la raison ci-dessus |

La premiere rejoint une idee deja presente : le crate est la memoire de ce que le DJ
possede. Une carte de morceau y a sa place a cote de la tonalite et de la famille.

## La confiance monte par paliers, et ce sont ceux du metier

> Je laisse toujours quelques echantillons de temps, 2 temps, 4 temps et sur 16 pour
> confirmer que le beat est matche.

Ces trois paliers ne sont pas arbitraires, et le systeme n'a aucune raison d'etre plus
presse que l'oreille qui l'emploie :

| Paliers | Ce qu'il confirme |
|---|---|
| 2 temps | on ne s'est pas cale d'une croche |
| 4 temps | on tient la mesure |
| 16 temps | on tient la phrase, et les deux disques ne derivent pas l'un par rapport a l'autre |

`ContinuityWatch.Trust` rend exactement cette echelle — 0 · 0,35 · 0,7 · 1 — et elle
voyage jusqu'au GPU dans un octet du paquet.

## Un seuil de rendu, jamais d'ecoute

Quand un disque saute ou qu'un calage est en cours, **on ne coupe rien**. L'analyse
continue de chercher ; ce qui change est ce que le renderer a le droit d'en faire.

C'est une question de latence autant que de justesse. Transmettre un octet de confiance
coute le prix d'un octet ; faire redecouvrir la meme chose au renderer couterait le retard
de toute la chaine — soit exactement ce que ce projet passe son temps a supprimer.

| Confiance | Ce que le renderer fait |
|---|---|
| 1 | tout : mesure, phrase, anticipation de la frontiere |
| 0,7 | mesure et phrase, sans anticiper |
| 0,35 | le temps seul, aucune structure |
| 0 | purement reactif — niveau, bandes, frappes constatees |

Le mode a zero n'est pas un mode degrade de secours : **c'est ce que le projet faisait
avant d'avoir une grille**, et il reste parfaitement regardable.

## Detecter la rupture : seuiller la forme, pas l'ecart

Le reflexe est de seuiller l'ecart de phase — au-dela d'un quart de temps, on decroche.
**Ce serait faux**, parce que pousser ou retenir le disque pour recaler produit exactement
cet ecart-la. Un tel seuil decrocherait a chaque beatmatch, c'est-a-dire tout le temps.

Ce qui distingue un accident d'un geste n'est pas l'amplitude, c'est la forme :

| | Ecart a la grille | D'une frappe a la suivante |
|---|---|---|
| pitch bend | grand | **peu de changement** — il croit puis revient |
| saut de sillon | quelconque | **sans aucun rapport** |

Une frappe n'est donc comptee egaree que si elle est **a la fois** loin de la grille et
loin de la precedente.

Deuxieme piege, trouve par le test : apres un saut, l'ecart n'est plus une erreur mais un
tirage, et **pres de la moitie des frappes tombent pres de la grille par coincidence**.
Exiger trois frappes egarees de suite ne se declenchait donc presque jamais. On integre le
desordre au lieu de le compter, et le cas ambigu — proche de la grille mais sans rapport
avec la precedente — ne compte ni pour ni contre.

## Ce qui reste a decider

- **Ou vit la carte.** Calculee a chaque cue, ou mise en cache par disque dans le crate ?
  La seconde evite de reanalyser un disque joue cent fois, au prix d'une invalidation a
  gerer.
- **Quand elle part au GPU.** Au chargement du cue, ou a mi-fondu quand le relais est
  deja amorce ?
- **Ce qu'on fait d'une carte fausse.** Un disque saute, un scratch, un arret : la
  position musicale ne correspond plus. Il faut un retour au mode reactif, et savoir le
  detecter.
- **Le format.** Binaire compact vers l'anneau partage, ou JSON au chargement puis
  binaire ensuite ?
