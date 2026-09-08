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
| tempo publié sur 4 % des fenêtres | vote sur les écarts entre attaques consécutives, par cases de 1,4 % | autocorrélation de l'enveloppe |
| intervalle de mesure à 149 ms | la grille recalculait sa position depuis une origine lointaine : changer la période faisait sauter le rang de seize temps | la phase s'accumule, elle ne se recalcule pas |
| 0,90 de confiance sur du bruit blanc | confiance mesurée sur la forme de la courbe, dont la moitié vaut zéro par troncature | sur la hauteur de la corrélation, qui est absolue |
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

Selim donne un morceau de Macroblank à **87 BPM**. Le système en annonçait **108,4**.
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
| Macroblank, 87 BPM (Selim) | 108,4 | **87,6** |
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

**Sur le set de Selim, la confiance est nulle.** Meilleur score 0,002. Le suivi dit qu'il
ne sait pas, ce qui est la bonne réponse — mais ça reste un résultat négatif :

> Sur le master, **deux morceaux se superposent**. La signature d'une mesure y mélange ce
> qui sort et ce qui entre, et aucune phrase ne peut se corréler avec elle-même. C'est
> l'argument de Selim pour le cue : au casque le morceau est **seul**, et c'est là que sa
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
| `Emotion.Server` | hub, endpoints, rendu servi en statique | ASP.NET Core, SignalR |
| `Emotion.Signal.Tests` | 108 tests | xUnit |
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

`/ready` donne l'état de préparation du disque en cours ; le feu vert part aussi une fois
par le hub, vers le téléphone. Voir `docs/systeme.md`.

`http://localhost:5099` · `S` cycle visuel / superposé / signaux · `D` diagnostic ·
`H` masque · `F` plein écran · `/health` pour l'état des tuyaux.

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

| Source | Rendu | Couleur |
|---|---|---|
| basse | halo qui grandit et rétrécit, au centre | vert |
| voix | **bouche en caractères** qui s'ouvre et se courbe | cyan |
| piano | octogone en rotation lente | violet |
| aiguës | colonne de blocs, pointe triangulaire au sommet | jaune |
| charleys | grain de caractères qui scintillent | gris |
| kick | traits qui filent du centre vers les bords | **blanc** |
| claps | les deux extrémités de la bande s'allument | violet |

**Le rendu est en caractères** parce qu'il doit rester léger — il n'y a pas de GPU sous la
main, et un remplissage de texte coûte une fraction d'un dégradé. Ils donnent en prime une
identité que des polygones translucides n'avaient pas : celle d'un terminal, ce qui va bien
à un projet qui passe son temps à mesurer.

**Une voix ne se déplace pas, elle s'ouvre.** La bande qui montait et descendait
« rebondissait comme une balle de basket ». Le contour mélodique commande donc la
**courbure** des lèvres — relevées dans l'aigu, retombantes dans le grave — et non plus une
position.

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
