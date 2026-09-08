// Ce que le retroprojecteur affiche.
//
// LA REGLE : une source de son, une forme. Toujours la meme.
//
//   basse       cercle plein au centre, qui respire
//   voix        anneaux concentriques, autant de cotes que le Camelot
//   xylophone   triangles disperses en haut
//   kick        onde circulaire qui part du centre
//   clap        losanges sur les cotes
//   charley     traits courts en bas
//   nouveaute   barre qui traverse
//
// Sans cette discipline, l'oeil n'apprend rien et le visuel redevient une bouillie qui
// reagit vaguement a la musique. Avec elle, on reconnait un instrument a sa forme, et
// c'est tout l'interet : le mur raconte ce qui se joue.
//
// Le caractere vient de la base — la famille donne la palette, le Camelot le nombre de
// cotes — et le mouvement vient du son. Aucun tempo n'est jamais lu dans une fiche.

import { ClipLibrary } from './clips.js';
import { Diagnostics } from './diag.js';
import { Signals } from './signals.js';
import { Calibrate } from './calibrate.js';
import { Spring, Lue, Pulse, FrameLerp } from './motion.js';
import { BeatClock } from './beatclock.js';
import * as S from './shapes.js';

const TAU = Math.PI * 2;

// LA PALETTE DES SOURCES, ET ELLE EST FIXE.
//
// Chaque source garde sa couleur d'un disque a l'autre : c'est ce qui permet d'apprendre
// a lire la scene. Si le kick changeait de teinte a chaque morceau, l'oeil devrait tout
// reapprendre a chaque transition, et le vocabulaire ne servirait plus a rien.
//
// La couleur du disque, elle, n'a pas disparu — elle teinte la <b>trame de fond et
// l'horizon</b>. L'identite du morceau devient l'ambiance, les sources gardent leur nom.
// Pendant un fondu, la trame passe d'une couleur a l'autre : le mix se voit sans que la
// lecture se brouille.
//
// Aucun rouge nulle part.
// UNE CASE PAR SOURCE, ET SIX CASES POUR SIX REGISTRES.
//
// Empiler les formes sur un axe unique les faisait se recouvrir quoi qu'on fasse, et six
// lignes dans une meme case ne se distinguent pas davantage : ce qu'on gagne en
// separation d'analyse se reperd au rendu. Chaque source a donc sa case, sa couleur et
// son caractere — trois separations valent mieux qu'une.
//
// RIEN N'EST NOMME PAR SON INSTRUMENT. Les cases s'appelaient PIANO, VOIX, AIGUES ; c'est
// une faute, parce que ce qui joue dans une bande change d'un disque a l'autre et
// qu'annoncer un piano la ou passe un saxophone est pire que ne rien annoncer. Une bande
// porte son numero, et c'est tout ce qu'on en sait.
//
//   ┌─────┬─────┬─────┐
//   │  1  │  2  │  3  │   six octaves, de 100 Hz a 6,4 kHz
//   ├─────┼─────┼─────┤
//   │  4  │  5  │  6  │
//   ├─────┼─────┼─────┤
//   │GRAVE│GRAIN│SPEC │
//   ├─────┴─────┴─────┤
//   │     FRAPPES     │
//   └─────────────────┘
const SRC = {
  kick:  { r: 244, g: 246, b: 244 },
  grave: { r: 53,  g: 208, b: 127 },
  hat:   { r: 150, g: 158, b: 155 },
  spec:  { r: 130, g: 138, b: 135 },
  cadre: { r: 110, g: 118, b: 115 },
};

// Six teintes distinctes, aucune rouge, chacune avec son caractere de remplissage.
const REG = [
  { c: { r: 53,  g: 208, b: 127 }, ch: '█' },
  { c: { r: 77,  g: 217, b: 232 }, ch: '▓' },
  { c: { r: 107, g: 168, b: 245 }, ch: '▒' },
  { c: { r: 167, g: 139, b: 232 }, ch: '▚' },
  { c: { r: 212, g: 139, b: 212 }, ch: '▞' },
  { c: { r: 232, g: 200, b: 77  }, ch: '░' },
];

// La premiere rangee commence sous le bandeau de reglage, qui occupe le haut de l'ecran.
// A 3 % elle passait dessous et ses trois titres — donc les trois jauges de maturite —
// etaient invisibles : la case avait l'air anonyme alors qu'elle ne l'etait pas.
const CASE = {
  r0: { x: 0.02,  y: 0.075, w: 0.31, h: 0.245, nom: '1' },
  r1: { x: 0.345, y: 0.075, w: 0.31, h: 0.245, nom: '2' },
  r2: { x: 0.67,  y: 0.075, w: 0.31, h: 0.245, nom: '3' },
  r3: { x: 0.02,  y: 0.335, w: 0.31, h: 0.245, nom: '4' },
  r4: { x: 0.345, y: 0.335, w: 0.31, h: 0.245, nom: '5' },
  r5: { x: 0.67,  y: 0.335, w: 0.31, h: 0.245, nom: '6' },
  grave: { x: 0.02,  y: 0.595, w: 0.31, h: 0.20, nom: 'GRAVE' },
  grain: { x: 0.345, y: 0.595, w: 0.31, h: 0.20, nom: 'GRAIN' },
  spec:  { x: 0.67,  y: 0.595, w: 0.31, h: 0.20, nom: 'SPECTRE' },
  bas:   { x: 0.02,  y: 0.815, w: 0.96, h: 0.125, nom: 'FRAPPES' },
};

const boite = (c, w, h) => ({
  x: c.x * w, y: c.y * h, w: c.w * w, h: c.h * h,
  cx: (c.x + c.w / 2) * w, cy: (c.y + c.h / 2) * h,
  u: Math.min(c.w * w, c.h * h), nom: c.nom,
});

// LE RENDU EST EN CARACTERES, ET C'EST UNE DECISION DE MESURE AUTANT QUE DE STYLE.
//
// Tant qu'il emploie des degrades et des arcs, un retard percu peut toujours venir de lui.
// Reduit a du texte monospace — une chaine par ligne, un remplissage par chaine — il
// devient trop rapide pour etre suspect, et ce qui reste se mesure ailleurs.
//
// C'est aussi ce qui prepare l'unite CUDA : un GPU qui affiche une grille de caracteres
// n'a rien a reimplementer. Il lit une matrice et l'affiche.
const BLOCS = [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
const bloc = (v) => BLOCS[Math.max(0, Math.min(8, Math.round(v * 8)))];

// SIX FORMES DISTINCTES, ET NON SIX FOIS LA MEME.
//
// Les six registres partageaient un seul dessin — une colonne qui montait — avec pour
// seule difference leur couleur et leur caractere de remplissage. Six colonnes identiques
// ne se comparent pas : l'oeil doit lire une legende pour savoir laquelle il regarde,
// alors que six silhouettes se reconnaissent sans y penser. Selim l'avait dit sur la
// premiere version : « le delire pour les levres en ascii c'etait super, cercle pour la
// basse c'etait super aussi ».
//
// TOUT EST EN COORDONNEES NORMALISEES, ET C'EST CE QUI EMPECHE LE DEBORDEMENT.
//
// Chaque forme travaille dans un carre de -1 a 1, quelles que soient les dimensions
// reelles de la grille. Une forme de rayon r y tient par construction tant que r ne
// depasse pas 1, et le contour melodique ne deplace le centre que de ce qui reste libre.
// Les debordements repetes des cases 4, 5 et 6 venaient tous de dessins calcules en
// cellules, ou une meme constante donnait un motif tenu dans une case et deborde dans une
// autre.
const FORMES = ['anneau', 'onde', 'levres', 'losange', 'etoile', 'grain'];

// Ce que chaque forme occupe verticalement, en fraction de la demi-hauteur. Sert a lui
// reserver sa place avant de la deplacer : couper un motif qui deborde le mutile, lui
// laisser sa place le garde entier.
const EXTENSION = {
  anneau: 0.62, onde: 0.42, levres: 0.55, losange: 0.62, etoile: 0.66, grain: 1.0,
};

// Le caractere d'une forme a une distance donnee de son trait : plein, puis estompe.
const trait = (d, e, plein, pale) => d < e ? plein : (d < e * 2.2 ? pale : null);

// La grille d'une case retranche la place du titre et une marge basse, une bonne fois.
// C'est ce qui faisait deborder le grain, dont les lignes etaient comptees sur la hauteur
// entiere puis dessinees sous la legende.
function grille(b, cellules) {
  const cw = b.w / cellules, ch = cw * 1.9;
  const titre = Math.max(ch, b.h * 0.16), dispo = b.h - titre - ch * 0.4;
  return {
    cols: cellules, lignes: Math.max(1, Math.floor(dispo / ch)),
    cw, ch, x0: b.x + cw * 0.5, y0: b.y + titre,
  };
}

// ON DEMANDE DES LIGNES, PAS DES COLONNES.
//
// Le nombre de lignes n'est pas libre : une cellule de caractere est 1,9 fois plus haute
// que large, donc c'est le nombre de <b>colonnes</b> qui decide combien de lignes tiennent
// dans une case. Les choisir a la main revient a deviner, et la devinette est tombee a
// cote : 22 colonnes dans la case du grave ne laissaient qu'une seule ligne, et l'anneau
// n'avait plus nulle part ou exister — la case restait vide sans que rien ne le signale.
//
// Une forme a besoin d'a peu pres neuf lignes pour se lire. On part donc de la, et l'on en
// deduit les colonnes.
function grillePour(b, lignesVoulues) {
  const titre = b.h * 0.16;
  const ch = Math.max(4, (b.h - titre) / (lignesVoulues + 0.4));
  return grille(b, Math.max(6, Math.round(b.w / (ch / 1.9))));
}

export class Visual {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false });

    // Ce qui joue, et ce qui se prepare.
    this.color = { r: 110, g: 110, b: 110 };
    this.sides = 6;
    this.kindName = 'Rest';
    this.intensity = 0;
    this.next = null;
    this.blend = 0;

    // Interpolation entre images d'analyse : le signal arrive a 47 Hz, l'ecran dessine
    // a 60. Sans elle, une image de rendu sur cinq repete la precedente et l'on voit
    // des paliers de 21 ms — c'est ce qui donnait l'impression de saccade.
    this.lerp = new FrameLerp();
    this.bandBuf = new Float32Array(12);

    // CE QUI A UNE MASSE EST AMORTI PAR L'ANALYSE, PLUS PAR LE RENDU.
    //
    // Ces valeurs traversaient ici des ressorts. Ils ont ete descendus dans
    // `Emotion.Signal.Damper`, avec les memes raideurs : le mouvement est identique, mais
    // l'unite CUDA n'aura pas a les reimplementer — et la raideur ne vit plus qu'a un
    // seul endroit. Garder les deux amortirait deux fois et rendrait tout mou.
    //
    // Le renderer ne calcule donc plus que les impulsions, qui doivent rester brutes :
    // une impulsion lissee en amont ne serait plus une impulsion.
    this.bass = new Lue();
    this.voice = new Lue();
    this.bells = new Lue();
    this.level = new Lue();
    this.tonal = new Spring(6);        // la tonalite n'est pas amortie en amont

    // OU JOUE CHAQUE REGISTRE, ET NON COMBIEN.
    //
    // Une amplitude ne decrit aucun mouvement : quand une melodie monte, le medium baisse
    // et l'aigu monte, et le visuel n'en montre qu'un frisson. Une position, elle, se
    // deplace — et une forme peut la suivre.
    //
    // Ressorts souples : c'est un contour melodique, pas une attaque. Il doit glisser.
    this.lowPitch = new Lue(0.5);
    this.midPitch = new Lue(0.5);
    this.highPitch = new Lue(0.5);

    // Six registres suivis a part. Trois confondaient un piano et un saxophone de la meme
    // octave : ils devenaient une seule grandeur, donc une seule forme, et tout ce que
    // l'oreille distingue entre eux disparaissait.
    this.regNiveau = Array.from({ length: 6 }, () => new Lue());
    this.regPos = Array.from({ length: 6 }, () => new Lue(0.5));
    this.regCoup = Array.from({ length: 6 }, () => new Pulse(0.5));

    // CE QUE CHAQUE SOURCE SAIT D'ELLE-MEME, ET QUI ARRIVE PLUS TARD QUE LE RESTE.
    //
    // Le niveau et le contour sont justes des la premiere image ; l'empreinte, elle, se
    // forme sur quelques secondes de jeu effectif — plus longtemps encore pour une source
    // qui n'entre qu'au refrain. Chaque case avance donc a son rythme, et le mur montre
    // cette progression au lieu de tout afficher avec le meme aplomb.
    //
    // Sur un morceau du crate, les six murissent a 4,5 s, 7,0 s, 7,2 s et 15,8 s ; les
    // deux registres graves restent inconnus, partages entre le kick et la basse. Une
    // bande partagee qui refuse de se laisser nommer est le bon resultat, pas une panne.
    this.regSur = Array.from({ length: 6 }, () => new Lue());
    this.regNom = new Array(6).fill(0);

    // LE TEMPO ANNONCE, ET L'ECART A CE QU'ON CROYAIT SAVOIR.
    //
    // Quand un disque a ete cale au casque, son tempo est deja connu et n'a pas a etre
    // recalcule. Mais un vinyle derive : le plateau bouge, le pitch glisse sous les
    // doigts. Un seul BPM d'ecart deplace la grille d'un sixieme de temps en seize temps
    // et d'un temps entier en une minute — c'est invisible en chiffres et flagrant a
    // l'ecran, donc c'est a l'ecran qu'il faut le montrer.
    this.annonce = new Pulse(4);       // le ping tient quatre temps
    this.annonceBpm = 0;
    this.derive = new Lue();

    // LA STRUCTURE NE PORTE PAS DE FORME A ELLE, ELLE GOUVERNE LES AUTRES.
    //
    // La regle du fichier est « une source de son, une forme ». Or la mesure et la phrase
    // ne sont pas des sources : ce sont du temps. Leur donner un dessin propre reviendrait
    // a poser une interface par-dessus le visuel — une jauge, un compteur — et personne
    // ne regarde une jauge pendant un set. Elles pilotent donc le <b>comportement</b> des
    // formes existantes : leur amplitude, leur vitesse, leur accent.
    this.tension = new Lue();          // ArcDetector la lisse deja, sur huit mesures
    this.drop = new Pulse(3);          // la rupture tient trois temps
    this.onOne = 0.55;                 // accent du temps fort, applique a l'onde du kick

    // Le timbre : la couleur du son, pas ses evenements. Un filtre passe-bas qu'on
    // ferme sur huit mesures ne change ni le tempo, ni les attaques, ni les notes — le
    // visuel restait donc impassible pendant le geste le plus visible d'un set.
    //
    // Raideur basse : ces grandeurs bougent au rythme de la main du DJ, pas de la
    // musique. Une reaction vive les ferait trembler.
    this.open = new Lue(1);            // ouverture du filtre, grande ouverte au demarrage
    this.bright = new Lue(0.5);
    this.density = new Lue(0.5);

    // Impulsions, dont la duree de vie est une fraction du temps musical et non une
    // constante : sinon un effet meurt en 120 ms quel que soit le morceau, et l'ecran
    // reste eteint les quatre cinquiemes d'un temps a 87 BPM.
    this.kick = new Pulse(0.60);
    this.clap = new Pulse(0.45);
    this.hat = new Pulse(0.20);
    this.bassHit = new Pulse(1.10);    // une note de basse resonne longtemps
    this.voiceHit = new Pulse(0.90);
    this.bellHit = new Pulse(0.55);

    this.spin = 0;
    this.sweep = -1;
    this.lastAt = performance.now();

    // L'horloge de battement : elle ne remplace pas la detection, elle la devance. Toute
    // la chaine ajoute du retard — fenetre, separation, sommet, interpolation, affichage,
    // ecran — et aller plus vite a chaque etage ne suffit pas. Un morceau a un tempo :
    // le prochain temps est previsible, donc on le declenche quand il tombe plutot
    // qu'apres l'avoir constate.
    this.clock = new BeatClock();

    // Avance volontaire, pour compenser ce qui reste en aval de l'analyse : la boucle
    // d'affichage et la dalle, une trentaine de millisecondes. Le visuel part alors un
    // cheveu avant le son, ce que l'oreille pardonne bien mieux que l'inverse.
    this.leadMs = 30;

    this.clips = new ClipLibrary();
    this.clips.load();

    this.diag = new Diagnostics();
    this.signals = new Signals();
    this.calibrate = new Calibrate();
    this.latencyMs = null;

    this.resize();
    addEventListener('resize', () => this.resize());
  }

  resize() {
    // Le rapport de pixels compte : un bord flou projete devient une bouillie.
    const dpr = Math.min(devicePixelRatio || 1, 2);
    this.canvas.width = Math.floor(innerWidth * dpr);
    this.canvas.height = Math.floor(innerHeight * dpr);
    this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    this.w = innerWidth;
    this.h = innerHeight;
  }

  // La face calee au casque. On la garde de cote sans rien changer a l'ecran : tant que
  // le fader est ferme, le public ne doit rien voir venir.
  setCued(t) {
    this.next = t ? {
      color: hexToRgb(t.colorHex) ?? { r: 110, g: 110, b: 110 },
      sides: sidesOf(t),
      kind: t.scene?.kind ?? 'Rest',
      intensity: t.scene?.intensity ?? 0,
    } : null;
  }

  setTrack(t) {
    this.color = hexToRgb(t.colorHex) ?? { r: 110, g: 110, b: 110 };
    this.sides = sidesOf(t);
    this.kindName = t.scene?.kind ?? 'Rest';
    this.intensity = t.scene?.intensity ?? 0;
  }

  draw(frame) {
    const { ctx, w, h } = this;
    const now = performance.now();

    // Borne sur le pas de temps : un onglet revenu d'arriere-plan rendrait un saut
    // enorme, et les ressorts partiraient en oscillation.
    const dtMs = Math.min(64, now - this.lastAt);
    const dt = dtMs / 1000;
    this.lastAt = now;

    this.lerp.push(frame, now);

    const bands = this.lerp.bands(this.bandBuf, now);
    const rms = this.lerp.scalar(f => f.rms, now);
    const bpm = frame.bpm ?? 90;
    const beatMs = 60000 / bpm;

    const v = frame.voices ?? {};
    const hit = frame.hits ?? {};

    // ---- l'horloge, avant les impulsions ----
    // Elle avance de l'ecart reel plus l'avance voulue, et se recale sur chaque kick
    // detecte sans jamais s'y aligner d'un coup.
    this.clock.step(dtMs + this.leadMs * 0.02, frame.bpm, this.leadMs);
    if (hit.kick) this.clock.sync();

    // ---- impulsions ----
    // Le kick part de la grille quand elle est verrouillee, de la detection sinon. C'est
    // le seul evenement periodique : un clap irregulier, une voix, un break n'ont pas de
    // grille et doivent rester reactifs. Predire l'imprevisible inventerait des
    // evenements, ce qui est pire qu'un visuel en retard.
    if (this.clock.locked ? this.clock.justFired : hit.kick) this.kick.fire();
    if (hit.clap) { this.clap.fire(); this.clips.onOnset(this.kindName, this.intensity); }
    if (hit.hat) this.hat.fire();
    if (v.lowHit) this.bassHit.fire();
    if (v.midHit) this.voiceHit.fire();
    if (v.highHit) this.bellHit.fire();
    if (frame.noveltyOnset) this.sweep = 0;

    // Les six registres : une attaque par bit, un niveau et un contour par bande.
    const hits = v.hits | 0;
    for (let r = 0; r < 6; r++) {
      if ((hits >> r) & 1) this.regCoup[r].fire();
      this.regNiveau[r].step(this.lerp.scalar(f => f.voices?.levels?.[r] ?? 0, now));
      this.regPos[r].step(this.lerp.scalar(f => f.voices?.pitches?.[r] ?? 0.5, now));

      // La maturite ne s'interpole pas : elle monte deja lentement en amont, et lui
      // ajouter un lissage ne ferait que retarder ce qu'elle annonce.
      this.regSur[r].step(v.lanes?.[r]?.confidence ?? 0);
      this.regNom[r] = v.labels?.[r] ?? 0;
    }

    // L'annonce de tempo : « on est a 87,9 », puis « 88,1 ». Elle ne dure qu'une image
    // cote analyse, on la tient quatre temps a l'ecran pour qu'elle soit lisible.
    if (frame.tempoAnnounce) {
      this.annonce.fire();
      this.annonceBpm = frame.announcedBpm ?? 0;
    }
    this.annonce.step(beatMs, dtMs);
    this.derive.step(frame.driftVisible ?? 0);

    // La structure. Elle est la seule chose de tout le systeme qu'on lise en avance :
    // partout ailleurs on constate un evenement et l'on court apres, ici la tension monte
    // pendant huit mesures et dit ou va le morceau. Une rupture peut donc etre jouee
    // <i>sur</i> l'instant plutot qu'apres, et la latence de la chaine cesse de compter.
    const st = frame.structure ?? {};
    this.tension.step(this.lerp.scalar(f => f.structure?.buildup ?? 0, now));
    if (st.drop) this.drop.fire();
    this.drop.step(beatMs, dtMs);

    // Le premier temps de la mesure porte un accent plus large. C'est le seul endroit ou
    // le rang du temps se voit directement — et il ne se voit que si l'on sait ou il est :
    // beat vaut -1 tant que la grille n'a pas tranche, et l'on reste alors neutre plutot
    // que d'accentuer un temps au hasard.
    if (this.clock.locked ? this.clock.justFired : hit.kick)
      this.onOne = st.beat === 0 ? 1 : (st.beat > 0 ? 0.5 : 0.62);

    for (const p of [this.kick, this.clap, this.hat,
                     this.bassHit, this.voiceHit, this.bellHit, ...this.regCoup])
      p.step(beatMs, dtMs);

    // ---- valeurs continues : AMORTIES EN AMONT, INTERPOLEES ICI ----
    //
    // Ces deux operations sont distinctes et il faut les deux. L'amortissement, descendu
    // dans l'analyse, donne au mouvement sa masse. L'interpolation comble les trous entre
    // deux images d'analyse — et sans elle, une valeur reste figee une image de rendu sur
    // cinq, ce qui se voit comme un tremblement.
    //
    // Le ressort cote rendu masquait ces paliers par accident : en le retirant, la
    // saccade est apparue. Ce n'etait pas le lissage qui manquait, c'etait
    // l'interpolation qui n'avait jamais ete branchee sur ces grandeurs-la.
    //
    // On n'extrapole jamais au-dela de la derniere mesure : inventer du mouvement absent
    // du son se verrait au premier silence.
    const lu = (pick, defaut) => {
      const x = this.lerp.scalar(pick, now);
      return Number.isFinite(x) ? x : defaut;
    };

    this.bass.step(lu(f => f.voices?.low ?? 0, 0));
    this.voice.step(lu(f => f.voices?.mid ?? 0, 0));
    this.bells.step(lu(f => f.voices?.high ?? 0, 0));
    this.level.step(rms);
    this.tonal.step(frame.harmony?.tonality ?? 0, dt);

    this.open.step(lu(f => f.timbre?.openness ?? 1, 1));
    this.bright.step(lu(f => f.timbre?.centroid ?? 0.5, 0.5));
    this.density.step(lu(f => f.timbre?.density ?? 0.5, 0.5));

    this.lowPitch.step(lu(f => f.voices?.lowPitch ?? 0.5, 0.5));
    this.midPitch.step(lu(f => f.voices?.midPitch ?? 0.5, 0.5));
    this.highPitch.step(lu(f => f.voices?.highPitch ?? 0.5, 0.5));

    this.spin += dt * (0.06 + this.level.value * 0.22 + this.tension.value * 0.55);
    if (this.sweep >= 0) {
      this.sweep += dtMs / (beatMs * 4);
      if (this.sweep > 1.3) this.sweep = -1;
    }

    // ---- la transition, mesuree et non commandee ----
    this.blend = frame.blend ?? 0;
    const c = this.next ? S.mix(this.color, this.next.color, this.blend) : this.color;
    const sides = (this.next && this.blend > 0.5) ? this.next.sides : this.sides;

    // ---- fond ----
    // Jamais un noir pur : il fait ressortir la trame du videoprojecteur.
    ctx.fillStyle = `rgb(${c.r * 0.05 | 0}, ${c.g * 0.05 | 0}, ${c.b * 0.06 | 0})`;
    ctx.fillRect(0, 0, w, h);

    const open = this.open.value;
    const coupe = Math.pow(open, 1.5);

    ctx.save();
    // Le filtre contracte et trouble la scene entiere sans deplacer les cases les unes
    // par rapport aux autres : elles restent lisibles meme fermees.
    const swell = 1 + this.tension.value * 0.08 + this.drop.value * 0.08;
    const shrink = (0.90 + open * 0.10) * swell;
    ctx.translate(w / 2, h / 2); ctx.scale(shrink, shrink); ctx.translate(-w / 2, -h / 2);

    this.drawTension(ctx, w, h);
    this.drawCadres(ctx, w, h, c);
    for (let r = 0; r < 6; r++) this.drawRegistre(ctx, boite(CASE['r' + r], w, h), r);
    this.drawGrave(ctx, boite(CASE.grave, w, h));
    this.drawGrain(ctx, boite(CASE.grain, w, h), coupe);
    this.drawSpectre(ctx, boite(CASE.spec, w, h), bands);
    this.drawFrappes(ctx, boite(CASE.bas, w, h));
    this.drawSweep(ctx, w, h, c);
    ctx.restore();

    this.clips.draw(ctx, w, h, this.level.value);

    // ---- ecrans de reglage, par-dessus tout ----
    this.diag.push(frame);
    this.diag.draw(ctx, w, h, frame);
    this.signals.push(frame);
    this.signals.draw(ctx, w, h, { ...frame, sceneName: this.kindName });
    this.calibrate.draw(ctx, w, h, frame, this.latencyMs);
  }

  // ------------------------------------------------------------- TENSION
  // Une lueur qui monte du bas de l'ecran. Elle n'obeit a aucun evenement : elle annonce.
  drawTension(ctx, w, h) {
    const t = this.tension.value, d = this.drop.value;
    if (t < 0.02 && d < 0.02) return;

    const g = ctx.createLinearGradient(0, h, 0, h * 0.4);
    g.addColorStop(0, S.rgba(SRC.grave, t * 0.20 + d * 0.26));
    g.addColorStop(1, S.rgba(SRC.grave, 0));
    ctx.fillStyle = g;
    ctx.fillRect(0, h * 0.4, w, h * 0.6);
  }

  // -------------------------------------------------------------- CADRES
  // Le bord de chaque case et son nom. Sans bord, des formes eparses paraissent flotter
  // au hasard plutot qu'occuper des places ; et sans nom, on ne sait pas ce qu'on regarde.
  drawCadres(ctx, w, h, c) {
    for (const k of Object.keys(CASE)) {
      const b = boite(CASE[k], w, h);
      const teinte = (k[0] === 'r' && k.length === 2) ? REG[+k[1]].c : c;
      ctx.strokeStyle = S.rgba(teinte, 0.20 + this.level.value * 0.10);
      ctx.lineWidth = 1;
      ctx.strokeRect(Math.round(b.x) + 0.5, Math.round(b.y) + 0.5,
                     Math.round(b.w), Math.round(b.h));
      this.police(ctx, Math.max(8, Math.min(13, b.w * 0.075)));
      ctx.fillStyle = S.rgba(teinte, 0.80);

      // LE TITRE D'UNE CASE DE REGISTRE PORTE SA MATURITE.
      //
      // Quatre crans qui se remplissent a mesure que la source se tient. Tant qu'ils sont
      // vides, ce qui joue dans cette bande n'est pas identifiable — souvent parce que
      // plusieurs instruments s'y relaient — et aucun nom ne doit y etre pose. Pleins, la
      // source est assez stable pour en porter un.
      //
      // AUCUN NOM N'EST INVENTE ICI. La case affiche un numero, et le nom seulement si la
      // fiche en a pose un : ce qui joue dans une bande change d'un disque a l'autre, et
      // annoncer un piano la ou passe un saxophone est pire que ne rien annoncer.
      if (k[0] === 'r' && k.length === 2) {
        const r = +k[1], sur = this.regSur[r].value;
        const crans = Math.round(sur * 4);
        let jauge = '';
        for (let i = 0; i < 4; i++) jauge += i < crans ? '▮' : '▯';
        const nom = this.regNom[r] ? `${b.nom}·${this.regNom[r]}` : b.nom;
        ctx.fillText(`${nom} ${jauge}`, b.x + 5, b.y + 4);
      } else {
        ctx.fillText(b.nom, b.x + 5, b.y + 4);
      }
    }
  }

  police(ctx, taille) {
    ctx.font = `${Math.max(6, taille).toFixed(0)}px ui-monospace, "JetBrains Mono", Menlo, monospace`;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
  }

  // ------------------------------------------------------------ REGISTRE
  // Une octave, dans sa case, avec sa couleur, son caractere et SA FORME.
  //
  // Le niveau donne la taille, le contour melodique deplace la forme de bas en haut — une
  // melodie qui monte fait monter le motif — et une attaque l'epaissit brievement.
  //
  // LA CONFIANCE GOUVERNE LA NETTETE, ET C'EST NOUVEAU.
  //
  // Une source dont l'empreinte n'est pas formee reste volontairement floue : trait
  // estompe, contour pointille. A mesure qu'elle se tient — quelques secondes pour une
  // basse, une minute pour un instrument qui n'entre qu'au refrain — son dessin se
  // resserre. Le mur montre ainsi ce que le systeme est en train d'apprendre, au lieu
  // d'afficher tout avec le meme aplomb qu'il sache ou non de quoi il parle.
  drawRegistre(ctx, b, r) {
    const niv = this.regNiveau[r].value;
    const pos = this.regPos[r].value;
    const frappe = this.regCoup[r].value;
    if (niv < 0.015 && frappe < 0.02) return;

    // QUARANTE-QUATRE COLONNES, ET LE CHIFFRE VIENT D'UN CALCUL.
    //
    // Le nombre de lignes n'est pas libre : une cellule fait 1,9 fois sa largeur, donc
    // moins de colonnes veut dire des cellules plus hautes et donc moins de lignes. A 34
    // colonnes la case n'en tenait que six, ce qui suffit a une barre mais pas a
    // distinguer un anneau d'un losange. A 44, elle en tient neuf — assez pour qu'une
    // silhouette se lise, sans descendre sous une police projetable.
    const G = grillePour(b, 9);
    this.police(ctx, G.cw * 1.5);

    const nom = FORMES[r];
    const sur = this.regSur[r].value;              // maturite de l'empreinte, 0 a 1
    const force = Math.min(1, niv + frappe * 0.35);

    // La forme reserve d'abord son extension, puis le contour n'utilise que ce qui reste.
    // L'ordre compte : le calculer dans l'autre sens ferait sortir la forme de sa case,
    // et la rogner ensuite la couperait en deux.
    const rayon = Math.min(0.92, (0.22 + force * 0.72) * EXTENSION[nom]);
    const libre = Math.max(0, 1 - rayon);
    const cy = (0.5 - pos) * 2 * libre;

    // L'ASPECT, ET C'EST CE QUI DECIDE SI UNE FORME EST RECONNAISSABLE.
    //
    // Une case fait deux fois plus large que haute, et sa grille davantage encore : une
    // cellule de caractere est 1,9 fois plus haute que large. Des coordonnees normalisees
    // par le nombre de cellules ecrasent donc tout — un anneau, un losange et une etoile
    // deviennent la meme bande horizontale, ce qui annule exactement ce qu'on cherchait en
    // leur donnant six silhouettes.
    //
    // On mesure donc en pixels, rapportes a la plus petite demi-dimension : les formes
    // sont rondes, occupent la hauteur disponible et restent etroites au milieu de leur
    // case. C'est aussi ce qui garantit qu'elles ne debordent jamais, puisque l'unite est
    // la dimension contraignante.
    const cxg = (G.cols - 1) / 2, cyg = (G.lignes - 1) / 2;
    const unite = Math.max(1, Math.min(cxg * G.cw, cyg * G.ch));
    const parCol = G.cw / unite, parLigne = G.ch / unite;
    const plein = REG[r].ch;

    // Un trait franc quand la source est connue, estompe tant qu'elle ne l'est pas.
    const pale = sur > 0.6 ? '·' : '˙';
    const epais = 0.16 + frappe * 0.22 + sur * 0.10;

    ctx.fillStyle = S.rgba(REG[r].c, 0.14 + niv * 0.55 + frappe * 0.3 + sur * 0.16);

    for (let l = 0; l < G.lignes; l++) {
      const ny = (l - cyg) * parLigne - cy;
      let ligne = '';

      for (let col = 0; col < G.cols; col++) {
        const nx = (col - cxg) * parCol;
        ligne += this.pixel(nom, nx, ny, rayon, force, epais, plein, pale, col, l,
                            parLigne) ?? ' ';
      }

      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // Le caractere a poser en un point, selon la forme. Une seule fonction pour les six :
  // ce qui les distingue tient dans la geometrie, pas dans six boucles de rendu.
  pixel(nom, nx, ny, r, force, e, plein, pale, col, l, parLigne) {
    const d = Math.sqrt(nx * nx + ny * ny);

    switch (nom) {
      // Un anneau qui respire. La forme la plus lisible du lot, et la premiere que Selim
      // ait validee.
      case 'anneau':
        return trait(Math.abs(d - r), e, plein, pale);

      // Une onde qui traverse la case, dont l'amplitude suit le niveau. Elle dit un
      // mouvement continu la ou un cercle dit une masse.
      case 'onde': {
        const y = Math.sin(nx * 2.2 + this.spin * 2.2) * r;

        // UNE ONDE SUR NEUF LIGNES SE CASSE SI ON LA TESTE COMME UN TRAIT.
        //
        // La ou la sinusoide est raide, elle traverse une ligne entiere entre deux
        // colonnes : un test de distance verticale ne l'attrape nulle part, et l'onde
        // arrive a l'ecran en morceaux epars. Le trait ne descend donc jamais sous une
        // demi-ligne — il reste fin, mais continu.
        const e2 = Math.max(e * 0.7, parLigne * 0.55);
        return trait(Math.abs(ny - y), e2, plein, pale);
      }

      // Deux levres qui s'ouvrent avec le niveau et se ferment avec lui. C'est la forme
      // qui rend une voix reconnaissable : une bouche s'ouvre, elle ne clignote pas.
      case 'levres': {
        if (Math.abs(nx) > r * 1.35) return null;
        const arc = Math.sqrt(Math.max(0, 1 - (nx / (r * 1.35)) ** 2));
        const ouverture = arc * r * (0.15 + force * 0.85);
        const haut = Math.abs(ny - ouverture), bas = Math.abs(ny + ouverture);
        const c = trait(Math.min(haut, bas), e * 1.4, plein, pale);
        if (c) return c;
        // L'interieur s'assombrit quand la bouche est grande ouverte : sans lui, deux
        // arcs seuls ne se lisent pas comme une ouverture.
        return Math.abs(ny) < ouverture * 0.7 && force > 0.45 ? '·' : null;
      }

      // Un losange : des aretes droites, que l'oeil separe d'un cercle sans hesiter.
      case 'losange':
        return trait(Math.abs(Math.abs(nx) + Math.abs(ny) - r), e * 1.3, plein, pale);

      // Une etoile a branches. Elle scintille avec l'attaque au lieu de gonfler.
      case 'etoile': {
        if (d > r * 1.15) return null;
        const a = Math.atan2(ny, nx);
        const branches = 5;
        const rayonAngle = r * (0.35 + 0.65 * Math.abs(Math.cos(a * branches / 2)));
        return d < rayonAngle ? (d < rayonAngle * 0.55 ? plein : pale) : null;
      }

      // Un semis dont la densite suit le niveau. Pour ce qui n'a pas de contour : un
      // souffle, une texture, ce qui remplit sans jouer de note.
      default: {
        const graine = ((col * 31 + l * 17) % 97) / 97;
        return graine < force * 0.55 ? (graine < force * 0.22 ? plein : pale) : null;
      }
    }
  }

  // --------------------------------------------------------------- GRAVE
  // Un anneau de caracteres qui respire. La seule forme que Selim ait dite bonne.
  drawGrave(ctx, b) {
    const v = this.bass.value + this.bassHit.value * 0.5;
    const G = grillePour(b, 7);
    this.police(ctx, G.cw * 1.5);

    // Mesure en pixels, pour la meme raison que les registres : une normalisation par le
    // nombre de cellules donnait deux rangees de points au lieu d'un anneau.
    const cxg = (G.cols - 1) / 2, cyg = (G.lignes - 1) / 2;
    const unite = Math.max(1, Math.min(cxg * G.cw, cyg * G.ch));
    const r = (0.25 + v * 0.70);

    ctx.fillStyle = S.rgba(SRC.grave, 0.30 + v * 0.6);
    for (let l = 0; l < G.lignes; l++) {
      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const dx = (col - cxg) * G.cw / unite, dy = (l - cyg) * G.ch / unite;
        const d = Math.sqrt(dx * dx + dy * dy);
        ligne += Math.abs(d - r) < 0.16 ? '●' : (Math.abs(d - r) < 0.32 ? '·' : ' ');
      }
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // --------------------------------------------------------------- GRAIN
  // Les charleys.
  //
  // Ils ne montraient rien : ils ne dependaient que d'une impulsion, laquelle retombe en
  // moins d'un sixieme de temps — invisible entre deux frappes. Le fond suit desormais les
  // registres aigus en continu, et l'impulsion ne fait que l'aviver.
  drawGrain(ctx, b, coupe) {
    const fond = (this.regNiveau[4].value + this.regNiveau[5].value) * 0.5;
    const v = Math.max(fond * 0.7, this.hat.value);
    if (v < 0.01 || coupe < 0.05) return;

    const G = grillePour(b, 7);
    this.police(ctx, G.cw * 1.5);
    ctx.fillStyle = S.rgba(SRC.hat, 0.25 + v * 0.7);

    for (let l = 0; l < G.lignes; l++) {
      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const i = l * G.cols + col, phase = (i * 0.618) % 1;
        const eclat = Math.max(0,
          1 - Math.abs(((this.hat.value * 0.7 + fond * 0.3 + phase) % 1) - 0.5) * 2.6);
        ligne += eclat > 0.62 ? '✳' : (eclat > 0.42 ? '·' : (eclat > 0.25 ? '˙' : ' '));
      }
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // ------------------------------------------------------------- SPECTRE
  // Douze jauges couchees, une par bande, remplies depuis la gauche.
  //
  // Les colonnes de blocs empilees etaient illisibles. C'est la lecture d'un mixeur, et
  // l'oeil y suit une bande sans effort.
  drawSpectre(ctx, b, bands) {
    const G = grillePour(b, 12);
    this.police(ctx, G.cw * 1.5);
    const parLigne = Math.max(1, Math.floor(G.lignes / 12));

    for (let i = 0; i < 12; i++) {
      const l = Math.min(G.lignes - 1, i * parLigne);
      const v = bands[i] ?? 0, n = Math.round(v * (G.cols - 2));
      let ligne = '';
      for (let col = 0; col < G.cols - 2; col++) ligne += col < n ? '▬' : '·';
      ctx.fillStyle = S.rgba(SRC.spec, 0.20 + v * 0.7);
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // ------------------------------------------------------------- FRAPPES
  // Le kick s'ecarte du centre, les claps allument les bords, la tension s'ecrit dessous.
  drawFrappes(ctx, b) {
    const k = this.kick.value, cl = this.clap.value;
    const G = grille(b, 70);
    this.police(ctx, G.cw * 1.7);

    const l = Math.max(0, Math.floor(G.lignes / 2));
    const centre = (G.cols - 1) / 2, d = (1 - k) * centre;
    const bord = Math.round(cl * G.cols * 0.12);

    let ligne = '';
    for (let col = 0; col < G.cols; col++) {
      const dist = Math.abs(col - centre);
      if (k > 0.02 && Math.abs(dist - d) < 0.8 + this.onOne) ligne += '█';
      else if (cl > 0.02 && (col < bord || col >= G.cols - bord)) ligne += '▪';
      else if (k > 0.02 && this.onOne > 0.9 && dist < 0.7) ligne += '│';
      else ligne += '·';
    }
    ctx.fillStyle = S.rgba(SRC.kick, 0.14 + k * 0.8);
    ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);

    // L'ANNONCE DE TEMPO, LA OU LA GRILLE SE VOIT.
    //
    // Elle se pose sous les frappes, parce que c'est la que l'ecart se constate : le motif
    // du kick tombe a cote, et le chiffre dit pourquoi. La derive, elle, teinte l'annonce
    // — plus la grille a glisse, plus elle s'impose.
    const a = this.annonce.value;
    if (a > 0.02 && this.annonceBpm > 0) {
      const der = this.derive.value;
      ctx.fillStyle = S.rgba(SRC.grave, 0.25 + a * 0.55 + der * 0.20);
      ctx.fillText(`▸ ${this.annonceBpm.toFixed(1)} BPM`,
                   G.x0, G.y0 + Math.min(G.lignes - 1, l + 1) * G.ch);
    }

    if (this.tension.value > 0.03) {
      let t = '';
      const n = Math.round(this.tension.value * G.cols);
      for (let col = 0; col < G.cols; col++) t += col < n ? '▔' : ' ';
      ctx.fillStyle = S.rgba(SRC.grave, 0.25 + this.tension.value * 0.5);
      ctx.fillText(t, G.x0, G.y0 + Math.min(G.lignes - 1, l + 1) * G.ch);
    }
  }

  drawSweep(ctx, w, h, c) {
    if (this.sweep < 0) return;
    const x = (this.sweep * 1.4 - 0.2) * w;
    const fade = Math.max(0, 1 - Math.max(0, this.sweep - 0.8) * 5);
    S.sweepBand(ctx, x, w, h, w * 0.10, S.lighten(c, 0.7), 0.14 * fade);
  }
}

/** "8A" -> huit cotes. Une valeur absente laisse six, plutot que de reduire a un point. */
// Le nombre de cotes vient de la fiche, qui le tire du Camelot. Il etait calcule ici
// aussi, et les deux implementations avaient diverge : celle-ci rendait 8 cotes pour un
// Camelot 8, l'autre en rendait 10. Une regle qui vit en deux endroits finit toujours par
// vivre de deux facons. Le repli ne sert qu'aux fiches anciennes, sans le champ.
function sidesOf(track) {
  if (typeof track?.sides === 'number' && track.sides >= 3) return track.sides;
  const m = /^(\d{1,2})([AB])$/.exec((track?.camelot || '').trim().toUpperCase());
  return m ? Math.max(3, parseInt(m[1], 10)) : 6;
}

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec((hex || '').trim());
  if (!m) return null;
  const v = parseInt(m[1], 16);
  return { r: (v >> 16) & 255, g: (v >> 8) & 255, b: v & 255 };
}
