# Outils de mesure

Ce que le dépôt ne peut pas contenir — les disques — et ce qu'il doit contenir : la façon
de les mesurer.

## `metronome.py`

Fabrique un signal dont on connaît la vérité : tempo exact, kick sur chaque temps, clap sur
2 et 4, charley sur les croches.

```
python3 outils/metronome.py /tmp/metronome.wav 87.85 90
dotnet run -c Release --project tools/Emotion.Probe -- /tmp/metronome.wav 0 90
```

Attendu : 100 % d'intervalles justes, 0,004 temps d'erreur de phase, tempo publié égal à la
période de la grille. **Tout écart est un défaut de la chaîne, pas de la matière.**

## `banc.sh`

Passe un jeu de morceaux dans la sonde avec une option donnée, puis rend la force, la
stabilité et la couverture moyennes du pouls — les trois grandeurs de `Emotion.Pulse`.

```
./outils/banc.sh base
./outils/banc.sh fermete0.7 fermete=0.7
```

Il attend un dossier de `.wav` ; adaptez les chemins en tête du script à votre corpus. Le
nôtre vit hors du dépôt : aucune œuvre n'est versionnée.

## Confronter à une implémentation de référence

`aubioonset` est installable partout et sert à répondre à « est-ce un vrai événement ».

```
aubioonset -i extrait.wav -O complex > aubio.txt
dotnet run -c Release --project tools/Emotion.Probe -- extrait.wav 0 90 instants=nous
```

**Toujours rapporter le niveau de hasard** : avec 477 attaques sur 90 s et une fenêtre de
±20 ms, aubio couvre déjà 20 % du temps, donc 20 % d'accord ne prouve rien.

Attention : `aubiotrack` (le suiveur de temps) rend 12 temps sur 90 s de métronome parfait,
quel que soit son réglage. Il n'est pas utilisable comme référence de pouls.

## `tempo_reference.py` et `comparer.py` — le contrôle extérieur

Le moteur se jugeait contre lui-même : ses indicateurs comparent les frappes à une grille
calée sur ces mêmes frappes, et sa sonde partage son code, donc ses erreurs. Ces deux
fichiers apportent le point de vue du dehors — autre langage, autre bibliothèque, autre
méthode, autre préférence de tempo.

```
python3 outils/tempo_reference.py morceau.wav rapport=reference/
dotnet run -c Release --project tools/Emotion.Probe -- morceau.wav 0 300 bpmtrace=moteur.txt
python3 outils/comparer.py reference/morceau.json moteur.txt
```

**Il passe ses étalons avant d'arbitrer.** Deux métronomes fabriqués à 87,85 BPM — l'un
avec un kick seul, l'autre avec claps et charleys — doivent tous deux ressortir à 87,9.
Six versions ont échoué avant celle-ci, et chaque échec est écrit dans le code : un peigne
sans recherche de phase, une règle du plus long décalage trop lâche, un doublement sans
condition d'arrêt, un estimateur « non biaisé » qui rendait des corrélations supérieures à
un, un seuil de fondamental qui écartait les subdivisions réelles.

**Sa préférence de tempo n'est pas celle du moteur.** Le moteur penche autour de 90 BPM,
valeur tirée du crate ; celui-ci prend la résonance perceptive publiée par van Noorden et
Moelants (1999), centrée sur 120 BPM. Lui emprunter sa préférence reviendrait à demander
au moteur de se vérifier tout seul.

## `fenetre.py` — le rendu natif, sans navigateur

Elle lit les 256 octets dans `/dev/shm` et dessine. Pas de HTTP, pas de WebSocket, pas de
moteur web : le jour où l'unité de rendu tournera, elle empruntera exactement ce chemin.

```
./run.sh pulse            # dans un terminal
python3 outils/fenetre.py # dans un autre
```

`q` ou Échap pour fermer. **Elle est faite pour être modifiée** : tout ce qui se dessine
tient dans `Mur.paintEvent`, chaque grandeur du paquet est nommée dans `Paquet`. Ajouter une
forme, c'est ajouter une méthode et l'appeler.

La lecture binaire est vérifiée contre le serveur : 84,988 BPM lus dans l'anneau contre
84,98787 rendus par `/ready`. Les décalages de ce fichier et ceux de `GpuPacket` doivent
rester d'accord — c'est tout le contrat.

`tools/Emotion.Ascii` fait la même chose dans un terminal, en C#, sans dépendance. Les deux
valident le même contrat depuis deux langages.

## Isoler une source, et marquer ce qu'on entend

```sh
./outils/voir.sh              # une piste au hasard de l'album, jouee, ecoutee, montree
./outils/voir.sh 5            # la piste 5   ·   ./outils/voir.sh passepartout   par le titre
./outils/voir.sh --direct     # rien ne se joue, le moteur ecoute ce que tu joues toi
```

| | |
|---|---|
| **clic sur une case, ou 1 à 6** | isole une source — les cinq autres s'éteignent sans disparaître |
| **espace maintenu** | un intervalle de présence, à confronter au niveau et au retrait |
| **espace tapé** | des instants, à confronter aux frappes et à la grille |
| **retour arrière** | défaire la dernière marque |
| **Q** | écrit le rapport et ferme |

**Le son sort des enceintes, et c'était le trou.** Le mode « fichier » du moteur analyse un
WAV sans rien jouer : on regardait un écran bouger sans rien entendre, donc sans pouvoir
marquer quoi que ce soit — ce qui vide de son sens tout ce qui suit. Le morceau passe
maintenant par la carte son et le moteur écoute cette même sortie. Aucune horloge en trop
entre ce qu'on entend et ce qu'on voit.

La piste et sa fiche viennent de l'album et du crate : `EMOTION_ALBUM` et `EMOTION_CRATE`
changent les deux chemins si besoin. Sans fiche, le moteur cherche son tempo dans le vide —
43 % de justesse au lieu de 99 — et `voir.sh` le dit à l'écran plutôt que de faire comme si.

C'est la seule mesure du projet qui vienne de l'extérieur du programme. Tout le reste se juge
contre des grandeurs que le moteur calcule lui-même — un décalage commun au juge et au jugé
leur est invisible par construction. Une oreille, non.

Le rapport part dans `~/Documents/emotion-sources/rapports/`, et il porte **les deux côtés** :
les marques, et ce que le moteur publiait au même instant pour la source isolée. Sans le
second, comparer serait impossible : il faudrait rejouer le morceau pour retrouver ses
frappes, et l'alignement obtenu serait approximatif — or c'est justement l'alignement qu'on
mesure. Environ 145 ko par minute.

**Aucun verdict n'est calculé à l'écran.** Le trancher en direct obligerait à décider tout de
suite de la latence de la main, qui n'est pas connue. Elle se lit à l'analyse : si le décalage
est constant, c'est la main ; s'il part dans tous les sens, c'est le moteur.

## `fenetre_reference.py` — retirée du flux, gardée pour les mesures

Elle confrontait le paquet du moteur à un rapport Python précalculé **sur un fichier** — le
tempo, l'énergie, la brillance, la densité d'attaques et les douze bandes, aux bornes exactes
du moteur. En écoute directe, les deux ne parlaient pas du même instant, et elle ne pouvait
rien dire. Elle ne se lance donc plus ; `deux-fenetres.sh` a été supprimé.

Pour une mesure hors ligne sur un fichier, elle garde tout son sens :

```
python3 outils/tempo_reference.py morceau.wav rapport=reference/
./run.sh fichier morceau.wav 63.5
python3 outils/fenetre_reference.py reference/morceau.json 63.5
```

**Une octave n'est pas une erreur de lecture** : annoncer 127,8 quand la fiche dit 63,50,
c'est avoir trouvé le bon pouls et l'avoir compté un niveau plus haut. Elle le nomme — « le
double », « la moitié » — au lieu d'afficher cent pour cent d'écart.

Ce qu'elle ne compare pas, et il faut le dire : la séparation en six sources par timbre. La
contrôler demanderait de réécrire la factorisation, donc de vérifier le moteur avec le
moteur. Les registres grave, médium et aigu sont des tranches de spectre, pas des instruments.

**La fiche ne passe plus par un sélecteur, mais par le lancement** — `./run.sh fichier
morceau.wav 90.92`. Le moteur amorce son tempo dessus sans jamais s'y verrouiller : un disque
poussé au fader est suivi malgré elle.

## `ecouter.sh` — entendre ce que chaque source entend

```sh
./outils/ecouter.sh morceau.wav 87.06        # le BPM de la fiche, s'il est connu
```

La sonde rejoue le morceau hors ligne, **sans ouvrir de port**, et exporte les six profils
spectraux appris. `extraire.py` refait sa propre transformée, retrouve les activations à
profils fixés, répartit le spectre au prorata et resynthétise avec la phase d'origine.

```
morceau-source1.wav … source6.wav      ce que chaque source retient
temoin/morceau-temoin1.wav … 6         le meme morceau dans un simple filtre fixe
```

**Écouter les deux.** Si `sourceN` et `temoinN` sonnent pareil, la séparation n'a fait que
couper des fréquences, et la source ne suit aucun instrument. C'est la seule question à
laquelle aucun chiffre du projet ne sait répondre.

Six contrôles passent avant qu'un fichier soit écrit — reconstruction exacte, somme des six,
bourdonnement à la cadence des trames, distinction des six, écart au filtre fixe,
reproductibilité des profils. Un outil de validation qui se trompe est pire que pas d'outil :
il produit une preuve à charge contre une pièce qui n'y peut rien.

`RECOUVREMENT` et `LISSAGE` se règlent par l'environnement, pour refaire les balayages qui
ont fixé leurs valeurs.

## Le partage des rôles

```
crate  --HTTP REST-->  C#                     le seul reseau legitime
C#     --/dev/shm-->   fenetre.py             le GPU simule : il recoit
C#     --/dev/shm-->   fenetre_reference.py   la mesure : est-ce juste
```

`fenetre.py` n'est pas une interface : c'est le **mock du GPU**. Le jour où l'eGPU sera
branché en PCIe ou USB-C, il lira exactement ces 256 octets, de la même façon. Le fichier se
comporte donc comme lui — lecture seule, jamais bloquant, vidant l'anneau jusqu'au plus
récent sans se plaindre de ce qu'il a manqué.

**Python précalcule et vérifie. C# rend en temps réel.** Aucun des deux ne se vérifie
lui-même, et c'est tout l'intérêt : les indicateurs internes du moteur comparent les frappes
à une grille calée sur ces mêmes frappes, donc un défaut commun aux deux leur est invisible.
Il a fallu un métronome fabriqué, une implémentation écrite dans un autre langage, et les
tempos que le DJ a calés lui-même, pour voir ce que le moteur ne pouvait pas voir seul.
