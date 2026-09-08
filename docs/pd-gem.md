# Ce que Pure Data et GEM font, et ce qu'on peut leur prendre

Etude du couple **Pd** (Miller Puckette, 1996) et **GEM** (Mark Danks, 1997), qui fait
depuis trente ans ce que ce projet cherche a faire : transformer du son en images, en
direct.

Sources lues : le code de `bonk~` dans `pure-data/extra`, les 22 objets de particules de
`Gem/src/Particles`, et le moteur `papi` qu'ils enveloppent.

---

## L'architecture, comparee a la notre

| | Pd + GEM | Ce projet |
|---|---|---|
| Analyse | objets `~` cadences par blocs de 64 echantillons | `SpectrumAnalyzer`, fenetres de 1024 |
| Passage au visuel | **messages asynchrones**, entre deux blocs audio | `VisualFrame`, puis un anneau partage |
| Rendu | `gemhead` → chaine d'objets → OpenGL, a sa propre cadence | canvas 2D a 60 Hz, CUDA plus tard |
| Processus | **un seul** | deux, separes par de la memoire partagee |

La difference de fond tient en une ligne : **Pd separe le signal du controle, nous
separons l'analyse du rendu.**

Pd fait passer l'audio a taux plein et le controle par messages, dans le meme processus —
un objet d'analyse ecoute le signal et emet un message quand il a quelque chose a dire.
GEM lit ces messages a la frame suivante. Il n'y a ni serialisation, ni transport, ni
frontiere : c'est simple, et c'est ce qui explique qu'un patch fasse en un apres-midi ce
qui prend ici des semaines.

Notre frontiere est plus couteuse et elle achete deux choses : le renderer peut vivre
dans un autre processus, ecrit dans un autre langage, sur le GPU — et un rendu qui rame
ne peut pas faire caler l'analyse.

> **A retenir sans complexe :** un patch Pd ferait une bonne part de ce projet tres vite.
> Ce qu'il ne ferait pas : le tempo — Pd vanilla n'a aucun suivi de tempo — ni la
> separation harmonique/percussive, ni la structure metrique.

---

## `bonk~` : le detecteur d'attaques de Puckette

C'est la piece la plus instructive, et elle est en production depuis 1998. Quatre choix y
different des notres, et **trois sont meilleurs**.

### 1. Un rapport, pas une difference

```c
growth += power / (h->h_mask[oldmaskphase] + 1.0e-15) - 1.;
```

Notre `BandRise` mesure une **difference** entre bandes normalisees. `bonk~` mesure un
**rapport** a la puissance masquee.

La consequence est directe : un rapport est invariant au niveau. Un kick doux dans un
passage doux produit la meme croissance qu'un kick fort dans un passage fort, alors qu'une
difference ne voit que le second. C'est exactement le probleme qu'on a rencontre — un mix
charge en graves qui eteint la detection des claps — et qu'on a traite en normalisant les
bandes. Le rapport le traite a la source.

### 2. Un masque qui suit la crete, pas un ecart minimum

```c
if (!willattack && countup >= masktime) maskpow *= maskdecay;
if (power > maskpow) { maskpow = power; countup = 0; }
```

Par bande, la puissance masquee **monte instantanement** avec le signal, se **maintient**
`masktime` fenetres, puis **decroit** de `maskdecay` par fenetre. Une attaque doit depasser
ce masque.

Notre `OnsetDetector.MinGap` interdit toute detection pendant 20 fenetres, sans nuance :
une frappe forte qui suit de peu une frappe faible est perdue. Le masque, lui, se laisse
depasser par ce qui est plus fort. **Il est musical la ou notre regle est administrative.**

Valeurs de reference : `masktime 4`, `maskdecay 0.7`.

### 3. Deux seuils au lieu d'un

```c
t_float x_hithresh;  /* threshold for total growth to trigger */
t_float x_lothresh;  /* threshold for total growth to re-arm */
```

`hithresh 5` pour declencher, `lothresh 2.5` pour se rearmer. Une hysteresis, quand nous
avons un seuil unique double d'une exigence de maximum local. Les deux visent le meme but
— ne pas declencher deux fois sur la meme attaque — mais l'hysteresis n'exige pas
d'attendre pour juger, la ou notre maximum local coute une fenetre de retard.

### 4. Une fenetre de 256 echantillons, et pas de FFT

`DEFNPOINTS 256`, `DEFPERIOD 128`, `DEFNFILTERS 11` : un banc de 11 filtres appliques
directement au signal, sur une fenetre quatre fois plus courte que la notre.

C'est le compromis de Gabor assume dans l'autre sens : Puckette sacrifie la resolution
frequentielle, dont une detection d'attaque n'a pas besoin, pour la resolution temporelle,
dont elle vit. **Une fenetre de 256 echantillons a 48 kHz, c'est 5 ms au lieu de 21.**

Nous faisons deja ce raisonnement pour l'harmonie, qui tourne sur une fenetre quatre fois
<i>plus longue</i>. Nous ne l'avons jamais fait dans l'autre sens.

### 5. Ce que `bonk~` fait et que nous ne faisons pas du tout

Il **apprend des gabarits** spectraux et classe les attaques : on lui joue un tom, il en
retient l'empreinte, et il annonce ensuite « tom » plutot que « attaque ». C'est
litteralement la demande « reconnaitre le xylophone », resolue en 1998 sans reseau de
neurones — par comparaison a des gabarits appris en quelques frappes.

---

## Le systeme de particules

GEM enveloppe **papi**, la *Particle System API* de David McAllister. Vingt-deux objets,
tous des maillons d'une meme chaine :

| Role | Objets |
|---|---|
| Tete de chaine | `part_head` |
| Emission | `part_source`, `part_vertex` |
| Vitesse initiale | `part_velocity`, `part_velcone`, `part_velsphere` |
| Forces | `part_gravity`, `part_damp`, `part_orbitpoint`, `part_follow`, `part_sink`, `part_move` |
| Mort | `part_killold`, `part_killslow` |
| Apparence | `part_color`, `part_targetcolor`, `part_size`, `part_targetsize` |
| Rendu | `part_draw`, `part_render` |
| Lecture | `part_information` |

Le modele : un **groupe** de N particules est alloue une fois, et chaque objet de la chaine
applique une action a ce groupe, a chaque image. L'etat persiste entre les images ; la
chaine ne fait que le modifier.

Un detail vaut d'etre note :

```cpp
pTimeStep((m_tickTime / 50.f) * m_speed);
```

Le pas de simulation est **derive du temps reel ecoule**, jamais fixe. C'est ce que fait
deja notre `Spring`, et pour la meme raison : une image qui tarde ne doit pas ralentir le
mouvement, elle doit le faire avancer davantage.

### Ce que ce modele vaut pour nous

Notre vocabulaire est fait de **formes discretes** — une source de son, une forme. Un
systeme de particules est l'inverse : un nuage continu dont on ne pilote que les forces.

Les deux ne s'opposent pas, ils repondent a des questions differentes. Une forme dit
*quel instrument joue*. Un nuage dit *dans quel etat est la musique*. La tension d'une
montee, l'ouverture d'un filtre, la densite d'un passage sont des grandeurs continues sans
contour net — exactement ce qu'un halo fait aujourd'hui, en moins riche.

---

## Ce qu'on prend, par ordre de valeur

| # | Emprunt | Pourquoi |
|---|---|---|
| 1 | **Rapport plutot que difference** dans la detection d'attaque | invariant au niveau, traite a la source un probleme qu'on contourne |
| 2 | **Masque a maintien et decroissance** au lieu de `MinGap` | une frappe forte qui suit une faible cesse d'etre perdue |
| 3 | **Fenetre courte pour les attaques**, longue pour l'harmonie | 5 ms au lieu de 21 sur le seul etage ou le retard se voit |
| 4 | **Hysteresis** a deux seuils | supprime le cout en retard du maximum local |
| 5 | Gabarits appris pour classer les frappes | la reponse historique a « reconnaitre le xylophone » |
| 6 | Particules pour le continu | ce que les formes discretes ne savent pas dire |

Les trois premiers sont a mesurer avec `Emotion.Probe` avant d'etre adoptes — la lecon de
la journee etant qu'une bonne idee non mesuree se retourne une fois sur deux.
