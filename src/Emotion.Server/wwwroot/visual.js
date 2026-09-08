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
import { Spring, Pulse, FrameLerp } from './motion.js';
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
const HAUTEUR = { aigu: 0.13, voix: 0.31, piano: 0.50, sol: 0.68 };

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

    // Ressorts pour tout ce qui doit paraitre avoir une masse. Un lissage simple
    // arriverait en retard et sans elan ; un ressort a une vitesse, donc de l'inertie.
    this.bass = new Spring(14);        // la basse est lourde, elle traine un peu
    this.voice = new Spring(22);
    this.bells = new Spring(40);       // le xylophone est vif
    this.level = new Spring(16);
    this.tonal = new Spring(6);        // la texture change lentement

    // LA STRUCTURE NE PORTE PAS DE FORME A ELLE, ELLE GOUVERNE LES AUTRES.
    //
    // La regle du fichier est « une source de son, une forme ». Or la mesure et la phrase
    // ne sont pas des sources : ce sont du temps. Leur donner un dessin propre reviendrait
    // a poser une interface par-dessus le visuel — une jauge, un compteur — et personne
    // ne regarde une jauge pendant un set. Elles pilotent donc le <b>comportement</b> des
    // formes existantes : leur amplitude, leur vitesse, leur accent.
    this.tension = new Spring(4);      // une montee dure huit mesures, rien ne presse
    this.drop = new Pulse(3);          // la rupture tient trois temps
    this.onOne = 0.55;                 // accent du temps fort, applique a l'onde du kick

    // Le timbre : la couleur du son, pas ses evenements. Un filtre passe-bas qu'on
    // ferme sur huit mesures ne change ni le tempo, ni les attaques, ni les notes — le
    // visuel restait donc impassible pendant le geste le plus visible d'un set.
    //
    // Raideur basse : ces grandeurs bougent au rythme de la main du DJ, pas de la
    // musique. Une reaction vive les ferait trembler.
    this.open = new Spring(5, 1);      // ouverture du filtre, 1 au demarrage
    this.bright = new Spring(5, 0.5);
    this.density = new Spring(4, 0.5);

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
    this.tension.step(st.buildup ?? 0, dt);
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

    // ---- valeurs continues, amorties ----
    this.bass.step(v.low ?? 0, dt);
    this.voice.step(v.mid ?? 0, dt);
    this.bells.step(v.high ?? 0, dt);
    this.level.step(rms, dt);
    this.tonal.step(frame.harmony?.tonality ?? 0, dt);

    const tb = frame.timbre ?? {};
    this.open.step(tb.openness ?? 1, dt);
    this.bright.step(tb.centroid ?? 0.5, dt);
    this.density.step(tb.density ?? 0.5, dt);

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

    const cx = w / 2, unit = Math.min(w, h);
    const sol = h * HAUTEUR.sol;

    // TRAME — quelques verticales tres pales, teintees par le disque en cours. Les formes
    // ont besoin d'un fond a quoi se mesurer ; sans elle, tout flotte dans le vide.
    ctx.strokeStyle = S.rgba(c, 0.05 + this.level.value * 0.05);
    ctx.lineWidth = 1;
    for (let i = 1; i < 12; i++) {
      const x = Math.round(w * i / 12) + 0.5;
      ctx.beginPath(); ctx.moveTo(x, h * 0.06); ctx.lineTo(x, h * 0.94); ctx.stroke();
    }

    // L'OUVERTURE DU FILTRE PILOTE TOUT LE RENDU.
    //
    // Quand le passe-bas se ferme, le son perd ses aigus : le visuel doit perdre ses
    // details de la meme facon. Trois gestes simultanes, parce qu'un seul ne se lirait
    // pas — la scene se contracte, elle se trouble, et tout ce qui est aigu s'efface.
    const open = this.open.value;

    ctx.save();
    // La tension joue contre la contraction : une montee ecarte les formes, une rupture
    // les relache d'un coup. Les deux gestes s'opposent sur le meme axe, ce qui rend le
    // retour des graves lisible apres huit mesures de gonflement.
    const swell = 1 + this.tension.value * 0.10 + this.drop.value * 0.10;
    const shrink = (0.80 + open * 0.20) * swell;
    ctx.translate(cx, sol); ctx.scale(shrink, shrink); ctx.translate(-cx, -sol);

    const blur = (1 - open) * 9;
    if (blur > 0.5) ctx.filter = `blur(${blur.toFixed(1)}px)`;

    this.drawTension(ctx, w, h, sol, unit);
    this.drawHorizon(ctx, w, sol, c);
    this.drawBass(ctx, cx, sol, w, unit);
    this.drawKick(ctx, cx, sol, w, unit);
    this.drawClaps(ctx, cx, sol, w, unit);
    this.drawPiano(ctx, cx, h, unit);
    this.drawVoice(ctx, cx, h, unit, sides);
    this.drawHighs(ctx, w, h, unit, open);
    this.drawHats(ctx, w, h, unit, open);
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
  // Une lueur qui monte du sol, comme une chaleur avant la rupture.
  //
  // C'est la seule chose du rendu qui n'obeisse pas a un evenement mais l'annonce. Une
  // lueur plutot qu'un contour : une montee n'a pas de bord net, elle se sent avant de se
  // voir, et une forme dessinee donnerait une precision que la mesure n'a pas.
  drawTension(ctx, w, h, sol, unit) {
    const t = this.tension.value, d = this.drop.value;
    if (t < 0.02 && d < 0.02) return;

    const haut = sol - unit * 0.4;
    const g = ctx.createLinearGradient(0, h, 0, haut);
    g.addColorStop(0, S.rgba(SRC.basse, t * 0.20 + d * 0.25));
    g.addColorStop(1, S.rgba(SRC.basse, 0));
    ctx.fillStyle = g;
    ctx.fillRect(0, haut, w, h - haut);
  }

  // ------------------------------------------------------------- HORIZON
  // La ligne sur laquelle tout repose, teintee par le disque en cours. Elle epaissit sur
  // chaque kick : c'est elle qui donne le sol a la composition.
  drawHorizon(ctx, w, sol, c) {
    ctx.strokeStyle = S.rgba(c, 0.18 + this.kick.value * 0.30);
    ctx.lineWidth = 1 + this.kick.value * 1.5;
    ctx.beginPath();
    ctx.moveTo(w * 0.04, sol); ctx.lineTo(w * 0.96, sol);
    ctx.stroke();
  }

  // ---------------------------------------------------------------- BASSE
  // Un arc plein pose sur l'horizon, qui enfle et retombe. Une masse, pas une tache : le
  // degrade monte, le contour reste net, et rien ne s'estompe dans le vide.
  drawBass(ctx, cx, sol, w, unit) {
    const r = unit * (0.13 + this.bass.value * 0.22 + this.bassHit.value * 0.07);
    if (r <= 0) return;

    ctx.save();
    ctx.beginPath(); ctx.rect(0, 0, w, sol); ctx.clip();

    const g = ctx.createLinearGradient(0, sol - r, 0, sol);
    g.addColorStop(0, S.rgba(SRC.basse, 0.10 + this.bass.value * 0.18));
    g.addColorStop(1, S.rgba(SRC.basse, 0.42 + this.bass.value * 0.40));
    ctx.fillStyle = g;
    ctx.beginPath(); ctx.arc(cx, sol, r, Math.PI, 0); ctx.fill();

    ctx.strokeStyle = S.rgba(SRC.basse, 0.55 + this.bass.value * 0.45);
    ctx.lineWidth = Math.max(1.5, unit * 0.005);
    ctx.beginPath(); ctx.arc(cx, sol, r, Math.PI, 0); ctx.stroke();
    ctx.restore();
  }

  // ----------------------------------------------------------------- KICK
  // Deux traits blancs qui partent du centre et filent vers les bords, le long de
  // l'horizon.
  //
  // La seule chose blanche de la scene, et la seule qui traverse : impossible a
  // confondre avec le reste, ce qui compte pour l'evenement le plus frequent d'un set.
  // Le premier temps de la mesure porte un accent supplementaire au centre — c'est le
  // seul endroit ou le rang du temps se voit directement.
  drawKick(ctx, cx, sol, w, unit) {
    const k = this.kick.value;
    if (k < 0.015) return;

    const d = (1 - k) * w * 0.52;
    const ht = unit * 0.075 * k * (0.55 + this.onOne * 0.75);

    ctx.strokeStyle = S.rgba(SRC.kick, k * this.onOne * 0.95);
    ctx.lineWidth = Math.max(2, unit * 0.011 * k * (0.6 + this.onOne * 0.6));
    ctx.lineCap = 'round';
    for (const s of [-1, 1]) {
      const x = cx + s * d;
      ctx.beginPath(); ctx.moveTo(x, sol - ht); ctx.lineTo(x, sol + ht * 0.45); ctx.stroke();
    }

    if (this.onOne > 0.9) {
      ctx.strokeStyle = S.rgba(SRC.kick, k * 0.5);
      ctx.lineWidth = Math.max(1, unit * 0.004);
      ctx.beginPath();
      ctx.moveTo(cx, sol - ht * 1.5); ctx.lineTo(cx, sol + ht * 0.7); ctx.stroke();
    }
  }

  // ---------------------------------------------------------------- CLAPS
  // Deux cercles ouverts qui gonflent depuis les bords, a hauteur d'horizon. Rien d'autre
  // ne va la : un clap se reconnait a sa place autant qu'a sa forme.
  drawClaps(ctx, cx, sol, w, unit) {
    const v = this.clap.value;
    if (v < 0.015) return;

    const r = unit * (0.045 + v * 0.085), o = w * 0.30;
    ctx.strokeStyle = S.rgba(SRC.piano, v * 0.85);
    ctx.lineWidth = Math.max(2, unit * 0.009 * v);
    for (const s of [-1, 1]) {
      ctx.beginPath(); ctx.arc(cx + s * o, sol, r, 0, TAU); ctx.stroke();
    }
  }

  // ---------------------------------------------------------------- PIANO
  // Un octogone violet en rotation lente, au milieu de la scene.
  //
  // Il ne clignote pas : il tourne, et son epaisseur suit le medium. Une forme qui tourne
  // se remarque sans agresser — ce qui convient a un registre presque toujours present,
  // la ou un clignotement permanent fatiguerait.
  drawPiano(ctx, cx, h, unit) {
    const m = this.voice.value, hit = this.voiceHit.value;
    const r = unit * (0.105 + m * 0.045 + hit * 0.03);
    const y = h * HAUTEUR.piano;

    S.polygon(ctx, cx, y, r, 8, this.spin * 0.5, SRC.piano, 0.18 + m * 0.55,
              false, Math.max(1.5, unit * 0.005 * (0.6 + m)));
    if (hit > 0.04)
      S.polygon(ctx, cx, y, r * (1 + hit * 0.18), 8, this.spin * 0.5, SRC.piano,
                hit * 0.4, false, 1.5);
  }

  // ------------------------------------------------------------------ VOIX
  // Un triangle cyan, pointe en haut, qui monte legerement quand la voix pousse.
  //
  // Son nombre de cotes vient du Camelot : c'est la seule forme qui porte la tonalite du
  // disque, et c'est voulu — la voix et le corps du piano sont ce qui chante.
  drawVoice(ctx, cx, h, unit, sides) {
    const m = this.voice.value, hit = this.voiceHit.value;
    const r = unit * (0.055 + m * 0.05 + hit * 0.045);
    const y = h * HAUTEUR.voix - m * unit * 0.03;
    const n = sides === 6 ? 3 : sides;

    S.polygon(ctx, cx, y, r, n, 0, SRC.voix, 0.22 + m * 0.6, true);
    S.polygon(ctx, cx, y, r * 1.35, n, 0, SRC.voix, 0.10 + hit * 0.5, false, 1.4);
  }

  // ---------------------------------------------------------------- AIGUES
  // Petits losanges jaunes disperses en haut, effaces des que le filtre ferme.
  drawHighs(ctx, w, h, unit, open) {
    const coupe = Math.pow(open, 1.5);
    const v = this.bells.value * 0.55 + this.bellHit.value;
    if (v < 0.03 || coupe < 0.05) return;

    for (let i = 0; i < 6; i++) {
      const p = S.scatter(i);
      S.polygon(ctx, w * (0.10 + p.x * 0.80), h * (HAUTEUR.aigu - 0.04 + p.y * 0.14),
                unit * 0.020 * (0.45 + v), 4, Math.PI / 4, SRC.aigu, v * coupe * 0.9, true);
    }
  }

  // -------------------------------------------------------------- CHARLEYS
  // Une reglette de traits fins tout en haut, comme une graduation. Ils etaient meles aux
  // aigues et l'on confondait les deux ; les separer suffit a les distinguer.
  drawHats(ctx, w, h, unit, open) {
    const coupe = Math.pow(open, 1.5), v = this.hat.value;
    if (v < 0.015 || coupe < 0.05) return;

    ctx.strokeStyle = S.rgba(SRC.hat, v * coupe * 0.7);
    ctx.lineWidth = Math.max(1, unit * 0.0035);
    ctx.lineCap = 'butt';
    for (let i = 0; i < 16; i++) {
      const x = w * (0.10 + i * 0.0533);
      ctx.beginPath();
      ctx.moveTo(x, h * 0.045);
      ctx.lineTo(x, h * 0.045 + unit * 0.022 * (0.35 + v * 0.65));
      ctx.stroke();
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
