# Emotion Emulator

Moteur de projection temps réel pour les sets vinyle du DJ. Compagnon de
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
| Tempo, hauteurs | le signal, dérivés | **jamais verrouiller** sur le BPM d'une fiche — mais s'en servir pour savoir où chercher |
| Famille, Camelot, pochette | la base | jamais tenter de les détecter |

Un vinyle se joue à vitesse variable : ±8 % au fader, jusqu'à ±16 %. Un BPM stocké est
faux dès la première seconde.

**Mais faux n'est pas inutile, et cette nuance a coûté cher.** La règle a longtemps été lue
comme un interdit, alors que le crate est la *première* source d'information du système :
c'est de là qu'on part. La fiche ne dit pas le tempo, elle dit le **voisinage** — et
`SpectrumAnalyzer.Amorcer` recentre la préférence de l'autocorrélation dessus sans jamais
la verrouiller. Mesuré contre les tempos que le DJ a lui-même calés, sur un album entier
du bac : **43 % de justesse sans la fiche, 99 % avec**.

La largeur de la pondération suit ce qu'on sait : un quart d'octave sans fiche, **0,12
octave avec** — soit ±8,7 %, la course exacte du fader d'une platine. Plus serré ferait
mieux d'un demi-point (0,08 → 99,4 %) et trahirait le principe : à ±5,7 %, on exclurait le
disque au moment précis où le DJ pousse le pitch. Les deux morceaux entièrement faux
étaient les plus lents — 59,87 et 63,50 BPM — sous la borne d'une préférence centrée sur 90
« parce que le bac vit entre 82 et 97 ». Cet album-là va de 59,87 à 90,92. À l'inverse, détecter une tonalité pendant un fondu est
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

## Une mesure qui se note elle-même ne prouve rien

Tous les indicateurs du détecteur — « intervalles sur la grille », « frappes bien calées »,
« justesse de phase » — comparent les frappes à la grille, laquelle se cale sur ces mêmes
frappes. **Un décalage commun aux deux leur est invisible par construction.**

C'est ainsi qu'un retard systématique de 21 ms a survécu à des semaines de réglages : en
interne il ne coûtait rien, et chaque tentative d'amélioration se notait contre une
référence qui portait le même biais. Il a fallu deux confrontations extérieures pour le
voir — un métronome fabriqué dont on connaissait la vérité, et les instants d'une
implémentation de référence (`aubioonset`).

Depuis : toute affirmation sur la justesse du détecteur doit s'appuyer sur au moins une de
ces deux références, jamais sur les seuls indicateurs internes.

## Deux mesures extérieures, et ce qu'elles ne disent pas

- **Le métronome fabriqué** — 90 s à un tempo exact — dit si la chaîne a un défaut
  systématique. Elle n'en a pas : 100 % d'intervalles justes, 2 ms d'erreur de phase.
- **`aubioonset`** dit si nos frappes tombent sur de vrais événements. Toujours reporter le
  niveau de hasard : avec 477 attaques sur 90 s et une fenêtre de ±20 ms, aubio couvre déjà
  20 % du temps.

**Aucune des deux ne mesure la pulsation.** La justesse dit « est-ce un vrai événement »,
jamais « est-ce le bon » — un détecteur qui tire sur toutes les attaques y excelle et rend la
grille inutilisable. C'est exactement ce qui arrive au blanchiment adaptatif : +9 points de
justesse, −7 points de verrouillage.

- **Le temps d'accroche** se mesure par la sonde, et distingue deux instants qu'il ne faut
  jamais confondre : quand le **tempo** publié se pose à moins de 1,5 BPM de sa valeur finale
  (le critère est en BPM et non en pour cent, parce que le tempo publié bouge par pas d'un
  BPM entier), et quand le **temps fort** dépasse 0,35. Chacun doit *tenir* cinq secondes.

  Repères sur treize morceaux : **tempo 15,1 s, temps fort 30,2 s**. Et un arbitrage
  structurel dont aucune combinaison essayée ne s'échappe — quatre secondes gagnées sur le
  tempo en coûtent cinq au temps fort, parce que le vote du temps fort accumule ses indices
  sur une grille qu'il suppose stable.

- **`tools/Emotion.Pulse`** est la troisième mesure, et celle qui tranche désormais. Elle
  demande si une suite d'instants forme un pouls, par la statistique de Rayleigh sur des
  fenêtres de quinze secondes. Aucune tolérance à régler, un niveau de hasard qui se calcule,
  et deux sorties : la **force** (concentration) et la **stabilité** (part des fenêtres qui
  retrouvent la même période). Elle ne consulte aucune grille.

  Repères : le métronome donne 0,973 et 100 %. Le répertoire donne **0,695 et 45 %**.

- **Elle compte trois grandeurs, et aucune ne se lit seule.** La **couverture** — marquages
  par période — a été ajoutée après coup, parce que force et stabilité se trichent sans elle :
  en durcissant un seuil, la stabilité montait de 33 à 55 % pendant que les frappes tombaient
  à une pour trois temps. Ce qui est rendu doit être juste, régulier, **et à peu près
  complet**.

**Toute comparaison de détecteur passe désormais par elle.** C'est elle qui a clos le débat
étroit/blanchi — match nul, 0,623 contre 0,645 — après que les deux autres familles
d'indicateurs eurent donné des réponses opposées et également invérifiables.

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
| tempo publié sur 4 % des fenêtres | vote sur les écarts entre attaques consécutives, par cases de 1,4 % | autocorrélation de l'enveloppe |
| intervalle de mesure à 149 ms | la grille recalculait sa position depuis une origine lointaine : changer la période faisait sauter le rang de seize temps | la phase s'accumule, elle ne se recalcule pas |
| 0,90 de confiance sur du bruit blanc | confiance mesurée sur la forme de la courbe, dont la moitié vaut zéro par troncature | sur la hauteur de la corrélation, qui est absolue |
| écart médian entre kicks = 1,21 temps | courbe moyennée sur deux fenêtres avant jugement : un pic d'une fenêtre en ressort étalé sur **deux fenêtres égales**, que le maximum local strict rejette | juger le kick sans lissage — 1,00 temps, intervalles justes 35 → 52 % |
| le battement se figeait, les sources semblaient « buguer » | le renderer consommait les impulsions **par image de rendu** et non par image d'analyse : 47 images/s côté signal, 120 côté écran, donc chaque frappe lue deux à trois fois — et `clock.sync()` rappelé sur la même frappe, ce qui épinglait la phase | une barrière `neuve` : une impulsion se consomme une fois par image d'analyse |
| une seule bavure décalait tout le train | le détecteur devient sourd 0,85 temps après avoir tiré : une bavure au quart du temps bloque le vrai kick, et la détection suivante retombe au quart du temps suivant | la **fermeté** — une frappe doit valoir 0,55 fois la médiane des huit précédentes ; stabilité du pouls 33 → 45 % |
| toutes les frappes publiées 17 à 30 ms trop tard | l'instant valait `tMs + offset_courant` alors que la frappe est jugée sur la fenêtre **précédente**, et que l'offset à employer est celui de cette précédente-là — deux erreurs de même sens | `tMs − fenêtre + offset_précédent` ; l'accord avec aubio passe de 17 à 43 % sur macro, pour un hasard de 21 % |
| `Phase` ne dépassait jamais 0,35 | remplie par `TempoTracker.Phase`, dont l'origine repart **à chaque attaque retenue** : un temps écoulé depuis le dernier coup, pas une position dans la mesure | la prendre sur `BeatGrid`, dont le « 1 » est voté et dont la phase avance seule |
| le grain sortait de sa case | `◆ ◇` avancent de 18 px et `✳ ✷` de 12,57 sur une grille réglée à 9,00 : ils ne sont pas dans la fonte monospace | palette au bon chasse, découpage sur chaque case, contrôle au démarrage |
| toute la grille battait avec le morceau | l'échelle de la scène entière était pilotée par `openness`, un descripteur de timbre calculé toutes les 21 ms et lissé par rien | seuls les gestes lents déplacent le cadre ; le filtre agit à l'intérieur des cases |
| trois extraits d'un set à 96,2 · 96,8 · 95,2 | la préférence de tempo écrasait la mesure au lieu de la départager | plancher à 0,55 : elle penche, elle ne décide pas |
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
- **Un détecteur qui protège peut coûter plus qu'il ne rapporte.** `ContinuityWatch` a été
  commité sur la foi de ses tests unitaires, sans mesure sur du signal réel. Il voyait 5 à
  10 ruptures par 100 s sur un set où aucun disque ne saute — chacune effaçant un temps
  fort acquis en une minute d'écoute, verrouillage de 74 à 31 %. Le test ne pouvait pas le
  montrer : il supposait une détection de kick parfaite. **Un test unitaire vert ne
  remplace pas une mesure sur la matière réelle.**
- **Atténuer plutôt qu'effacer.** Effacer suppose que le détecteur ne se trompe jamais.
  En atténuant, une vraie rupture laisse la nouvelle information l'emporter en quelques
  mesures, une fausse ne coûte qu'un peu de confiance passagère.
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

**Et ce tableau flattait.** Il porte sur un morceau isolé dont la fiche donne la vérité.
Sur un vrai set, la même méthode ne publiait un tempo que sur 4 à 11 % des fenêtres, et
quand elle en publiait un il était faux de 8 % — `aubiotrack` disait 96,4 là où nous
disions 88,9, et c'est `aubio` qui avait raison. Un bon chiffre sur un cas choisi ne dit
rien de la tenue sur la matière réelle.

En .NET il n'existe aucun équivalent d'Essentia ou d'aubio ; `NWaves` couvre le DSP mais
ni le beat tracking ni le HPSS. Le C++ reste justifié **pour le rendu GPU uniquement** —
conteneuriser l'analyse ajouterait un runtime et une frontière IPC pour un gain nul,
l'écriture d'un message coûtant 2,9 µs.

## La séparation HPSS est coupée par défaut

Elle avait été introduite pour empêcher le piano de déclencher les claps, et elle le fait —
le nombre de claps monte de 91 à 113 quand on la coupe. Mais mesurée ailleurs, elle coûte
bien plus qu'elle ne rapporte.

| Verrouillage du temps fort | Macroblank | 640 s | 900 s | 1500 s | moyenne | **pire cas** |
|---|---|---|---|---|---|---|
| avec | 20 % | **84 %** | 52 % | 35 % | 48 % | **20 %** |
| **sans** | **86 %** | 49 % | **60 %** | **43 %** | **60 %** | **43 %** |

Et la latence d'analyse tombe de **43 ms à 21**.

La raison est celle du répertoire : le barber beats étouffe et filtre ses kicks jusqu'à les
noyer, si bien que ce que la séparation retient comme percussif y est surtout du
crépitement de bande — qui n'a ni période ni accent. Elle retire du grave la basse, qui
porte la pulsation autant que la frappe.

`Signal__Separate=true` la réactive, et le restera : sur un répertoire aux kicks francs et
au piano bavard, le calcul pourrait s'inverser.

**Une voie mixte a été essayée et abandonnée** — kick avant séparation, claps après. Elle
donne 40 / 33 / 75 / 33, moins bon que les deux autres, et casse au passage la garde qui
empêche un kick de compter comme clap, laquelle compare les deux registres entre eux.

## Deux corrections trouvées par une vérité terrain

Le DJ donne un morceau de Macroblank à **87 BPM**. Le système en annonçait **108,4**.
Deux défauts empilés, dont aucun n'était visible sur le set.

### 1. La séparation détruisait la pulsation

```
                        90 BPM        108 BPM
avec HPSS               0,066         0,310      ← le mauvais tempo domine
sans HPSS               0,226 (85)    0,144
```

Le barber beats étouffe et filtre ses kicks jusqu'à les noyer. Ce que HPSS retient comme
« percussif » y est alors surtout du **crépitement de bande**, qui n'a aucune période. La
pulsation est portée par le morceau *entier* — la basse, les accords qui pulsent, tout ce
que la séparation met de côté.

> **La séparation sert à décider *ce qui frappe*, jamais *à quelle vitesse ça tourne*.**
> Le tempo se mesure donc avant elle, sur le spectre complet — exactement comme le timbre,
> et pour la même raison.

### 2. La fenêtre de Rayleigh est trop plate

Une fois la pulsation retrouvée, 85,2 BPM restait battu par 127,8 — soit **exactement son
triolet**, et le répertoire est joué en swing. La pondération classique du domaine donnait
0,80 à 128 contre 0,82 à 85 : autant dire rien.

L'oreille juge les tempos en **rapports**, pas en différences : entre 60 et 70 il y a le
même intervalle qu'entre 120 et 140. La préférence est donc gaussienne en logarithme du
tempo, à un quart d'octave d'écart-type — comme le sont déjà les bandes et le centroïde.

| Vérité terrain | Avant | Après |
|---|---|---|
| Macroblank, 87 BPM (le DJ) | 108,4 | **87,6** |
| le set, 96,1 BPM (`aubiotrack`) | 96,6 | 94,2 |

## Le tempo, par autocorrélation

L'ancienne méthode votait sur les écarts entre attaques **consécutives**. Cette seule
contrainte la condamnait : une frappe manquée double l'écart, une frappe parasite le
coupe en deux. Elle regroupait de surcroît par cases de 10 ms, soit 1,4 % d'un temps à
87 BPM — un jeu humain en sort en permanence.

`TempoTracker` autocorrèle l'enveloppe du registre du kick. Aucune décision binaire :
on mesure à quel point l'enveloppe ressemble à elle-même décalée. Trois pièces, chacune
contre un défaut précis.

| Pièce | Ce qu'elle règle |
|---|---|
| autocorrélation | supprime la dépendance aux attaques individuelles |
| fenêtre de Rayleigh | tranche l'octave sans replier à la main |
| inertie sur la courbe | donne la stabilité que le vote n'avait pas |

**Mesuré sur 100 s de set, avant puis après :**

| Extrait | Tempo publié | Valeur | Intervalle de mesure |
|---|---|---|---|
| 300 s | 4 % → **37 %** | 88,9 → **96,4** | 2688 → **2517 ms** |
| 900 s | 11 % → **94 %** | — → **96,6** | 2837 → **2496 ms** |
| 1500 s | 8 % → **96 %** | — → **95,1** | 2880 → **2496 ms** |

`aubiotrack` sur les mêmes extraits : **96,4 et 96,1 BPM**. L'ancienne méthode ne se
contentait pas de se taire, elle **se trompait** — et une référence externe était le seul
moyen de le savoir. L'intervalle de mesure tombe à 0,2 % de la valeur théorique.

Trois extraits du même set rendent presque le même tempo, ce qui avait d'abord paru
suspect : c'est au contraire attendu, un set beatmatché a par construction un seul tempo.

**Ce qui reste ouvert :** le verrouillage du temps fort varie de 38 à 74 % selon le
passage. C'est le vote du downbeat, pas le tempo.

### Le profil des quatre temps

Le vote ne portait que sur des **détections binaires** — un kick est là ou il n'est pas.
Or dans quantité de morceaux le kick tombe sur les quatre temps et n'apprend alors rien.
Mais quatre kicks présents ne sont pas quatre fois le même kick : **celui du 1 porte plus
de grave**. La présence ne discrimine pas, l'amplitude si.

`DownbeatProfile` accumule, pour chacune des quatre positions, ce qu'elle porte en
moyenne — énergie grave, montée du registre du kick, mouvement harmonique — sur des
dizaines de mesures.

| Verrouillage | 300 s | 900 s | 1500 s | pire cas |
|---|---|---|---|---|
| sans le profil | 38 % | **74 %** | 57 % | 38 % |
| **avec** | **59 %** | 71 % | 53 % | **53 %** |

Moyenne identique, **pire cas de 38 à 53**. Pour un usage live c'est le pire cas qui
compte : un passage où le système ne sait pas deux fois sur trois se voit, trois passages
moyens non.

C'est la troisième fois qu'un problème cède en passant de l'événement au continu — le
tempo, la structure longue, le temps fort.

> **Piège traversé :** j'ai d'abord jugé ce profil sur une base cassée. Branché, il
> faisait chuter le verrouillage à 21/38/31 — mais `ContinuityWatch`, commité la veille
> sans mesure, effaçait la grille 5 à 10 fois par 100 s. Une fois cette régression
> réparée, le même profil relevait le pire cas de 15 points. **Ne jamais évaluer une
> addition sur une base dont on n'a pas vérifié l'état.**

### Trois tentatives sur le vote du downbeat, trois échecs

Le score décroît de 0,99 par temps. L'idée — la sienne, et elle est théoriquement juste —
était qu'un backbeat régulier vote sans cesse pour les deux mêmes hypothèses à égalité :
il n'apprend rien, tandis que la décroissance ronge l'avance qu'un changement d'accord
avait acquise. L'oubli devrait donc être déclenché par un changement, pas par le temps.

| Tentative | Verrouillage (300 / 900 / 1500 s) |
|---|---|
| **décroissance fixe 0,99** (en place) | 38 / **74** / **58 %** |
| oubli seulement sur rupture de section | 58 / 34 / 36 % |
| renormalisation par soustraction du minimum | inchangé — le plafond n'est jamais atteint |
| oubli modulé par la dérive de la grille | 41 / 28 / 30 % |

**Ce que la mesure apprend :** un vote est exprimé dans le *référentiel de la grille*, et
ce référentiel glisse. Un vote vieux d'une minute a été porté dans une grille qui n'est
plus la même — il ne désigne plus le même temps. On n'oublie donc pas parce que la musique
change, mais parce que notre propre référence a bougé. Moduler l'oubli par la dérive
mesurée n'a pourtant pas suffi : **le mécanisme reste mal compris, et le constat empirique
tient lieu de règle en attendant.**

### Les ruptures ne marquent pas les phrases

Test direct de l'hypothèse « la musique se construit en 4, 8, 16 » : sur un compteur de
mesures **libre**, jamais réaligné, les ruptures détectées tombent-elles aux frontières ?

```
900 s   modulo 4 : 4 3 5 6    écart au hasard 1,1
1500 s  modulo 4 : 3 4 6 3    écart au hasard 1,5
```

Réparties au hasard — il faudrait dépasser 8 pour parler de structure. Le
`NoveltyDetector` signale une rupture toutes les deux mesures, ce qui est le rythme d'un
changement de timbre, pas d'une section.

**La piste est celle qui vient de marcher pour le tempo, un ordre de grandeur au-dessus :
autocorréler une signature à longue échelle** pour trouver la période de la section
(10 à 45 s) comme on trouve celle du temps (0,3 à 1 s). Une phrase se répète, donc elle
se corrèle avec elle-même. → `SectionTracker`, ci-dessous.

**Piège de mesure à ne pas refaire :** mesurer la position des ruptures sur le compteur
`Bar` est circulaire, puisque `AlignPhrase` le remet à zéro à chaque rupture. Les trouver
sur la mesure 0 ne prouve rien.

## Structure## La structure longue : ce qu'elle trouve, et ce qu'elle ne trouve pas

`SectionTracker` autocorrèle une signature de mesure — 12 bandes, brillance, densité —
sur cinq hypothèses seulement : 4, 8, 16, 32 mesures. **Deux n'y figure pas** : à cette
échelle on décrit le motif que le batteur répète *à l'intérieur* de la phrase, qui se
corrèle mieux que la vraie phrase et n'apprend rien. Mesuré en la laissant : elle
l'emportait deux fois sur trois.

**Sur signal structuré, ça marche parfaitement.** Ressemblance mesurée : **0,999** pour
une phrase de 8 régulière, **0,092** sur du bruit. Un facteur dix.

**Sur le set du DJ, la confiance est nulle.** Meilleur score 0,002. Le suivi dit qu'il
ne sait pas, ce qui est la bonne réponse — mais ça reste un résultat négatif :

> Sur le master, **deux morceaux se superposent**. La signature d'une mesure y mélange ce
> qui sort et ce qui entre, et aucune phrase ne peut se corréler avec elle-même. C'est
> l'argument du DJ pour le cue : au casque le morceau est **seul**, et c'est là que sa
> structure est lisible. La structure longue est probablement une mesure de cue, pas de
> master.

### Trois métriques de confiance, deux fausses

| Mesure | Sur du bruit | Sur une phrase franche |
|---|---|---|
| écart à la moyenne des hypothèses | **1,00** ✗ | 1,00 |
| écart au poursuivant / dispersion | 0,56 | **0,00** ✗ |
| **valeur absolue** (en place) | 0,18 | 1,00 |

La deuxième échoue pour une raison qui vaut d'être retenue : **16 mesures est le double de
8**, donc quand la réponse est 8 son harmonique se corrèle presque autant. La bonne
réponse et son double se tiennent, ce qui écrase l'écart au suivant précisément quand tout
va bien.

### Le faux positif qu'il ne faut pas refaire

Le suivi a d'abord rendu « 8 mesures, 100 % du temps », avec des phrases espacées de
**20,0 s** — soit exactement 8 mesures à 96 BPM. Tout concordait. C'était la **valeur par
défaut jamais modifiée** : une hypothèse manquant de données valait zéro, les scores réels
étant négatifs, et l'estimateur sortait sans rien décider.

> **Une valeur par défaut qui a l'air juste est plus dangereuse qu'une erreur franche.**
> Un test vérifie désormais qu'un signal sans répétition ne produit aucune confiance.

Les scores sont négatifs, et c'est normal : la somme de vecteurs centrés est nulle, donc
la somme de leurs produits scalaires vaut l'opposé de la somme de leurs carrés. On compare
les hypothèses entre elles, jamais à zéro.

## Emprunts à `bonk~`

Le détecteur d'attaques de Puckette, en production depuis 1998. Voir `docs/pd-gem.md`.

| Mesure sur 3 × 100 s | Avant | Après |
|---|---|---|
| écart médian entre kicks | 619 / 597 / 597 ms — **0,94 temps** | **619 / 619 / 619 ms — 0,97 à 0,99 temps** |
| kicks détectés | 140 / 157 / 155 | 142 / 139 / 140 |
| verrouillage du temps fort | 59 / 71 / 53 % | 54 / 76 / 51 % |
| charleys | 331 / 302 / 302 | 331 / 302 / 302 |

Les kicks tombent désormais **sur** les temps, avec un écart identique d'un passage à
l'autre. Le reste est inchangé.

**Le rapport et le masque sont indissociables.** Pris contre la fenêtre précédente — qui
peut être quasi nulle — un rapport explose sur du bruit : les charleys tombaient de 331 à
121. Il se prend contre un masque qui suit la crête, seule référence stable.

**Le masque doit s'effacer entre deux frappes.** J'ai voulu « adapter » le `maskdecay` de
0,7 à 0,94 pour compenser nos fenêtres huit fois plus longues. C'était l'inverse du
raisonnement : s'il tient encore quand la frappe suivante arrive, une frappe régulière de
même amplitude ne produit aucun rapport. **5 charleys détectés au lieu de 302.** La valeur
d'origine était la bonne.

**Un rapport n'a de sens que dans un registre qui se vide.** Les aigus de ce répertoire ne
se vident jamais — souffle de bande et crépitement de vinyle y entretiennent un plancher
permanent, ce qui avait déjà forcé à limiter le flux au registre du kick. Le grave et le
médium prennent le rapport ; **les aigus gardent la différence**.

### Emprunt 3 : non prouvé, mais il a trouvé autre chose

`TransientLocator` situe l'attaque *dans* la fenêtre — passe-bas sur le signal brut, huit
sous-blocs de 2,7 ms — pour que la grille se cale sur l'instant réel de la frappe plutôt
que sur celui de la fenêtre qui la contient.

**Aucune métrique existante ne bouge d'un chiffre.** C'est normal : le verrouillage et
l'écart entre frappes sont comptés à la fenêtre, ils ne peuvent pas voir 21 ms.

La métrique qu'il a fallu créer pour le juger — l'écart des frappes à la grille — a révélé
autre chose :

```
300 s   écart moyen 0,200 temps = 124 ms   dispersion 0,140
900 s   écart moyen 0,217 temps = 139 ms   dispersion 0,125
1500 s  écart moyen 0,225 temps = 144 ms   dispersion 0,149
```

**Une distribution parfaitement aléatoire donnerait 0,25.** La grille n'est donc presque
pas synchronisée avec les frappes qu'elle suit — et c'est cohérent avec le verrouillage du
temps fort qui plafonne à une fenêtre sur deux : *un vote porté dans une grille mal calée
désigne un temps au hasard.*

Ce défaut domine complètement les 21 ms que l'emprunt 3 corrige. Il est donc gardé mais
**non prouvé**.

### Et le diagnostic s'est retourné : ce n'est pas la grille

En cherchant à corriger la phase, deux mesures ont renversé la conclusion :

```
période de la grille  625 / 619 / 634 ms      tempo publié  625 / 619 / 628 ms   → écart 0 à 0,9 %
intervalles entre kicks tombant sur un multiple du temps :  51 % · 39 % · 46 %
frappes à moins de 0,1 temps de la grille :                 35 % · 20 % · 27 %
```

La grille tourne exactement au bon tempo. Mais **moins d'une frappe détectée sur deux
tombe sur un multiple du temps.** La détection produit autant de bruit que de signal, et
une boucle qui se corrige autant sur l'un que sur l'autre poursuit le bruit.

> **Le problème n'est pas dans `BeatGrid`, il est dans `OnsetDetector`.** J'ai passé la
> soirée à améliorer le vote du temps fort, puis la phase de la grille, alors que les deux
> reposent sur des frappes fausses une fois sur deux.

### Quatre tentatives sur la phase, quatre reculs

| Tentative | Verrouillage (300 / 900 / 1500 s) |
|---|---|
| **état commité** | **54 / 76 / 51 %** |
| `Sync` utilisant enfin l'instant réel de la frappe (correction d'un vrai bug) | 24 / 55 / 39 % |
| plus une fenêtre de capture gaussienne | 25 / 65 / 50 % |

Le paramètre `tMs` de `Sync` était bel et bien reçu et jamais lu — un vrai défaut, qui
rendait `TransientLocator` entièrement inopérant. **Le corriger dégrade** : le système
bénéficiait accidentellement du bug.

La fenêtre de capture — ne se laisser tirer que par ce qui tombe près de la grille — est
la bonne réponse de principe, et elle échoue pour une raison connue : un PLL sans phase
d'acquisition ne peut pas s'accrocher, puisque tant qu'il est mal calé, *toutes* les
vraies frappes lui paraissent lointaines. Il faudrait une fenêtre large au départ,
resserrée ensuite.

**Mais rien de tout cela ne vaut avant d'avoir moins de 50 % de fausses frappes.**

### Le détecteur d'attaques : deux échecs, et un fait rassurant

Deux tentatives, aucune ne bat l'existant :

| Tentative | kicks | sur les temps | verrouillage |
|---|---|---|---|
| **en place** (moyenne × 1,8) | 142 / 139 / 140 | 43 / 34 / 38 % | **54 / 76 / 51 %** |
| médiane × 1,8 | 167 / 102 / 108 | 34 / 39 / 43 % | 24 / 60 / 32 % |
| médiane + 3 écarts absolus médians | 136 / 95 / 104 | 45 / 37 / 39 % | 51 / 45 / 35 % |

La médiane est pourtant la statistique juste — une moyenne est tirée vers le haut par les
pics qu'elle sert à détecter. Elle échoue quand même, et **réduire le nombre de détections
n'améliore pas leur qualité** : les frappes supprimées étaient autant des bonnes que des
mauvaises.

**Comparaison externe, sur les mêmes extraits :**

| | attaques / 100 s | écart médian | sur les temps | sur les croches |
|---|---|---|---|---|
| `aubioonset -O hfc` | 483 · 617 | ~160 ms | 3 · 4 % | 16 · 3 % |
| **ce projet** | 142 · 139 | **619 ms** | **43 · 34 %** | 35 · 28 % |

Le hasard donnerait 24 % sur les temps à cette tolérance. Nous sommes donc
significativement au-dessus, et **environ dix fois meilleurs qu'`aubio`** — qui détecte
toutes les micro-attaques sans distinguer le kick.

> **Le blocage n'est plus un algorithme, c'est l'absence de vérité terrain.** On ne peut
> pas mesurer une précision et un rappel sans savoir où sont les vrais kicks. `aubio` ne
> peut pas servir de référence ici : il est plus mauvais que nous. Il faut soit une
> annotation manuelle de quelques mesures, soit un signal de test dont la grille est
> connue par construction.

### La vérité terrain, enfin — et ce qu'elle a révélé

`outils/verite_terrain.py` fabrique la grille d'un morceau de studio à partir de deux
nombres. Un morceau de studio a un tempo constant : sa grille est donc entièrement décrite
par une période et une phase.

| | Source | Pourquoi c'est extérieur |
|---|---|---|
| période | **le crate**, affinée sur l'audio à ±1 % | le DJ l'a calée à l'oreille sur les platines |
| phase | l'**énergie** repliée sur cette période | aucun détecteur n'intervient, ni le nôtre ni un autre |

**Elle se vérifie sur un signal dont la vérité est dans le fichier.** Sur `etalon-kick.wav`
— des grosses caisses seules à 87,85 BPM, dont les clics se relèvent à l'échantillon — la
chaîne rend **87,854 BPM et la phase à 1,3 ms près**. Deux étapes ont été nécessaires, et
la seconde a été imposée par la mesure : le repli grossier sur spectrogramme s'y trompait
encore de **77 ms**, parce que pour une grosse caisse le flux culmine bien après le début
de l'attaque. Le resserrement se fait donc sur une enveloppe filtrée **à phase nulle**,
dans le domaine fréquentiel — un filtre récursif déplacerait ce qu'il mesure.

**Deux fausses pistes, toutes deux instructives.** Prendre la phase des battements
d'`aubiotrack` échouait : au double du tempo, ses instants repliés forment *deux paquets
opposés* dont la somme vectorielle est nulle, et le R de Rayleigh valait 0,005 — le hasard
exactement — sur neuf morceaux sur dix. Et supposer le tempo rigoureusement égal à la
fiche échouait aussi : deux dixièmes de pour cent d'écart font 180 ms de dérive en
quatre-vingt-dix secondes, soit un quart de temps.

### La mesure qui tranche : la concentration à la période du crate

`outils/concentration.py`. Elle replie les frappes sur la période **imposée**, et rend le R
de Rayleigh avec son niveau de hasard calculé. **Elle ne demande aucune phase** — ce qui la
rend robuste là où le rappel ne l'est pas, dater le « vrai » temps sur du barber beats
restant ambigu à quelques dizaines de millisecondes.

> **`Emotion.Pulse` ne pouvait pas répondre à cette question.** Elle cherche elle-même la
> période qui concentre le mieux : un détecteur qui pulse régulièrement sur les
> contretemps, ou une fois sur deux, y obtient une excellente note. « Est-ce un pouls » et
> « est-ce **le** pouls » sont deux questions différentes, et seule la seconde compte pour
> la projection.

### Le plafond, et où va la distance

Mesuré sur le même flux du registre grave, dix morceaux du bac :

| | R |
|---|---|
| **oracle** — sélection parfaite, un sommet par temps | **0,675** |
| sommets locaux au-dessus de moyenne + écart-type, sans grille | 0,051 |
| le détecteur | 0,239 → **0,286** |
| hasard | 0,082 |

**Le signal porte le temps.** Le prendre au plus fort ne le trouve pas — c'est le hasard.
Toute la distance entre 0,05 et 0,68 est de la **connaissance de grille**, et le détecteur
n'en parcourt qu'un tiers.

**Cinq bandes valent mieux que trois** — 30 à 410 Hz au lieu de 30 à 144. R de 0,238 à
0,286, cinq morceaux sur dix au-delà du double du hasard puis huit, accord avec
`aubioonset` de 54,5 à 59,6 % pour un hasard de 25 %. C'est un maximum franc (b4 0,270,
b6 0,270). La valeur était déjà écrite ici et pas dans le code.

**Et ce n'est qu'un tiers du chemin.** L'écart médian entre deux kicks détectés vaut encore
**1,49 temps** : le détecteur saute un temps sur trois.

### Une piste essayée et réfutée : la phase par accumulation

L'idée : les quatre tentatives de fenêtre de capture ont échoué parce qu'une boucle à
verrouillage de phase ne peut pas s'accrocher tant qu'elle est mal calée. Un histogramme de
phase, lui, examine toutes les phases candidates à la fois — pas d'état à faire converger,
donc pas de phase d'acquisition. La période est connue du crate dès la première seconde.

**Mesurée hors ligne, elle donne 188 ms d'écart médian à la vérité pour un hasard de
251 ms.** Presque rien. La mémoire n'y change rien, même infinie. La raison est une mesure
en soi :

| bande repliée | écart médian à la vérité |
|---|---|
| grave 0–160 Hz | 231 ms |
| 0–400 Hz | 114 ms |
| **médium 160–2000 Hz** | **60 ms** |
| tout le spectre | 104 ms |

**Le registre du kick est le pire endroit où chercher la phase du temps.** C'est le médium
qui la porte. « Quelle bande dit qu'il y a une attaque » et « quelle bande dit où est le
temps » ne sont pas la même question — et le projet n'avait posé que la première.

### Et il porte aussi la période, sur les morceaux feutrés

L'estimateur de tempo ne voyait que 30–410 Hz, à travers le **rapport au masque** — la même
combinaison qui avait rendu le repli de phase inutile. Sur un répertoire joué aux
instruments continus, où l'attaque est molle et le grave étouffé, cela se paie.

Une part du flux du médium versée dans l'enveloppe du tempo, mesurée sur l'album :

| `PoidsMedium` | 0 | **0,08** | 0,15 |
|---|---|---|---|
| tempo publié | 54 % | **79 %** | 78 % |
| écart au crate | 0,7 % | **0,6 %** | 0,7 % |

Les morceaux les plus feutrés gagnent le plus : t04 de 22 à 92 %, t09 de 24 à 88,
**« Passepartout » de 27 à 58** — exactement ceux où le grave n'a pas d'attaque franche.
Huit centièmes et non quinze à cause d'un seul morceau : t11, à 59,87 BPM, ne publiait déjà
qu'un tempo sur vingt fenêtres et se tait complètement à 0,15.

> **Le tempo n'était pas faux, il était absent** — et un tempo absent clignote à l'écran.
> C'est ce que le DJ voyait sur Passepartout.

## La grille tient sa phase du repli du médium, plus des frappes

**Le défaut le plus grave du projet, et il était invisible.** `BeatGrid` calait sa phase
sur les kicks détectés. Sa période est juste. Sa phase était **exactement au hasard** :
confrontée à la vérité terrain sur dix morceaux, **0,252 temps d'écart quand un tirage au
sort en donne 0,25**. C'est ce que le DJ voyait à l'écran.

`PhaseFold` empile l'énergie du médium dans un histogramme de phase et regarde où elle
s'accumule. **Toutes les phases candidates sont examinées à chaque instant** : il n'y a pas
d'état à faire converger, donc pas de phase d'acquisition — c'est ce qui le distingue des
quatre tentatives de fenêtre de capture, qui échouaient toutes parce qu'une boucle à
verrouillage de phase ne peut pas s'accrocher tant qu'elle est mal calée.

| | écart à la vérité, en fraction de temps |
|---|---|
| frappes seules (avant) | **0,252** — le hasard |
| repli + frappes (en place) | **0,187** |
| repli seul | 0,190 |

Les frappes n'apportent presque plus rien, et on les garde : atténuer plutôt qu'effacer.

### Trois branchements avant que ça marche, et chacun a appris quelque chose

| Ce qu'on lui donnait | Écart | Pourquoi |
|---|---|---|
| `BandRise` en mode rapport | 169 ms | le masque est fait pour détecter des **événements** : il s'efface entre deux frappes, donc un motif régulier n'y produit presque rien. Il efface exactement la périodicité qu'on cherche. |
| flux brut des douze bandes | 165 ms | agréger avant de différencier fait s'annuler, dans une même bande, une partielle qui monte contre une qui descend |
| **flux bin par bin, 144–1969 Hz** | **91 ms** | on différencie chaque bin, puis on somme les hausses. L'ordre inverse, et le seul qui garde le signal. |

*(hasard : 182 ms)*

### Il cherche sa période, et ce n'est pas un luxe

Le repli est d'une sensibilité qu'on ne devine pas :

| erreur de période | 0 % | 1 % | 2 % | 4 % |
|---|---|---|---|---|
| mémoire 48 temps | 85 ms | 112 | **175** | 183 |
| mémoire 12 temps | 103 ms | 104 | 128 | 147 |

**Deux pour cent suffisent à le ramener au hasard** — sur quarante-huit temps de mémoire,
cela fait un temps entier de dérive. Or le tempo du moteur est faux de 2 à 5 % sur
plusieurs morceaux. Sept histogrammes tournent donc en parallèle à ±1,5 %, et l'on garde
celui qui se concentre le mieux. Le coût est nul à côté de la FFT.

### Le kick lève l'ambiguïté du demi-temps

Le médium ne distingue pas le temps du contretemps : ce répertoire pose autant d'accords
entre les temps que dessus, et l'histogramme a deux sommets qui se ressemblent. Trois
morceaux se calaient donc **à un demi-temps**, et pire encore : sur `t04` la grille se
verrouillait *avec confiance* à côté, ce qui vaut moins que de ne pas se verrouiller.

Le kick, lui, tombe du bon côté — concentration 0,286 pour un hasard de 0,09. On lui
demande donc de départager le sommet et son antipode, **et rien d'autre** : il ne place pas
la grille, il choisit entre deux positions que le médium a déjà trouvées.

| écart à la vérité, en fraction de temps | moyenne | bien calés |
|---|---|---|
| frappes seules | 0,252 | 2/10 |
| repli seul | 0,187 | 2/10 |
| **repli + kick** | **0,178** | **4/10** |

**Le seuil de renversement n'est pas déterminé par la mesure**, et il faut le dire : 1,5
donne 0,207, 2,0 donne 0,178, 3,0 redonne 0,207, 5,0 donne 0,184. Le paysage n'est pas
monotone. Ce qui tient, c'est la présence du mécanisme, pas la valeur.

**Et c'est le pire cas qui le justifie.** Verrouillage du temps fort sur dix morceaux :

| | moyenne | pire cas |
|---|---|---|
| sans repli | 45,2 % | 14 % |
| repli seul | 62,6 % | **4 %** |
| repli + kick | 59,6 % | **17 %** |

Le repli seul gagnait trois points de moyenne de plus et *effondrait* le pire cas. Pour un
usage live c'est le pire cas qui compte — un passage où le système ne sait pas se voit,
trois passages moyens non.

### La vérité terrain a une limite, et elle a failli faire condamner le moteur

`t04` semblait le pire morceau : 333 ms d'écart. En cherchant pourquoi, on a trouvé que
**les frappes du moteur y sont fortement concentrées — R 0,54 — et tombent à 0,48 temps de
la vérité.** Un train serré ne se décale pas d'un demi-temps par accident : c'est la vérité
qui se pose au mauvais endroit, pas le moteur. Idem `t06` (−0,45). Partout ailleurs l'écart
est sous 0,08.

La cause est la même que celle qu'on venait de corriger dans le moteur : le repli
d'énergie a deux sommets qui se ressemblent, et la basse de ce répertoire tombe souvent
*après* le kick.

**Une correction a été essayée et retirée.** Départager les deux sommets par l'énergie
d'attaque du grave en réparait deux et en cassait trois. Continuer à régler ce départage
jusqu'à ce qu'il donne raison au moteur aurait été exactement la circularité que cet outil
existe pour rompre.

`noter_detecteur` rend donc **deux chiffres** : l'écart brut, qui inclut l'ambiguïté, et
l'écart replié sur un demi-temps, qui mesure la précision sans elle.

| | brut (hasard 0,25) | replié (hasard 0,125) |
|---|---|---|
| frappes seules | 0,252 | 0,119 |
| repli | 0,187 | 0,094 |
| repli + kick | **0,178** | **0,088** |

Le gain tient dans les deux colonnes, donc il ne vient pas de l'ambiguïté.

> **Lever le demi-temps demande une oreille.** Celle du DJ : il sait où est le « 1 ». Deux
> ou trois morceaux tapés à la main donneraient la seule vérité qui tranche vraiment.

### Ce qui reste

Le repli ne dit pas **quel** temps est le « 1 » — c'est une autre question, et
`DownbeatProfile` la traite. Et le plafond mesuré reste loin : une sélection parfaite sur le
même flux atteint 0,675 de concentration là où le détecteur fait 0,286.

**Deux erreurs de mesure commises et corrigées en chemin**, toutes deux dans le sens
flatteur : une tolérance relative à l'unité testée, qui rendait les croches *moins*
souvent justes que les temps — impossible, tout multiple du temps étant multiple de la
croche ; puis une tolérance de ±0,12 temps sur une grille de 0,25, qui couvre 96 % de
l'espace et annonçait fièrement « 97 % sur la grille ». **Une métrique se vérifie comme un
algorithme.**

## Structure

| Projet | Rôle | Dépendances |
|---|---|---|
| `Emotion.Signal` | modèle, analyse, sources, transport | **aucune** |
| `Emotion.Server` | endpoints du crate, boucle d'analyse | ASP.NET Core |
| `Emotion.Signal.Tests` | 170 tests | xUnit |
| `Emotion.Probe` | sonde hors ligne : un WAV entre, des chiffres sortent | — |
| `Emotion.Pulse` | la mesure de pulsation, par Rayleigh | — |
| `outils/` | le GPU simulé (`fenetre.py`) et sa mesure (`fenetre_reference.py`) | Python, PySide6 |

Le cœur se teste sans serveur, sans carte son et sans navigateur. **Le garder ainsi.**

## Faire tourner

```sh
./outils/voir.sh [dossier-des-précalculs]   # tout : le moteur, le GPU simulé, la mesure
```

```sh
dotnet run --project src/Emotion.Server                      # signal fabriqué
dotnet run --project src/Emotion.Server -- --sans-reseau     # aucun port ouvert
Signal__Source=pulse Signal__Device=…monitor dotnet run …    # écoute réelle
Signal__CueDevice=alsa_input.…                               # seconde entrée
Signal__Separate=false                                       # couper HPSS, pour comparer
```

```sh
dotnet run -c Release --project tools/Emotion.Probe -- <set.wav> [début_s] [durée_s]
```

Regarder l'écran renseigne sur ce qu'on voit, jamais sur ce qui décide. La sonde a
corrigé quatre constantes devinées dès sa première exécution.

`/ready` donne l'état de préparation du disque en cours, `/health` l'état des tuyaux,
`/deck/*` les commandes du crate. **C'est tout ce que le port sert désormais** : plus
aucune page, plus aucun hub, et `--sans-reseau` n'en ouvre même pas un.

Dans la fenêtre : `Q` ferme, `+` et `-` règlent l'avance du visuel. Le réglage appartient
à ce qui affiche, et à lui seul — c'est la seule pièce de la chaîne qui puisse l'appliquer.

Le port vient de `launchSettings.json` et vaut **5099** — pas 5299, qui a traîné ici et
m'a fait diagnostiquer à côté une collision de ports.

**Mesurer en `Release`.** En `Debug`, l'écriture vers l'anneau coûte trois fois plus, et
toute conclusion sur la latence serait fausse.

## Le vocabulaire visuel

**La scène est une matrice de cases, et elle n'est pas symétrique.** Empiler les formes sur
un axe unique les faisait se recouvrir quoi qu'on fasse — trois tentatives de bornage n'y
ont rien changé, parce que le problème n'était pas le calcul mais la composition : tout
partageait la même colonne.

```
┌──────────┬────────────────┬──────────┐
│ charleys │                │  aiguës  │
├──────────┤     BASSE      ├──────────┤
│  piano   │                │   VOIX   │
├──────────┴────────────────┴──────────┤
│              kick · claps            │
└──────────────────────────────────────┘
```

Une case ne peut pas mordre sur une autre puisqu'elles ne se touchent que par leurs bords,
et l'asymétrie donne à l'œil de quoi se repérer : on apprend « la voix est à droite » plus
vite que « la voix est à trente-quatre centièmes de hauteur ».

**Chaque case expose sa source à sa façon**, et ce n'est pas de la décoration : six motifs
qui se ressemblent obligent à lire l'étiquette pour savoir ce qu'on regarde, et l'œil perd
alors le temps qu'un visuel est censé lui faire gagner.

| Forme | Ce qu'elle fait | Sa composition |
|---|---|---|
| `barres` | monte | des colonnes **espacées**, depuis le bas |
| `onde` | ondule | **une** sinusoïde lente, de bord à bord |
| `orbe` | enfle et retombe | un disque **plein**, centré, grand même au repos |
| `comete` | glisse | une masse qui **se déplace** horizontalement, avec sa traînée |
| `etoile` | éclate sur l'attaque | centré, **minuscule** |
| `grain` | scintille | **réparti** partout |
| `vague` | déferle | des crêtes serrées, front **continu** depuis le bas |

Ordre par défaut, du grave à l'aigu : barres, onde, orbe, comete, **vague**, grain.

**CE QUI DISTINGUE DEUX MOTIFS N'EST PAS LEUR TRACÉ, C'EST LEUR COMPOSITION.** Anneau,
losange et étoile étaient trois dessins différents — et tous centrés, tous en contour, tous
de la même taille : trois taches identiques à un mètre. « Ils ressemblent à des anneaux
lumineux qui clignotent, et ça n'aide pas. » Ce qui les sépare désormais est *où* la matière
se trouve dans la case et *comment elle bouge* : par le bas, de bord à bord, au centre, en
déplacement, partout.

**Aucun anneau parmi les sources.** La case GRAVE en porte un, et c'est la seule forme que
le DJ ait dite bonne — la garder unique est ce qui la rend lisible.

**Et aucune information deux fois.** La case GRAIN du bas montrait un semis nourri des
registres aigus, c'est-à-dire exactement ce que la source 6 montre déjà, avec le même
dessin. Elle porte maintenant le **timbre** — brillance, ouverture du filtre, densité —
qui n'était montré nulle part alors qu'il décide de ce qu'on voit.

**TOUTE FORME RONDE SE MESURE EN PIXELS, ET C'EST LA CORRECTION QUI LES A DÉSEMBROUILLÉES.**
Une case fait trois fois et demie sa hauteur en largeur ; un cercle calculé en cellules y
devient une bande horizontale. C'est pour cela que l'anneau, le losange et l'étoile se
ressemblaient tous — trois motifs différents, écrasés en la même barre. La case GRAVE, elle,
mesurait déjà en pixels, et c'est la seule forme que le DJ ait dite bonne : on a généralisé
ce qui marchait.

**Les lèvres ont été retirées.** Elles voulaient dire « ce qui chante » et ne disaient rien
— « les lèvres, ça ressemble à rien ». Une masse qui enfle et retombe se lit sans
apprentissage. L'octet 3 porte donc l'orbe.

**L'aigu reçoit la vague.** Il n'a ni attaque nette ni hauteur stable, seulement une
agitation : un motif centré lui va mal, il lui faut quelque chose qui bouge partout à la
fois.

## L'enveloppe de chaque source

**Ce que le système ne savait pas dire.** Chaque source publiait son niveau, sa hauteur et
un drapeau de frappe. Rien de tout cela ne distingue une corde pincée d'un souffle : l'une
monte d'un coup puis meurt, l'autre s'installe et ne frappe jamais. Les deux produisent le
même niveau moyen et la même hauteur, et le rendu leur donnait donc le même mouvement.

> « Un instrument à corde c'est une frappe suivie d'une onde courte ou longue, un
> instrument à vent c'est en continu, il ne frappe pas. »

| | Ce que ça mesure | Aucun réglage |
|---|---|---|
| `pique` | la pente rapportée à la crête | une montée qui atteint la crête en une fenêtre vaut 1 |
| `tenue` | la moyenne rapportée à la crête | l'inverse du facteur de crête, un rapport pur |

**Les deux sont indépendantes, et c'est ce qui les rend utiles.** Un orgue : piqué faible,
tenue forte. Un woodblock : l'inverse. Un piano : piqué fort et tenue moyenne — c'est-à-dire
exactement « une frappe suivie d'une onde », où la longueur de l'onde **est** la tenue.

Deux octets par source, à l'offset 216 : le mot d'une source fait huit octets et ils sont
tous pris. Elles vivent donc dans un second bloc, ce qui ne change rien à la garantie qui
comptait — deux sources n'écrivent jamais dans le même octet.

### L'interpolation passe avant l'enveloppe, et l'ordre n'est pas indifférent

Les deux se ressemblent — toutes deux lissent quelque chose — et les intervertir détruirait
précisément ce qu'on vient de mesurer. La règle qui les sépare est celle qui tient déjà tout
le reste : **un descripteur se mêle, un événement jamais.**

- `pique` et `tenue` décrivent la **nature** d'une source. Ils se moyennent sur une seconde
  et demie en amont et ne bougent pas d'une fenêtre à l'autre : les mêler entre deux images
  ne perd rien et supprime les paliers de 21 ms. Ils passent donc par l'interpolation, avec
  le niveau.
- La **frappe** ne passe pas, et c'est pour ça qu'elle survit.

**Et l'enveloppe agit après, au rendu** : `pique` et `tenue` interpolés règlent la façon
dont l'impulsion brute retombe. Inverser — enveloppe d'abord, interpolation ensuite —
mêlerait deux retombées calculées à des instants différents, ce qui arrondirait l'attaque
même quand `pique` vaut 1. On aurait dépensé un descripteur pour décrire une netteté que le
rendu venait d'effacer.

### Ce que ça change à l'écran

| | Avant | Après |
|---|---|---|
| retombée de l'impulsion | la même pour toutes | `0,7 + 8 × (1 − tenue)` par seconde |
| force de l'attaque | `+ 0,6` pour toutes | `+ 0,15 + 0,75 × pique` |
| vitesse de l'onde | fixe | `3,4 − 2,2 × tenue` — ce qui tient ondule lentement |
| étiquette de la case | le nom de la forme | **`pincé` · `frappé` · `tenu`** |

L'étiquette existe parce qu'un écran qui montre autre chose que ce qui décide est pire
qu'aucun écran : sans elle on voit un mouvement sans savoir s'il décrit un instrument qui
frappe ou un qui souffle.

## Le « buffering » n'en était pas un

Le DJ décrit un hoquet. Mesuré, **rien ne hoquette dans le flux** : les paquets arrivent à
46,9 par seconde, zéro perdu, pire intervalle 36,9 ms ; la fenêtre rend une image en 4,5 ms
médians sur un budget de 16,7. Deux affichages sautaient, et c'est tout.

**Le tempo clignotait** parce qu'il n'était publié que quatre images sur cinq — « ne rien
dire plutôt que dire faux » porte sur ce que le moteur *affirme*, mais un nombre qui
disparaît vingt fois par seconde ne se lit pas. La fenêtre garde donc la dernière valeur
connue, **en gris** : ce n'est pas dire faux, c'est dire « voilà ce que c'était », et la
couleur dit que ce n'est plus frais. (Depuis `PoidsMedium`, il n'y a plus de trou du tout
sur macro : 0 % d'images sans tempo.)

**Le curseur de mesure reculait**, et c'était le plus visible. `Phase` vaut zéro tant que le
« 1 » n'est pas identifié — un tiers du temps — donc le curseur retombait au début du
bandeau à chaque fois que le vote lâchait. Le rattraper par la phase du temps ne faisait que
déplacer le saut : à chaque bascule entre les deux régimes, **quarante fois par minute**.

> **Un repère qui saute est pire qu'un repère absent** : l'œil suit le saut et perd la
> musique.

La fenêtre tient donc une position locale qui avance **toujours** à la cadence du temps, et
qu'elle *tire* vers la phase publiée quand celle-ci existe — la règle de partout ailleurs :
corriger une fraction, jamais recaler d'un coup. Plus aucun retour à zéro ; le pire
déplacement en une image vaut 3 % d'une mesure, quand le moteur change d'avis sur le temps
fort.

**Et il se calcule après l'interpolation**, pour la même raison que l'enveloppe : nourri du
paquet brut il avancerait par paliers de 21 ms au lieu de suivre l'écran — c'est-à-dire
qu'il produirait le hoquet qu'il est censé supprimer.

## Le rôle des sources dans l'orchestre : essayé, mesuré, abandonné

L'idée était bonne et l'image du DJ juste : « le mec qui fait le tambour joue le métronome
pour les autres, celui au saxophone est chargé de jouer les mêmes notes mais à des instants
précis ». Trois rôles se mesurent en repliant une source sur la période du temps —
**métronome**, **ponctuel**, **continu** — et ils auraient dit à la grille quelles sources
peuvent témoigner à la place du kick quand il se retire.

**Elle a échoué deux fois, et la seconde ferme la question.**

| nourrie de | résultat sur six morceaux du bac |
|---|---|
| les **frappes détectées** de chaque registre | 34 « continu » sur 36 |
| les **montées de niveau** de chaque source | **36 sur 36** |

La substitution détections → énergie continue, qui a payé trois fois ailleurs dans ce
projet, ne paie pas ici. Et le diagnostic dit pourquoi :

```
tours contributifs   12 à 53        assez de matière
concentration R      0,05 à 0,17    pour un hasard de 0,13 à 0,25
```

**Ce n'est pas un problème de seuil, c'est un problème de signal.** Les montées de niveau
des six sources séparées ne sont pas calées sur le temps — leur concentration est au niveau
du hasard. La séparation produit des activations qui varient doucement et ne conservent pas
la structure rythmique. On ne peut pas mesurer la régularité d'une source à partir d'une
grandeur qui n'est pas régulière.

**Le critère d'abandon avait été fixé avant la mesure** — plus de 24 « continu » sur 36 —
et il n'y a pas eu de second essai. Les deux octets du rôle et de la place sont rendus au
paquet ; seul le **retrait** subsiste, qui ne dépend d'aucun classement.

> **Deux erreurs de méthode attrapées par les tests avant la mesure réelle**, et elles
> valent d'être notées. Le niveau de hasard était d'abord tiré de la répartition de
> l'énergie entre les cases — donc plus une source était concentrée, plus le seuil montait :
> une source parfaitement régulière rendait R = 1 pour un seuil de 1,25 et se voyait classée
> « continue ». Puis un niveau constant, qui ne monte qu'une fois en entrant, remplissait une
> case et paraissait parfaitement concentré. **Un test unitaire ne prouve pas qu'une idée
> marche, mais il coûte mille fois moins cher qu'une mesure pour prouver qu'elle est mal
> écrite.**

## Une absence n'est pas un changement

> « Le seul changement qui justifierait de recheck le beat est un changement, pas un mute
> du kick. »

Le principe est juste et il s'applique partout. Ce qui se tait n'a rien démenti : il faut
**tenir** ce qu'on savait, et ne se raviser que devant une contradiction.

| | Avant | Après |
|---|---|---|
| `BeatGrid` | la phase s'accumule seule | déjà juste |
| `GridAgreement` | fenêtre glissante de 64 frappes, sans horloge | déjà juste — par construction plus que par intention |
| verrouillage de l'horloge | un seuil sur la valeur de l'instant | **hystérésis** : on s'accroche à 0,5, on ne lâche qu'à 0,25 |
| `SourceEnvelope` | décroît pendant le silence | **gelée** après un retrait |

### Deux silences, et les confondre casse la mesure

C'est le piège de cette correction, et la première version y est tombée.

- Le silence **entre deux notes** est ce qui fait la tenue d'un pizzicato : c'est lui qui
  abaisse la moyenne sous la crête. Le geler mesurerait un pizzicato comme un souffle,
  c'est-à-dire détruirait le descripteur qu'on venait de construire.
- Le silence d'un **retrait** est autre chose : la source ne joue plus pendant des mesures.
  Celui-là doit geler, sans quoi le violon oublie qu'il était un violon pendant le creux.

**Seule la durée les sépare, et elle n'est connue qu'après coup.** On continue donc de
mesurer pendant le silence — indispensable au pizzicato — mais on garde de quoi revenir en
arrière : si le silence dure plus d'une seconde et demie, on restaure l'état de la dernière
note, sans la décroissance subie pour rien.

**Le rendu est en caractères** parce qu'il doit rester léger — il n'y a pas de GPU sous la
main, et un remplissage de texte coûte une fraction d'un dégradé. Ils donnent en prime une
identité que des polygones translucides n'avaient pas : celle d'un terminal, ce qui va bien
à un projet qui passe son temps à mesurer.

**Une voix ne se déplace pas, elle enfle.** La bande qui montait et descendait
« rebondissait comme une balle de basket ». Les lèvres qui l'ont remplacée ne se lisaient
pas davantage. Le contour mélodique déplace donc l'orbe dans **ce qui reste de place une
fois son rayon posé** — jamais librement, sinon la forme sort de sa case dès que les deux
se cumulent, et une forme coupée ne se reconnaît plus.

La palette des sources est fixe d'un disque à l'autre ; la couleur du disque teinte les
**cadres** de la matrice. Aucun rouge.

## Où vit le lissage

**Dans l'analyse, plus dans le rendu.** Chaque grandeur continue traversait un ressort
côté renderer. L'unité CUDA aurait dû les réimplémenter tous, avec les mêmes raideurs — et
une règle qui vit en deux endroits finit par vivre de deux façons, comme le Camelot avant
elle.

| | Amorti par | Raideur |
|---|---|---|
| niveau, 12 bandes | `Damper` (analyse) | 16 · 20 |
| registres grave / médium / aigu | `Damper` | 14 · 22 · 40 |
| ouverture, brillance, densité | `TimbreTracker`, déjà | — |
| tension | `ArcDetector`, déjà | — |
| **kick, clap, charley, rupture** | **personne — et c'est voulu** | — |

Les raideurs sont celles qu'employait le renderer : le mouvement ne change pas en changeant
de place, et **aucune latence n'est ajoutée** puisque le ressort existait déjà.

> **Les événements restent bruts.** Une impulsion lissée n'est plus une impulsion. C'est
> la seule chose que le renderer calcule encore — il déclenche, il n'amortit plus.

**Amortir et interpoler sont deux choses différentes, et il faut les deux.** L'amortissement
donne sa masse au mouvement ; l'interpolation comble les trous entre deux images d'analyse.
Le ressort côté rendu masquait les paliers de 21 ms *par accident* — le retirer sans
brancher `FrameLerp` sur ces grandeurs a fait apparaître une saccade que personne n'avait
introduite : elle avait toujours été là, cachée. `FrameLerp` n'était branché que sur le
niveau et les douze bandes ; il l'est désormais sur tout ce qui est continu.

Côté rendu, `Lue` remplace `Spring` partout où l'amortissement est descendu : elle porte la
valeur sans la retoucher. Garder les deux amortirait deux fois et rendrait tout mou.

## Le rendu : plus une seule ligne de JavaScript

```
crate  --HTTP REST-->  C#                       le seul réseau légitime
C#     --/dev/shm-->   outils/fenetre.py        le GPU simulé : il reçoit
C#     --/dev/shm-->   outils/fenetre_reference.py   la mesure : est-ce juste
```

Le rendu a tourné un an dans un navigateur. Il en est sorti pour une seule raison : **il
imposait un réseau là où il n'en faut aucun.** La page recevait par WebSocket ce que
l'unité de rendu lira sur PCIe ou USB-C avec un eGPU. Tant que ce maillon existait, la
latence mesurée n'était pas celle du système visé.

`outils/fenetre.py` lit exactement les 256 octets de `GpuPacket` — même contrat, même
décalages, aucune traduction. C'est ce qui en fait une mesure et non une illustration.

**Deux pièces vivent encore côté rendu, et deux seulement** (`outils/mouvement.py`), parce
qu'elles dépendent de la cadence de l'écran et non de celle du signal :

| | Ce qu'elle fait | Mesuré |
|---|---|---|
| `Horloge` | attend le temps au lieu de le constater | suit la grille à **5,2 ms** de médiane ; avance demandée 30 ms → réelle 39 |
| `Interpolation` | comble les paliers de 21 ms entre deux images | — |

### L'horloge se cale sur la grille, plus sur les frappes

Elle se calait sur les drapeaux de kick, comme le faisait le renderer web. Mesuré sur
`macro.wav` : **une frappe tous les 962 ms pour un temps de 688** — six à sept temps
marqués sur dix, et les manques ne sont pas réguliers. Une horloge ne verrouille pas sur un
train troué : la fiabilité restait à **0,00 sur tout le morceau**, et toute la stratégie de
latence du projet était inerte.

`BeatGrid` tient pourtant déjà cette phase — elle s'accumule, elle se corrige, son origine
ne bouge pas à chaque coup entendu. Elle est désormais publiée : `Structure.BeatPhase`, un
octet dans le paquet.

| | Ce que ça répond | Où c'est publié |
|---|---|---|
| `Phase` | où l'on est dans la **mesure** de quatre temps — **nulle si le « 1 » est inconnu** | offset 24, flottant |
| `BeatPhase` | où l'on est dans le **temps** — toujours remplie | offset 117, un octet |
| `GridSure` | sait-on **quel** temps est le « 1 » | offset 118 |
| `GridAgreement` | la **période** est-elle la bonne, dit par une voie indépendante | offset 119 |

**Un octet suffit** : 1/256 de temps vaut 2,7 ms à 88 BPM, huit fois plus fin que le pas de
21 ms qui la produit.

**Le verrou est `GridAgreement`, et surtout pas `GridSure`.** Les confondre a coûté une
soirée : anticiper un temps ne demande pas de savoir où l'on est dans la mesure, seulement
quand le suivant tombe. Verrouillé sur `GridSure` (médiane 0,20, jamais au-dessus de 0,45),
rien ne partait — alors que la phase publiée tourne à **87,0 temps/min pour un tempo de
87,3**. `GridAgreement`, lui, vérifie la période par les familles de frappes, formées sur le
timbre sans jamais consulter le tempo : c'est la seule vérification du projet qui ne soit
pas circulaire.

**Le seuil sort de la distribution, pas d'un chiffre rond.** Sur 70 s, l'accord se répartit
en deux modes — un bas vers 0,3 (955 images), un haut vers 0,65 (1715) — séparés par un
creux entre 0,4 et 0,5 (282). Le seuil se pose dans le creux : **0,5**.

**Ce qui est gagné, et ce qui ne l'est pas.** L'horloge suit maintenant une grille continue
au lieu d'un train troué, et comble les temps que la détection manque. Elle prédit **2 à
25 % du temps selon le passage**. Elle ne promet pas que la grille soit *alignée sur la
musique* : l'alignement ne vient que des frappes détectées, et **le défaut est dans
`OnsetDetector`, pas dans `BeatGrid`** — c'est le problème ouvert du projet.

> **La relecture de fichier n'acceptait pas la fiche.** `IAcceptsCue` et `ILearnsTracks`
> ne vivaient que sur `PulseAudioSource` : `TrackMemory.Play` ne trouvait pas d'apprenant,
> et l'amorce n'était jamais posée. **Toute mesure faite sur un WAV portait donc sur un
> moteur non amorcé** — c'est-à-dire sur l'autre système, celui à 43 % de justesse au lieu
> de 99. Et c'est précisément le chemin qu'on emprunte pour mesurer, puisqu'un fichier se
> rejoue à l'identique quand un set ne se rejoue pas. Corrigé : `WavAudioSource` implémente
> les deux.

**Il faut les deux, et les confondre coûte cher.** Amortir donne sa masse au mouvement,
interpoler comble les trous. Le ressort du renderer web masquait les paliers *par
accident* ; le retirer sans brancher l'interpolation a fait apparaître une saccade que
personne n'avait introduite — elle avait toujours été là, cachée.

Une impulsion se consomme **une fois par image d'analyse**, jamais par image de rendu :
47 images par seconde côté signal contre 60 côté écran, donc chaque frappe serait lue une
à deux fois de trop, et le recalage rappelé sur la même frappe épinglerait la phase.

**Le panneau de contrôle DJ** — listes, formulaires, état — reste un bon candidat Angular
si le DJ en veut un au dossier. Ce serait une application à part, jamais le rendu.

## Façon de travailler

Questions ciblées avant de partir sur une solution. Mesurer avant de corriger, et écrire
la mesure dans le commit. Pas de refactor non demandé, pas de nouvelle dépendance sans le
dire. Commits petits, nommés par fonctionnalité, en français sans accents.

**Ce dépôt est une vitrine.** Le DJ le présentera en entretien comme le projet dont il est
le plus fier. Le README doit donc défendre chaque choix, y compris les échecs — et ne
jamais réclamer un motif d'architecture qu'il n'applique pas.
