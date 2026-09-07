# Emotion Emulator

Moteur de projection temps réel pour un set de vinyles. Aux platines, le DJ pose une
face ; le rétroprojecteur en donne le phénomène — des vagues sur un `M-`, un orage sur
un `M+` — animé par le son qui sort réellement des enceintes.

Compagnon de [crate](https://github.com/LiquidSnake0/crate), la base de données du bac
de disques. Les deux se parlent par HTTP, ils ne fusionnent pas.

.NET 10 · ASP.NET Core · SignalR · Canvas 2D · PulseAudio · xUnit · **29 tests**

---

## 1. Le problème

Un visualiseur audio classique ne connaît que le son. Il produit donc la même chose
pour tout : des barres qui montent et descendent. Il ne sait pas qu'un morceau est
mélancolique ou massif, parce que cette information n'est nulle part dans le signal.

À l'inverse, un système qui ne connaîtrait que la fiche du morceau serait figé : il
ignorerait ce qui se joue réellement, et notamment le fait qu'un vinyle **se joue à une
vitesse variable**. Le DJ pitche au fader, sur ±8 % en usage courant et jusqu'à ±16 %.
Un BPM enregistré en base est donc faux dès la première seconde.

D'où la règle qui structure tout le projet :

> **Le son donne le mouvement. La base donne le caractère.**

| Donnée | Source | Raison |
|---|---|---|
| Attaques, énergie, 12 bandes | le **signal** | seule vérité du rythme, insensible au pitch |
| Tempo | le **signal**, dérivé | jamais nécessaire, jamais lu depuis une fiche |
| Famille, Camelot, pochette | la **base** | plus fiable qu'une détection, et stable pendant un fondu |

Le dernier point mérite d'être défendu, parce qu'il va contre l'intuition « détectons
tout ». Estimer une tonalité en temps réel sur un mix est peu fiable en général et
**impossible pendant une transition** : deux disques superposés produisent un accord qui
n'existe dans aucun des deux. La saisie manuelle est ici plus juste que l'algorithme.

---

## 2. Architecture

### 2.1 Vue d'ensemble

```
┌──────────────────┐   HTTP, commandes rares    ┌───────────────────────────┐
│ Crate (PWA iOS)  │ ─────────────────────────► │  Emotion.Server           │
│ le chef          │   POST /deck/cue           │                           │
│ d'orchestre      │        /deck/take          │  ┌─────────────────────┐  │
└──────────────────┘                            │  │ DeckState (verrou)  │  │
                                                │  └─────────────────────┘  │
┌──────────────────┐                            │  ┌─────────────────────┐  │
│ Table de mixage  │ ── PCM ──► parec ────────► │  │ IAudioSource        │  │
│ ou sortie système│                            │  │  └ SpectrumAnalyzer │  │
└──────────────────┘                            │  └─────────────────────┘  │
                                                │           │ ~47 img/s     │
                                                │  ┌────────▼────────────┐  │
                                                │  │ SignalWorker        │  │
                                                │  │ (BackgroundService) │  │
                                                │  └────────┬────────────┘  │
                                                │  ┌────────▼────────────┐  │
                                                │  │ VisualHub (SignalR) │  │
                                                └──┴────────┬────────────┴──┘
                                                            │ WebSocket
                                                  ┌─────────▼──────────┐
                                                  │ Renderer projeté   │
                                                  │ Canvas 2D + clips  │
                                                  └────────────────────┘
```

### 2.2 Deux canaux, et ce n'est pas un doublon

Le point d'architecture le plus discutable au premier regard : pourquoi les commandes
passent-elles par **HTTP** alors qu'une connexion **SignalR** est déjà ouverte ?

Parce que ce sont deux besoins opposés, et les mélanger dégraderait les deux :

| | Commande (`/deck/cue`) | Événement (`frame`) |
|---|---|---|
| Fréquence | quelques dizaines par set | ~47 par seconde |
| Réponse attendue | oui, l'état résultant | aucune |
| Perte tolérable | **non** | **oui**, la suivante arrive dans 21 ms |
| Ordre | strict | sans importance |
| Émetteur | le téléphone | le serveur |

Les séparer a une conséquence pratique décisive : **Crate n'embarque aucun client temps
réel**. Un `fetch` suffit pour commander. La PWA reste légère, et le même appel se teste
en une ligne de `curl` depuis les platines.

C'est une séparation commande/événement au sens du découpage des responsabilités —
l'écriture passe par un chemin transactionnel et acquitté, la lecture continue par un
flux de diffusion. Ce **n'est pas du CQRS** au sens strict, et le prétendre serait
malhonnête : il n'y a ni modèle de lecture distinct, ni magasin séparé, ni projection
asynchrone. L'état tient dans un enregistrement immuable de quelques champs. Du CQRS
ici ajouterait de la cérémonie sans résoudre le moindre problème réel — la question
n'est pas de savoir si le motif est prestigieux, mais s'il paie son coût.

### 2.3 Ports et adaptateurs sur la seule frontière qui bouge

`IAudioSource` est la seule abstraction du projet, et elle est placée exactement là où
l'incertitude est maximale : **d'où vient le son**.

```csharp
public interface IAudioSource
{
    string Name { get; }
    IAsyncEnumerable<VisualFrame> ReadAsync(CancellationToken ct);
}
```

Deux implémentations :

- `MockAudioSource` — fabrique un signal plausible à partir d'un tempo. Kick sur chaque
  temps, charleys sur les contretemps, nappes lentes dans les médiums.
- `PulseAudioSource` — écoute pour de vrai, via `parec` en sous-processus.

`IAsyncEnumerable` plutôt qu'un événement : le flux est **tiré**, pas poussé. Un
consommateur lent ne noie donc pas le producteur, et l'annulation coopérative arrête
proprement le sous-processus par le `finally` de l'itérateur.

Le mock n'est pas un échafaudage jetable. Il sert à deux choses qui survivront au
branchement de la table :

1. Construire et régler tout le rendu **sans matériel**.
2. **Rejouer une séquence à l'identique** : à graine égale, le signal est le même à la
   milliseconde près. C'est ce qui permet de comparer deux versions d'un visuel sur
   exactement le même passage, au lieu de juger à l'œil sur deux écoutes différentes.

Le jour de la vraie table, **une seule ligne change** dans `Program.cs`. Ni le hub, ni
le contrat, ni le renderer ne savent d'où vient le son.

### 2.4 Le sous-processus assumé

`PulseAudioSource` lance `parec` et lit du PCM `s16le` sur sa sortie standard, plutôt
que de passer par une liaison native.

C'est un compromis explicite. Le coût : un processus fils, et une dépendance à un
binaire système. Le gain : aucune bibliothèque native à compiler par plateforme, un
outil présent sur toute machine PulseAudio ou PipeWire, et **le même adaptateur pour les
deux cas d'usage** — seul le nom du périphérique change :

```
alsa_output.…analog-stereo.monitor   ce qui sort des haut-parleurs   (essai sans matériel)
alsa_input.…analog-stereo            l'entrée ligne                  (table branchée)
```

Ce n'est pas un contournement : c'est ce qui permet de valider la chaîne complète
aujourd'hui, sur un ordinateur portable, sans rien acheter.

---

## 3. La chaîne de traitement du signal

Fenêtres de 1024 échantillons à 48 kHz, soit 21 ms — assez court pour qu'un kick reste
net, assez long pour que la résolution fréquentielle serve à quelque chose.

```
PCM ─► fenêtre de Hann ─► FFT radix-2 ─┬─► 12 bandes log      ─► relief
                                       ├─► flux spectral (+)  ─► OnsetDetector ─► attaque
                                       └─► RMS compressé      ─► niveau
                                                                      │
                                                          TempoEstimator ─► BPM, phase
```

**Fenêtre de Hann.** Sans elle, une note qui ne tombe pas exactement sur un bin fuit sur
tout le spectre, et les bandes graves se remplissent de bruit d'aigu.

**FFT écrite à la main.** Le projet a besoin du module du spectre d'une fenêtre de 1024
points, 47 fois par seconde. Une dépendance de calcul scientifique pour cela coûterait
plus en surface qu'elle ne rapporte. 60 lignes, un test qui vérifie qu'une sinusoïde
pure produit son pic au bon bin.

**Bandes logarithmiques**, de 30 Hz à 16 kHz. L'oreille entend le rapport entre deux
fréquences, pas leur différence : douze bandes linéaires donneraient onze bandes d'aigus
et une seule pour tout le grave. On prend le **pic** de chaque bande et non la moyenne,
parce que sur une bande large une moyenne noie la pointe — or c'est la pointe qui se voit
à l'écran.

**Flux spectral positif** pour les attaques : on ne compte que ce qui monte. Une note qui
s'éteint n'est pas une attaque.

**Seuil adaptatif**, et c'est indispensable. Un seuil fixe marcherait sur un morceau et
raterait tout le suivant, puisque le crate va d'un ambient feutré à des batteries sèches.
Chaque valeur est comparée à la moyenne des ~0,9 dernières secondes : le détecteur suit
le morceau au lieu d'être réglé pour lui. Un test vérifie que le même motif, cent fois
plus fort, produit exactement le même nombre d'attaques.

**Tempo par vote, pas par moyenne.** Les écarts entre attaques sont arrondis à 10 ms et
votent. Une moyenne serait détruite par une seule attaque manquée, qui doublerait un
écart ; un vote laisse les erreurs se disperser pendant que la bonne valeur s'accumule.
Il faut qu'un tiers des écarts soient d'accord pour déclarer un tempo — **en dessous, on
préfère ne rien dire**. `Bpm` est donc `float?`, et le renderer sait tourner sans lui.

**Limite connue :** l'ambiguïté d'octave. 87 et 174 BPM produisent les mêmes intervalles
si une frappe sur deux est plus marquée. La famille du morceau pourrait lever le doute
sans jamais fournir la valeur — piste ouverte.

---

## 4. Le modèle des platines

C'est ici que se joue une contrainte de métier qu'aucune considération technique ne peut
arbitrer.

Le DJ cale son prochain disque **au casque**, pendant que le précédent joue encore. Si la
projection changeait au moment où il sélectionne, le public verrait le beatmatch
commencer — c'est-à-dire la coulisse.

```csharp
public sealed record Deck(TrackContext Playing, TrackContext? Cued)
{
    public Deck Cue(TrackContext next) => this with { Cued = next };
    public Deck Take() => Cued is null ? this : new Deck(Cued, null);
    public Deck Drop() => this with { Cued = null };
}
```

| Geste | Endpoint | Effet sur le mur |
|---|---|---|
| Poser une face | `POST /deck/play` | bascule immédiate |
| Caler au casque | `POST /deck/cue` | **aucun** |
| Transition faite | `POST /deck/take` | bascule |
| Renoncer | `POST /deck/drop` | aucun |

Le préparé existe quand même dans le modèle : il alimente le bandeau de contrôle affiché
sur le téléphone — jamais projeté — et il laissera plus tard au renderer le temps de
précharger la pochette et les clips de la face qui vient.

Trois détails défensifs, chacun couvert par un test :

- `Take()` sans rien de calé **ne coupe pas la projection**. Un geste de trop en plein
  set ne doit pas éteindre le mur.
- Une famille inconnue retombe sur `Rest`, pas sur une exception. Un crate en cours de
  correction contient des familles vides.
- `Deck.Empty` projette un repos. Au lancement, avant le premier disque, le mur montre
  quelque chose.

`DeckState` sérialise les mutations sous verrou. Elles sont rares — une poignée par set —
mais lues depuis plusieurs connexions : un `Cue` qui croiserait un `Take` pourrait faire
jouer une face qui vient d'être abandonnée.

---

## 5. Du caractère au phénomène

`Scene.ForFamily` traduit une famille du bac en phénomène projeté.

| Famille | Phénomène | Intensité |
|---|---|---|
| `M-` `M` `M+` | Waves · Swell · Thunder | 0,35 · 0,65 · 1,00 |
| `B-` `B` `B+` | Breeze · Grove · Roots | 0,35 · 0,65 · 1,00 |
| `R` | Bloom | 0,60 |
| `V` | Nebula | 0,60 |
| `S-` | Ember | 0,50 |
| `S` | Void | 0,80 |

**Cette table est une proposition, pas une règle du domaine.** Elle est tirée de la forme
de la palette — deux voies parallèles, du clair au foncé, plus quatre familles isolées —
et non d'une intention écrite. Elle se corrige famille par famille, à l'écoute. Seuls
`Waves` et `Thunder` sont dessinés ; les autres retombent sur une figure géométrique
commune.

L'intensité ne choisit pas le visuel : **elle décide s'il part**. Sur un `M-` la moitié
des occasions passe sans rien, ce qui laisse respirer ; sur un `M+` presque tout se
déclenche. La montée d'un set se voit donc à la densité de l'écran autant qu'à sa
couleur.

Les enums partent par leur **nom** et non leur rang. Un jour ou l'autre une valeur sera
insérée au milieu, et un renderer qui compare des entiers changerait alors de phénomène
sans que rien ne le signale.

---

## 6. Le rendu

Canvas 2D, pas WebGL — pour l'instant. Le rendu se compose en trois couches :

1. **Le fond**, teinte très sombre de la famille. Jamais un noir pur : le noir pur fait
   ressortir la trame du vidéoprojecteur.
2. **La géométrie**, qui porte le rythme. Le chiffre Camelot donne le nombre de branches,
   la lettre donne le trait — `A` anguleux, `B` arrondi.
3. **Les clips**, qui l'habillent. Jusqu'à trois calques en composition additive ; au-delà
   l'écran devient une bouillie.

Le flux de signal est trop rapide pour redessiner à la réception : on garde la dernière
image connue et c'est `requestAnimationFrame` qui cadence. **Le signal pousse, l'écran
tire.**

Les enveloppes sont indispensables : `Beat` est une impulsion vraie sur une seule image.
Si elle restait vraie toute la durée du temps, l'effet serait figé au maximum au lieu de
frapper.

### Les images et les clips

Le dépôt ne contient **aucune œuvre**. `assets/manifest.json` est versionné, les fichiers
qu'il décrit ne le sont pas. Deux raisons, et les deux comptent : des vidéos dans un
dépôt git le rendent inutilisable en trois commits, et du matériel sous copyright dans un
dépôt public devient une pièce à charge plutôt qu'une démonstration.

```json
{ "file": "clips/pluie.webm", "kinds": ["Thunder", "Swell"],
  "weight": 3, "blend": "screen", "every": 4 }
```

Le déclenchement suit les **attaques**, jamais un minuteur : un clip parti sur le kick
reste calé même si le disque est pitché. Sans dossier d'assets, la bibliothèque reste
inerte et la géométrie tourne seule.

Pour du matériel réellement libre : archive.org, Pexels, Pixabay, les banques de la NASA.

---

## 7. Faire tourner

```sh
# Signal fabriqué, aucun matériel requis
dotnet run --project src/Emotion.Server

# Écoute réelle : ce qui sort des haut-parleurs
Signal__Source=pulse \
Signal__Device=$(pactl list short sources | grep monitor | head -1 | cut -f2) \
dotnet run --project src/Emotion.Server
```

`http://localhost:5299` · `F` plein écran · `H` masque le bandeau — il ne doit jamais
finir sur le mur.

```sh
# Poser une face
curl -X POST localhost:5299/deck/play -H 'Content-Type: application/json' -d '{
  "title":"Dreamcast Nostalgia","disc":"MACINTOSH PLUS","side":"B",
  "camelot":"8A","family":"M-","colorHex":"#7FB3D5","coverUrl":null}'

# Caler la suivante au casque : la projection ne bouge pas
curl -X POST localhost:5299/deck/cue -H 'Content-Type: application/json' -d '{
  "title":"Sleep Paralysis","disc":"HAIRCUTS FOR MEN","side":"C",
  "camelot":"3B","family":"M+","colorHex":"#154360","coverUrl":null}'

# Transition faite : le mur bascule
curl -X POST localhost:5299/deck/take
```

```sh
dotnet test        # 29 tests
```

---

## 8. Structure

| Projet | Rôle | Dépendances |
|---|---|---|
| `Emotion.Signal` | Modèle, analyse, sources | **aucune** — ni web, ni paquet tiers |
| `Emotion.Server` | Hub, endpoints, renderer statique | ASP.NET Core, SignalR |
| `Emotion.Signal.Tests` | 29 tests | xUnit |

Le cœur ne dépend de rien : la FFT, la détection d'attaques, l'estimation de tempo et le
modèle des platines se testent sans serveur, sans carte son et sans navigateur.

---

## 9. Ce qui reste

- **Ambiguïté d'octave** du tempo : utiliser la famille comme indice sans jamais en tirer
  la valeur.
- **Geler le tempo pendant un fondu**, où les attaques de deux disques se mélangent.
- **Les huit phénomènes non dessinés.**
- **Le mapping proprement dit** : déformation par homographie pour caler l'image sur la
  surface physique projetée.
- **Passage à WebGL** quand il y aura une carte graphique en face et de vrais clips à
  composer.
- **Précharger la face calée** — le modèle le permet déjà, le renderer ne s'en sert pas
  encore.
