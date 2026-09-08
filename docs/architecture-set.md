# L'architecture d'une soiree

Decrite par Selim, le 8 septembre 2026. C'est le plan d'ensemble : ce qui suit ne
concerne plus un algorithme mais la facon dont le son traverse le systeme, du telephone au
mur.

## Le principe

> **On ne precalcule pas le morceau. On precalcule ce qui s'y repete.**

Un disque contient deux choses de nature differente. Ce qui revient — la grille, les
frappes, l'entree de la basse, la couleur d'ensemble — s'echantillonne au casque et se
connait d'avance. Ce qui n'arrive qu'une fois — le saxophone du milieu, la voix qui dit
« sunrise » — ne s'echantillonne pas et doit etre entendu au moment ou il passe.

Le systeme ne choisit donc pas entre partition et ecoute : **il fait les deux, sur des
matieres differentes, et c'est ce qui equilibre la charge.**

---

## Les trois etats du son

### 1. Avant que le public n'arrive

Rien n'est branche. Le set vit sur le telephone.

Selim choisit sur son telephone le morceau qui ouvrira la soiree. Il part a l'analyse, et
le GPU en tire de quoi generer une image animee — **avant que la salle ne se remplisse**.

Le GPU n'attend pas d'avoir tout entendu. Passe un certain seuil, il decide qu'il en sait
assez et le fait savoir :

> « c'est bon j'ai assez pour faire, je connais les informations de la piste et j'ai charge
> ce que je sais du signal »

La notification revient sur le telephone. **C'est le seul moment de la soiree ou l'on peut
attendre**, et il faut s'en servir : tout ce qui est prepare ici ne sera pas a decouvrir
en direct.

### 2. Le cue

Selim charge le disque suivant au casque et cale. Quand l'oreille lui dit que ca tient, il
**laisse tourner** — et ce temps-la n'est pas perdu : c'est celui dont le systeme a besoin
pour atteindre son seuil sur le morceau entrant.

Deux signaux gouvernent la suite :

| Signal | Ce qu'il dit | Ou il existe deja |
|---|---|---|
| le cue « appartient » au master | les deux disques sonnent ensemble | `BlendEstimator`, correlation master/cue |
| le seuil est atteint | le systeme en sait assez sur l'entrant | a construire |

Le basculement visuel commence quand le second donne le feu vert. **Pas avant** : un visuel
qui bascule sur un morceau qu'il ne connait pas encore n'a rien a montrer.

### 3. Le master, en continu

Le son bascule, et rien ne s'arrete. Le GPU continue d'ecouter : les BPM bougent, un filtre
s'ouvre, une basse est coupee. Ces informations arrivent regulierement et ajustent la
vitesse a laquelle les formes changent.

---

## L'hysteresis, et pourquoi elle vaut pour tout

> Quand ils changent en live, passer de 93 a 94 ca va meme pas se sentir. Il me faut un
> intervalle de 1 BPM pour chaque changement de vitesse, filtre applique, basse mutee.

C'est une regle generale deguisee en detail de reglage. **Une grandeur qui bouge sous le
seuil du perceptible ne doit pas bouger a l'ecran** : elle ne communique rien et coute un
flottement.

Le chiffre est un ordre de grandeur donne de memoire, a verifier sur le mur. Le principe,
lui, tient : le seuil du perceptible est un ecart de tempo et non une proportion.

**Attention a ne pas confondre avec la cadence d'envoi.** Le paquet part au GPU **a chaque
instant t**, sans condition. Le pas minimal ne decide pas d'envoyer ou non, il decide si la
valeur transportee bouge. Un flux qui ne parlerait qu'en cas de changement priverait le
renderer de son horloge.

---

## Le GPU, scinde en deux

Deux processus, deux natures de travail, et un equilibrage qui tombe naturellement.

| | Processus **cue** | Processus **master** |
|---|---|---|
| Matiere | ce qui se repete : grille, frappes, basse, couleur | ce qui n'arrive qu'une fois : un solo, une voix, un accident |
| Quand | pendant le calage, hors du temps de la salle | a l'instant meme |
| Contrainte | aucune — on peut attendre | tout : c'est la que la latence se voit |
| Sortie | des motifs prets a jouer | des formes creees a la volee |

Ils travaillent **en file** : ce que le cue a prepare est lu, charge, puis execute.

    read   →   load   →   execute

Le master, lui, court-circuite la file quand il entend quelque chose que le cue n'avait pas
pu connaitre.

> **L'equilibrage n'est pas une optimisation, c'est une consequence.** Le percussif et le
> structurel sont repetitifs, donc precalculables ; le melodique et l'expressif sont
> uniques, donc temps reel. Repartir le GPU selon cette ligne, c'est le repartir selon la
> musique.

---

## Le seuil : tranche

Selim propose un pourcentage du morceau — « peut-etre 15 % ». La question merite d'etre
posee autrement, parce que les mesures de la journee donnent des chiffres :

| Grandeur | Temps avant qu'elle ne se stabilise |
|---|---|
| tempo (`TempoTracker`) | ~8 s d'observation, la memoire de l'autocorrelation |
| temps fort (`BeatGrid`) | plusieurs dizaines de mesures, et il plafonne a une fenetre sur deux |
| structure longue (`SectionTracker`) | deux phrases, soit 40 s |
| couleur, registre de basse | quelques secondes |

Un pourcentage fixe est simple et previsible, mais **15 % d'une intro ne valent pas 15 %
d'un refrain** : un morceau qui commence par trente secondes de nappe ne dira rien de sa
grille. Un critere de convergence — publier quand chaque estimateur a cesse de bouger —
colle a ce que le systeme sait reellement, au prix d'une attente imprevisible.

**Decide : convergence, plafonnee a 30 secondes.**

Le plafond vient du metier et non de la technique. Le palier de validation d'un calage est
de seize temps — c'est celui que Selim emploie a l'oreille — et seize temps a 96 BPM font
une quarantaine de secondes de cue en comptant l'approche. **Trente secondes est donc le
temps qu'il accepte de laisser tourner**, pas une contrainte de calcul.

Ce que ce plafond emporte :

| Grandeur | Prete a temps ? |
|---|---|
| couleur, registre de basse, densite | oui, en quelques secondes |
| tempo | oui, ~8 s |
| grille et temps fort | oui, avec la reserve qu'il plafonne a une fenetre sur deux |
| **structure longue** | **non** — deux phrases font 40 s |

La structure longue n'entrera donc pas dans ce que le GPU recoit au moment de la bascule.
Elle continuera de se construire apres, en direct, et servira plus tard dans le morceau.
C'est une consequence a assumer, pas un defaut a corriger : **le systeme livre ce qu'il
sait a l'instant ou l'on en a besoin, et continue d'apprendre ensuite.**

---

## Ce qui existe deja

| Piece | Etat |
|---|---|
| deux sources simultanees | `DualAudioSource` |
| correlation master / cue | `BlendEstimator` |
| relais de tempo entre platines | `TempoTracker.Adopt` |
| transport vers le GPU | `SharedRing`, 112 octets, 1,5 µs par message |
| hysteresis sur le tempo | `TempoTracker`, exprimee en pourcentage |

## Ce qui manque

- Le **seuil de connaissance** et sa notification vers le telephone
- L'**hysteresis generalisee** a toutes les grandeurs publiees
- Le **flag de bascule visuelle**, distinct de la correlation sonore
- Les **deux processus GPU** et leur file — dans `emotion-renderer`
