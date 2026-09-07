# Emotion Emulator

Le visuel projeté d'un set vinyle. Aux platines, Selim pose une face ; le
rétroprojecteur en donne le phénomène : des vagues sur un `M-`, un orage sur un `M+`.

Compagnon de [crate](https://github.com/LiquidSnake0/crate), qui reste la base et la
jugeote. Les deux se parlent, ils ne fusionnent pas.

## La règle qui tient tout

> **Le son donne le mouvement, la base donne le caractère.**

Aucun tempo ne vient jamais de la fiche du morceau. Un BPM stocké est faux dès que le
fader bouge, et le crate se joue justement en pitchant. Le rythme est donc **déduit du
signal** : on détecte les attaques, et le tempo n'est qu'une valeur dérivée, utile au
confort et jamais nécessaire. Pitcher un disque de six pour cent ne désynchronise rien.

Ce que la base apporte, elle seule : la famille de couleur, le tag Camelot, la
pochette. Le caractère, pas la vitesse.

## Les pièces

```
iPhone — Crate (PWA)                     le chef d'orchestre
   │  POST /track   famille · couleur · camelot · face · pochette
   ▼
Emotion.Server (ASP.NET Core)
   ├── IAudioSource        analyse temps réel → onsets, énergie, 12 bandes
   ├── Scene.ForFamily     famille → phénomène
   └── VisualHub (SignalR) diffusion ~60 images/s
          ▼
   wwwroot/  renderer projeté
```

| Projet | Rôle |
|---|---|
| `Emotion.Signal` | Le modèle et l'analyse. Aucune dépendance web. |
| `Emotion.Server` | Le hub, l'API, et le renderer servi en statique. |
| `Emotion.Signal.Tests` | Les garanties sur le signal. |

## Le mock n'est pas un bouche-trou

`MockAudioSource` fabrique un signal plausible à partir d'un tempo, sans carte son :
kick sur chaque temps, charleys sur les contretemps, nappes lentes dans les médiums.

Il sert à deux choses, et les deux survivront au branchement de la table :

1. **Construire et régler tout le visuel** avant d'avoir une table et un projecteur.
2. **Rejouer exactement la même séquence** pour comparer deux versions d'un rendu. À
   graine égale, le signal est identique à la milliseconde.

Le jour où la table est branchée, une seule ligne change dans `Program.cs`. Ni le hub,
ni le contrat, ni le renderer ne savent d'où vient le son. C'est toute la raison d'être
de l'interface.

## La table des phénomènes

| Famille | Phénomène | Intensité |
|---|---|---|
| `M-` `M` `M+` | Waves · Swell · Thunder | 0,35 · 0,65 · 1,00 |
| `B-` `B` `B+` | Breeze · Grove · Roots | 0,35 · 0,65 · 1,00 |
| `R` | Bloom | 0,60 |
| `V` | Nebula | 0,60 |
| `S-` | Ember | 0,50 |
| `S` | Void | 0,80 |

**Cette table est une proposition, pas une règle du domaine.** Elle est tirée de la
forme de la palette — deux voies parallèles, du clair au foncé — et pas d'une intention
écrite. Elle se corrige famille par famille, à l'écoute. Seuls `Waves` et `Thunder` sont
dessinés ; les autres retombent sur une figure géométrique commune.

## Faire tourner

```sh
dotnet run --project src/Emotion.Server
```

Puis `http://localhost:5299`. Touche `F` pour le plein écran, `H` pour masquer le
bandeau de réglage — il ne doit jamais finir sur le mur.

Poser une face à la main :

```sh
curl -X POST http://localhost:5299/track -H 'Content-Type: application/json' -d '{
  "title":"Dreamcast Nostalgia","disc":"MACINTOSH PLUS","side":"B",
  "camelot":"8A","family":"M-","colorHex":"#7FB3D5","coverUrl":null
}'
```

Le tempo du mock se règle par `Signal:Bpm`.

## Ce qui reste

- L'entrée ligne, quand il y aura une table branchée sur une machine à GPU.
- La détection d'onsets et de tempo sur vrai signal : flux spectral, seuil adaptatif,
  autocorrélation pour le tempo.
- Les huit phénomènes non dessinés.
- Les clips : découpe et déclenchement au rythme.
- Le mapping proprement dit : déformation par homographie pour caler l'image sur la
  surface projetée.
