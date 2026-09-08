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
const SRC = {
  basse: { r: 53,  g: 208, b: 127 },
  kick:  { r: 244, g: 246, b: 244 },
  voix:  { r: 77,  g: 217, b: 232 },
  piano: { r: 167, g: 139, b: 232 },
  aigu:  { r: 232, g: 200, b: 77  },
  hat:   { r: 140, g: 148, b: 145 },
};

// LE REGISTRE COMMANDE LA HAUTEUR.
//
// Empiler toutes les formes au centre les rendait illisibles, et les separer par la
// couleur seule ne suffisait pas. L'ordre vertical resout les deux d'un coup parce qu'il
// est deja dans l'oreille : le grave est bas et large, l'aigu est haut et fin. La scene
// se lit alors comme un spectre debout.
// CHAQUE SOURCE A SA BANDE, ET N'EN SORT PAS.
//
// Donner une position ne suffit pas : sans borne, une forme qui bouge finit dans celle du
// voisin — la bande de la voix, avec sa course d'un tiers de hauteur, entrait dans
// l'octogone du piano. Les zones se touchent sans se recouvrir, et chaque forme est
// dimensionnee pour tenir dans la sienne.
const ZONE = {
  hat:   { de: 0.02, a: 0.10 },
  aigu:  { de: 0.13, a: 0.24 },
  voix:  { de: 0.27, a: 0.42 },
  piano: { de: 0.45, a: 0.58 },
  basse: 0.61,     // plafond de l'arc : il ne remonte jamais au-dela
  sol:   0.78,     // l'horizon
};

// LA SCENE EST UNE MATRICE DE CASES, ET ELLE N'EST PAS SYMETRIQUE.
//
// Empiler les formes sur un axe unique les faisait se recouvrir quoi qu'on fasse — trois
// tentatives de bornage n'y ont rien change. Le probleme n'etait pas le calcul mais la
// composition : tout partageait la meme colonne.
//
// Chaque source occupe donc sa propre case, de taille et de place differentes. Une case
// ne peut pas mordre sur une autre puisqu'elles ne se touchent que par leurs bords, et
// l'asymetrie donne a l'oeil de quoi se reperer : on apprend « la voix est a droite »
// plus vite que « la voix est a trente-quatre centiemes de hauteur ».
//
//   ┌──────────┬────────────────┬──────────┐
//   │ charleys │                │  aigues  │
//   ├──────────┤     BASSE      ├──────────┤
//   │  piano   │                │   VOIX   │
//   ├──────────┴────────────────┴──────────┤
//   │              kick · claps            │
//   └──────────────────────────────────────┘
//
// x, y, largeur et hauteur en fractions de l'ecran.
const CASE = {
  hat:   { x: 0.03, y: 0.05, w: 0.22, h: 0.26 },
  piano: { x: 0.03, y: 0.35, w: 0.22, h: 0.36 },
  basse: { x: 0.28, y: 0.05, w: 0.44, h: 0.66 },
  aigu:  { x: 0.75, y: 0.05, w: 0.22, h: 0.26 },
  voix:  { x: 0.75, y: 0.35, w: 0.22, h: 0.36 },
  bas:   { x: 0.03, y: 0.75, w: 0.94, h: 0.20 },
};

const boite = (c, w, h) => ({
  x: c.x * w, y: c.y * h, w: c.w * w, h: c.h * h,
  cx: (c.x + c.w / 2) * w, cy: (c.y + c.h / 2) * h,
  u: Math.min(c.w * w, c.h * h),
});

// LES HUIT NIVEAUX DE BLOC, DU VIDE AU PLEIN.
//
// Le rendu est en caracteres parce qu'il doit rester leger — il n'y a pas de GPU sous la
// main, et un remplissage de texte coute une fraction de ce que coute un degrade. Ils
// donnent en prime une identite que des polygones translucides n'avaient pas : celle d'un
// terminal, ce qui va bien a un projet qui passe son temps a mesurer.
const BLOCS = [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
const bloc = (v) => BLOCS[Math.max(0, Math.min(8, Math.round(v * 8)))];

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
                     this.bassHit, this.voiceHit, this.bellHit])
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
    // Le filtre contracte la scene entiere et la trouble, sans deplacer les cases les
    // unes par rapport aux autres : elles restent lisibles meme fermees.
    const swell = 1 + this.tension.value * 0.08 + this.drop.value * 0.08;
    const shrink = (0.88 + open * 0.12) * swell;
    ctx.translate(w / 2, h / 2); ctx.scale(shrink, shrink); ctx.translate(-w / 2, -h / 2);
    const flou = (1 - open) * Math.min(6, h * 0.008);
    if (flou > 0.5) ctx.filter = `blur(${flou.toFixed(1)}px)`;

    this.drawTension(ctx, w, h);
    this.drawCadres(ctx, w, h, c);
    this.drawBasse(ctx, boite(CASE.basse, w, h));
    this.drawBouche(ctx, boite(CASE.voix, w, h), coupe);
    this.drawPiano(ctx, boite(CASE.piano, w, h));
    this.drawAigues(ctx, boite(CASE.aigu, w, h), coupe);
    this.drawCharleys(ctx, boite(CASE.hat, w, h), coupe);
    this.drawBas(ctx, boite(CASE.bas, w, h), w);
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

    const g = ctx.createLinearGradient(0, h, 0, h * 0.35);
    g.addColorStop(0, S.rgba(SRC.basse, t * 0.22 + d * 0.28));
    g.addColorStop(1, S.rgba(SRC.basse, 0));
    ctx.fillStyle = g;
    ctx.fillRect(0, h * 0.35, w, h * 0.65);
  }

  // -------------------------------------------------------------- CADRES
  // Le trait de chaque case, teinte par le disque en cours. Il donne la matrice, et c'est
  // lui qui rend l'asymetrie lisible : sans bord, des formes eparses paraissent flotter au
  // hasard plutot qu'occuper des places.
  drawCadres(ctx, w, h, c) {
    ctx.strokeStyle = S.rgba(c, 0.10 + this.level.value * 0.10);
    ctx.lineWidth = 1;
    for (const k of Object.keys(CASE)) {
      const b = boite(CASE[k], w, h);
      ctx.strokeRect(Math.round(b.x) + 0.5, Math.round(b.y) + 0.5,
                     Math.round(b.w), Math.round(b.h));
    }
  }

  // --------------------------------------------------------------- BASSE
  // Un halo vert au centre, qui grandit et retrecit. Rien d'autre : c'est la seule forme
  // que Selim ait dite bonne, et la seule qui occupe la grande case.
  drawBasse(ctx, b) {
    const v = this.bass.value + this.bassHit.value * 0.5;
    const r = b.u * (0.16 + v * 0.30);

    const g = ctx.createRadialGradient(b.cx, b.cy, r * 0.05, b.cx, b.cy, r);
    g.addColorStop(0,   S.rgba(SRC.basse, 0.30 + v * 0.40));
    g.addColorStop(0.6, S.rgba(SRC.basse, 0.10 + v * 0.20));
    g.addColorStop(1,   S.rgba(SRC.basse, 0));
    ctx.fillStyle = g;
    ctx.beginPath(); ctx.arc(b.cx, b.cy, r, 0, TAU); ctx.fill();

    ctx.strokeStyle = S.rgba(SRC.basse, 0.35 + v * 0.5);
    ctx.lineWidth = Math.max(1.5, b.u * 0.008);
    ctx.beginPath(); ctx.arc(b.cx, b.cy, r * 0.62, 0, TAU); ctx.stroke();
  }

  // ---------------------------------------------------------------- VOIX
  // Une bouche en caracteres, qui s'ouvre et se ferme.
  //
  // Elle remplace la bande qui montait et descendait — « pourquoi la voix rebondit comme
  // une balle de basket ». Une voix ne se deplace pas dans l'espace : elle s'ouvre. Ce
  // que le contour melodique commande ici n'est donc plus une position mais la
  // <b>courbure</b> des levres, qui se relevent dans l'aigu et retombent dans le grave.
  drawBouche(ctx, b, coupe) {
    const v = this.voice.value + this.voiceHit.value * 0.45;
    const colonnes = 9;
    const taille = Math.max(7, b.w / colonnes * 1.25);
    ctx.font = `${taille.toFixed(0)}px ui-monospace, "JetBrains Mono", monospace`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    // La courbure : le contour releve les coins dans l'aigu, les abaisse dans le grave.
    const courbe = (this.midPitch.value - 0.5) * b.h * 0.28;

    for (let i = 0; i < colonnes; i++) {
      const t = i / (colonnes - 1);                 // 0 a 1 sur la largeur
      const cloche = Math.sin(t * Math.PI);         // ouverte au centre, close aux coins
      const ecart = b.h * (0.03 + v * 0.22) * cloche;
      const x = b.x + b.w * (0.10 + t * 0.80);
      const y = b.cy + (t - 0.5) * 2 * courbe;

      ctx.fillStyle = S.rgba(SRC.voix, (0.25 + v * 0.6) * (0.45 + cloche * 0.55));
      ctx.fillText('▄', x, y - ecart);
      ctx.fillText('▀', x, y + ecart);
    }

    // Ce que la bouche dit, quand elle pousse : le niveau ecrit en blocs sous elle.
    if (v > 0.12 && coupe > 0.2) {
      let ligne = '';
      for (let i = 0; i < colonnes; i++)
        ligne += bloc(v * Math.sin((i / (colonnes - 1)) * Math.PI));
      ctx.fillStyle = S.rgba(SRC.voix, v * 0.35 * coupe);
      ctx.font = `${(taille * 0.6).toFixed(0)}px ui-monospace, monospace`;
      ctx.fillText(ligne, b.cx, b.y + b.h * 0.86);
    }
  }

  // --------------------------------------------------------------- PIANO
  // L'octogone, seul dans sa case, qui tourne au lieu de clignoter.
  drawPiano(ctx, b) {
    const m = this.voice.value, hit = this.voiceHit.value;
    const r = b.u * (0.24 + m * 0.10 + hit * 0.07);

    S.polygon(ctx, b.cx, b.cy, r, 8, this.spin * 0.5, SRC.piano,
              0.20 + m * 0.55, false, Math.max(1.5, b.u * 0.016 * (0.6 + m)));
    if (hit > 0.04)
      S.polygon(ctx, b.cx, b.cy, r * (1 + hit * 0.22), 8, this.spin * 0.5,
                SRC.piano, hit * 0.45, false, 1.5);
  }

  // -------------------------------------------------------------- AIGUES
  // Une colonne de caracteres qui monte et descend avec le contour de l'aigu. Le triangle
  // est libre : il sert desormais de pointe a cette colonne, la ou elle culmine.
  drawAigues(ctx, b, coupe) {
    const v = this.bells.value * 0.6 + this.bellHit.value;
    if (v < 0.03 || coupe < 0.05) return;

    const lignes = 7;
    const taille = Math.max(7, b.h / lignes * 0.9);
    ctx.font = `${taille.toFixed(0)}px ui-monospace, "JetBrains Mono", monospace`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    const sommet = 1 - this.highPitch.value;        // 0 en bas, 1 en haut
    for (let i = 0; i < lignes; i++) {
      const t = i / (lignes - 1);
      const pres = 1 - Math.abs(t - sommet) * 2.2;  // brille autour du sommet
      if (pres < 0.05) continue;

      ctx.fillStyle = S.rgba(SRC.aigu, pres * v * coupe * 0.9);
      ctx.fillText(bloc(pres * v), b.cx, b.y + b.h * (0.10 + t * 0.80));
    }

    S.polygon(ctx, b.cx, b.y + b.h * (0.10 + sommet * 0.80), b.u * 0.11 * (0.4 + v),
              3, 0, SRC.aigu, v * coupe * 0.85, true);
  }

  // ------------------------------------------------------------ CHARLEYS
  // Du grain : des caracteres qui scintillent chacun a sa phase.
  drawCharleys(ctx, b, coupe) {
    const v = this.hat.value;
    if (v < 0.015 || coupe < 0.05) return;

    const taille = Math.max(6, b.u * 0.11);
    ctx.font = `${taille.toFixed(0)}px ui-monospace, monospace`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    for (let i = 0; i < 18; i++) {
      const p = S.scatter(i, 3);
      const phase = (i * 0.618) % 1;
      const eclat = Math.max(0, 1 - Math.abs(((v + phase) % 1) - 0.5) * 2.6);
      if (eclat < 0.06) continue;

      ctx.fillStyle = S.rgba(SRC.hat, eclat * v * coupe);
      ctx.fillText(eclat > 0.6 ? '·' : '˙',
                   b.x + b.w * (0.08 + p.x * 0.84),
                   b.y + b.h * (0.10 + p.y * 0.80));
    }
  }

  // ------------------------------------------------------- KICK ET CLAPS
  // La bande du bas : le kick la traverse, les claps l'allument a ses deux bouts.
  drawBas(ctx, b, w) {
    const k = this.kick.value, cl = this.clap.value;
    const y = b.cy;

    // Le kick : deux traits blancs qui filent du centre vers les bords.
    if (k > 0.015) {
      const d = (1 - k) * b.w * 0.5;
      const ht = b.h * 0.38 * k * (0.5 + this.onOne * 0.8);
      ctx.strokeStyle = S.rgba(SRC.kick, k * this.onOne * 0.95);
      ctx.lineWidth = Math.max(2, b.h * 0.06 * k * (0.6 + this.onOne * 0.6));
      ctx.lineCap = 'round';
      for (const s of [-1, 1]) {
        const x = b.cx + s * d;
        ctx.beginPath(); ctx.moveTo(x, y - ht); ctx.lineTo(x, y + ht); ctx.stroke();
      }
      if (this.onOne > 0.9) {
        ctx.strokeStyle = S.rgba(SRC.kick, k * 0.55);
        ctx.lineWidth = Math.max(1, b.h * 0.02);
        ctx.beginPath(); ctx.moveTo(b.cx, y - ht * 1.4);
        ctx.lineTo(b.cx, y + ht * 1.4); ctx.stroke();
      }
    }

    // Les claps : les deux extremites de la bande s'allument.
    if (cl > 0.015) {
      const l = b.w * (0.05 + cl * 0.10);
      ctx.strokeStyle = S.rgba(SRC.piano, cl * 0.9);
      ctx.lineWidth = Math.max(2, b.h * 0.10 * cl);
      ctx.lineCap = 'butt';
      ctx.beginPath(); ctx.moveTo(b.x, y); ctx.lineTo(b.x + l, y); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(b.x + b.w - l, y); ctx.lineTo(b.x + b.w, y); ctx.stroke();
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
