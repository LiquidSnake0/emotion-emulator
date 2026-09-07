# Emotion Emulator

Moteur de projection temps réel pour les sets vinyle de Selim. Compagnon de
[crate](https://github.com/LiquidSnake0/crate), qui reste la base de données du bac.
Les deux se parlent par HTTP, ils ne fusionnent jamais.

Matériel visé, pas encore là : platines, table de mixage, rétroprojecteur, GPU pour le
rendu final en CUDA. **Ce qui existe aujourd'hui : les disques, le crate, un portable.**
Tout est donc construit pour être réglé sans matériel et branché sans réécriture.

## La règle qui tient tout

> **Le son donne le mouvement. La base donne le caractère.**

| Donnée | Source | Ne jamais faire |
|---|---|---|
| Attaques, énergie, bandes | le signal | — |
| Tempo, hauteurs | le signal, dérivés | **jamais** lire un BPM depuis une fiche |
| Famille, Camelot, pochette | la base | jamais tenter de les détecter |

Un vinyle se joue à vitesse variable : ±8 % au fader, jusqu'à ±16 %. Un BPM stocké est
faux dès la première seconde. À l'inverse, détecter une tonalité pendant un fondu est
impossible — deux disques superposés produisent un accord qui n'existe dans aucun des
deux.

## Décisions à ne pas défaire sans en parler

- **Le préparé n'atteint jamais le mur.** Caler au casque ne change rien à la
  projection : sinon le public verrait le beatmatch, c'est-à-dire la coulisse.
- **On jette, on ne bloque jamais.** Files bornées en `DropOldest`, partout. Une image de
  21 ms arrivée en retard n'a aucune valeur.
- **Ne rien dire plutôt que dire faux.** `Bpm` est `float?` et reste nul tant que le vote
  n'atteint pas son tiers d'accord. Le renderer sait tourner sans.
- **Le relais amorce, il ne verrouille pas.** À mi-fondu le master reprend le tempo du
  cue comme point de départ, puis continue de chercher : le pitch a bougé pendant le
  beatmatch, et l'EQ de la table modifie le spectre.
- **Aucune œuvre dans le dépôt.** `assets/manifest.json` est versionné, les fichiers non.
- **Les enums partent par leur nom**, jamais leur rang.

## Réglages tirés du répertoire, pas de principes

Le crate vit entre 82 et 97 BPM. Plusieurs constantes en découlent et **ne sont pas
générales** — un crate de drum and bass demanderait d'autres valeurs :

| Constante | Valeur | Raison |
|---|---|---|
| `OnsetDetector.MinGap` | 20 fenêtres (426 ms) | entre la noire (620-730 ms) et la croche (310-365 ms) |
| `TempoEstimator.Preferred` | 70–110 BPM | replie l'octave sans lire de fiche |
| `SpectrumAnalyzer.KickBandLimit` | 5 bandes | le registre du kick |
| `Structure.PhraseBars` | 8 mesures | le hip-hop se construit en 8, la house en 16 |
| `TempoEstimator` repli ternaire | ×2/3 | le répertoire est joué en swing |
| `ChordChangeVote` | 0,75 | p99 de la distribution mesurée, pas une intuition |

## Ce que le diagnostic a appris

Chaque correction vient d'une mesure, pas d'une intuition. À conserver dans cet esprit.

| Symptôme | Cause réelle | Correction |
|---|---|---|
| 4 attaques par temps (341 BPM) | flux sur tout le spectre, saturé par le souffle et le crépitement | flux limité au registre du kick |
| écart médian ≈ écart minimal | pas de condition de maximum local | exiger un sommet, pas un franchissement |
| `kick 80` et `clap 81` simultanés | tranches de registre qui se chevauchaient | tranches disjointes + dominance du médium |
| corrélation à 100 % fader fermé | deux morceaux se ressemblent spectralement par construction | centrer bande par bande, garder la dynamique |
| flux arrêté sans erreur | `parec` bloqué sur une sortie d'erreur jamais lue | drainer stderr, relancer si mort |
| 22 µs par message GPU | `ParseHex` et `Scene.ForFamily` sur le chemin chaud | calculer une fois dans `TrackContext` |
| pire cas à 7,5 ms | pages mappées non matérialisées | pré-toucher l'anneau à l'ouverture |
| « les éclairs c'est trop chelou » | **128 ms de retard** — deux étages réglés sans additionner leur total | une fenêtre chacun : 43 ms, et la détection s'est *améliorée* |
| tempo publié sur 4 % des fenêtres | repli d'octave sans point fixe : la plage préférée couvrait 1,57, pas 2 | plage portée à un facteur deux |
| 133 BPM sur un répertoire à 87 | le swing : le détecteur voit le triolet | repli ternaire ×2/3 vers la zone du répertoire |
| 935 changements d'accord en 2 min | seuil deviné à 0,45, soit le milieu de la distribution | 0,75, la queue — un indice fréquent ne discrimine rien |
| tension jamais au-dessus de 0,08 | diviseur des pentes à 0,5 quand le maximum réel est 0,149 | 0,12, mesuré à la sonde |
| 20 ruptures au même instant | l'arc n'avance qu'une fois par temps, son verdict était republié à chaque fenêtre | l'événement ne vaut que pour la fenêtre qui le constate |
| filtre passe-bas invisible | rien ne mesurait la *couleur* du son, seulement ses événements | centroïde et rolloff, mesurés sur le spectre entier avant HPSS |
| 17 messages mixtes sur 200 000 | course latente dans l'anneau, révélée en passant de 96 à 112 octets | vérifier le curseur **après** la copie, pas seulement avant |

## Pièges connus

- **Le mock doit remplir le même contrat qu'un vrai signal.** Il a déjà cessé de produire
  des `Hits` après un changement de contrat, et plus aucun effet ne partait en mode
  simulé. Un mock incohérent ne simule plus rien.
- **Un écran de réglage qui affiche autre chose que ce qui décide est pire qu'aucun
  écran.** Le diagnostic a montré un temps le flux global alors que les attaques venaient
  des enveloppes par registre.
- **L'écran des signaux est cadencé par l'affichage (60 Hz), le signal arrive à 47 Hz.**
  Dédupliquer sur `f.t`, sinon les attaques apparaissent doublées.
- **Ne pas mesurer un maximum brut.** Un incident unique au démarrage le fixe pour la
  soirée. Compter les dépassements.
- **Additionner les retards.** Chaque étage d'analyse en ajoute un, et 40 ms est le seuil
  où l'œil cesse de lier une image au son. `SpectrumAnalyzer.LatencyMs` doit rester
  affiché et sous ce seuil. Un filtrage payé en désynchronisation ne vaut jamais son prix
  sur un visuel.
- **La latence ne se règle pas seulement en allant plus vite.** Après avoir ramené
  l'analyse à 43 ms, il restait ~100 ms bout en bout — interpolation, affichage, dalle.
  La réponse est la prédiction : `BeatClock` verrouille une grille sur le tempo et
  déclenche le kick dessus. Écart mesuré 4,5 ms. **Mais uniquement pour le périodique** :
  prédire un clap irrégulier ou une voix inventerait des événements.
- **Dans un anneau sans verrou, constater qu'une case est valide ne suffit pas.** Entre
  la vérification et la fin de la copie, le producteur peut avoir fait un tour complet et
  réécrit la case. Il faut relire le curseur *après* et jeter la copie s'il a dépassé. Le
  défaut dormait depuis le début ; il n'est apparu que le jour où le message a grossi de
  96 à 112 octets, parce qu'une copie plus longue élargit la fenêtre. **Une course ne se
  corrige pas quand on la voit, elle se corrige quand on l'écrit.**
- **Une preuve unique ne doit pas être diluée par ce qui ne prouve rien.** La confiance
  du temps fort portait sur l'écart *relatif* au meilleur score. Or un backbeat vote
  autant pour deux hypothèses opposées : il n'apprend rien mais gonfle le total, et
  noyait la rupture de section qui, elle, tranchait — cinq centièmes de confiance pour la
  seule preuve du lot. L'écart absolu entre hypothèses *est* la somme des votes
  discriminants.
- **Certaines ambiguïtés ne sont pas des lacunes.** Kick sur 1 et 3 avec clap sur 2 et 4
  est symétrique par décalage de deux temps : l'information n'existe pas dans le signal.
  Rendre -1 est la bonne réponse ; tirer à pile ou face donnerait raison une fois sur
  deux et décalerait toute la structure l'autre fois.
- **Le mock perd le contrat à chaque fois qu'on l'étend.** Deux fois déjà — les frappes,
  puis les registres, le timbre et la structure. Un test vérifie désormais qu'aucun champ
  ne reste à sa valeur par défaut sur quatre secondes.
- **Ce qui suit en continu paraît toujours calé.** L'orbe des graves n'a jamais été en
  retard parce qu'il ne décide de rien. Ne pas en conclure que le reste va bien.

## Face aux bibliothèques du domaine

Mesuré, pas supposé — 89 s d'`instamata`, morceau fiché à 87 BPM :

| Source | Tempo | Erreur |
|---|---|---|
| `aubiotrack` | 117,1 | +34,6 % |
| `aubioonset` + nos contraintes | 115,4 | +32,6 % |
| **ce projet** | **90,4** | **+3,9 %** |

**Hypothèse réfutée :** l'avantage ne vient pas des contraintes de domaine appliquées en
aval — les greffer sur `aubio` ne corrige rien. Il vient du **prétraitement** : HPSS puis
ciblage du registre du kick avant détection.

En .NET il n'existe aucun équivalent d'Essentia ou d'aubio ; `NWaves` couvre le DSP mais
ni le beat tracking ni le HPSS. Le C++ reste justifié **pour le rendu GPU uniquement** —
conteneuriser l'analyse ajouterait un runtime et une frontière IPC pour un gain nul,
l'écriture d'un message coûtant 2,9 µs.

## Le tempo, plafond connu du système

Il n'est publié que sur **4 à 11 % des fenêtres** d'un vrai set. La grille métrique s'en
accommode — une fois calée elle garde sa période — mais tout ce qui suit en dépend.

**Tentative faite et annulée.** Le vote regroupe les écarts par cases de 10 ms, soit
1,4 % d'un temps à 87 BPM : une frappe humaine en sort en permanence. Regrouper par
tolérance relative (±4 %) fait effectivement monter la détection de 4 % à 17 % des
fenêtres — et **dégrade tout le reste** : verrouillage du temps fort de 54 % à 15 %,
intervalle de mesure de 2688 ms à 11926. Le tempo est trouvé plus souvent mais il saute,
et la grille le suit.

> **Un tempo instable est pire qu'un tempo absent.** Absent, la grille garde sa période et
> continue ; instable, elle court après.

Le chantier reste ouvert et il est à traiter pour lui-même, pas en ajustant une constante :
il demande un lissage du tempo publié, ou une hystérésis, ou de voter sur l'histogramme
cumulé plutôt que sur les écarts récents.

## Structure

| Projet | Rôle | Dépendances |
|---|---|---|
| `Emotion.Signal` | modèle, analyse, sources, transport | **aucune** |
| `Emotion.Server` | hub, endpoints, rendu servi en statique | ASP.NET Core, SignalR |
| `Emotion.Signal.Tests` | 79 tests | xUnit |
| `Emotion.Probe` | sonde hors ligne : un WAV entre, des chiffres sortent | — |

Le cœur se teste sans serveur, sans carte son et sans navigateur. **Le garder ainsi.**

## Faire tourner

```sh
dotnet run --project src/Emotion.Server                      # signal fabriqué
Signal__Source=pulse Signal__Device=…monitor dotnet run …    # écoute réelle
Signal__CueDevice=alsa_input.…                               # seconde entrée
Signal__Separate=false                                       # couper HPSS, pour comparer
```

```sh
dotnet run -c Release --project tools/Emotion.Probe -- <set.wav> [début_s] [durée_s]
```

Regarder l'écran renseigne sur ce qu'on voit, jamais sur ce qui décide. La sonde a
corrigé quatre constantes devinées dès sa première exécution.

`http://localhost:5099` · `S` cycle visuel / superposé / signaux · `D` diagnostic ·
`H` masque · `F` plein écran · `/health` pour l'état des tuyaux.

Le port vient de `launchSettings.json` et vaut **5099** — pas 5299, qui a traîné ici et
m'a fait diagnostiquer à côté une collision de ports.

**Mesurer en `Release`.** En `Debug`, l'écriture vers l'anneau coûte trois fois plus, et
toute conclusion sur la latence serait fausse.

## Front

Le renderer est en canvas 2D sans cadriciel : 60 images par seconde, aucun DOM, aucun
état. Angular ou React n'y apporteraient rien et coûteraient sur un chemin où l'on veut
zéro surcoût. **En revanche le futur panneau de contrôle DJ** — listes, formulaires,
état — est un bon candidat Angular, et Selim souhaite en avoir au dossier.

## Façon de travailler

Questions ciblées avant de partir sur une solution. Mesurer avant de corriger, et écrire
la mesure dans le commit. Pas de refactor non demandé, pas de nouvelle dépendance sans le
dire. Commits petits, nommés par fonctionnalité, en français sans accents.

**Ce dépôt est une vitrine.** Selim le présentera en entretien comme le projet dont il est
le plus fier. Le README doit donc défendre chaque choix, y compris les échecs — et ne
jamais réclamer un motif d'architecture qu'il n'applique pas.
