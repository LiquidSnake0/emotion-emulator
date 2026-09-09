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
