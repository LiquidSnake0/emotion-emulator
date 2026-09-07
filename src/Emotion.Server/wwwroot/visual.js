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
      sides: sidesOf(t.camelot),
      kind: t.scene?.kind ?? 'Rest',
      intensity: t.scene?.intensity ?? 0,
    } : null;
  }

  setTrack(t) {
    this.color = hexToRgb(t.colorHex) ?? { r: 110, g: 110, b: 110 };
    this.sides = sidesOf(t.camelot);
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

    for (const p of [this.kick, this.clap, this.hat,
                     this.bassHit, this.voiceHit, this.bellHit])
      p.step(beatMs, dtMs);

    // ---- valeurs continues, amorties ----
    this.bass.step(v.low ?? 0, dt);
    this.voice.step(v.mid ?? 0, dt);
    this.bells.step(v.high ?? 0, dt);
    this.level.step(rms, dt);
    this.tonal.step(frame.harmony?.tonality ?? 0, dt);

    this.spin += dt * (0.06 + this.level.value * 0.22);
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

    const cx = w / 2, cy = h / 2;
    const unit = Math.min(w, h);

    this.drawBass(ctx, cx, cy, unit, c);
    this.drawKickWave(ctx, cx, cy, unit, c);
    this.drawVoice(ctx, cx, cy, unit, c, sides);
    this.drawBells(ctx, w, h, unit, c);
    this.drawClap(ctx, cx, cy, unit, c);
    this.drawHat(ctx, w, h, unit, c, bands);
    this.drawSweep(ctx, w, h, c);

    this.clips.draw(ctx, w, h, this.level.value);

    // ---- ecrans de reglage, par-dessus tout ----
    this.diag.push(frame);
    this.diag.draw(ctx, w, h, frame);
    this.signals.push(frame);
    this.signals.draw(ctx, w, h, { ...frame, sceneName: this.kindName });
    this.calibrate.draw(ctx, w, h, frame, this.latencyMs);
  }

  // ---------------------------------------------------------------- BASSE
  // Un cercle plein au centre, qui respire. C'est la masse du morceau : lourde, lente,
  // toujours au meme endroit. Elle enfle sur chaque note grave et se degonfle seule.
  drawBass(ctx, cx, cy, unit, c) {
    const r = unit * (0.06 + this.bass.value * 0.14 + this.bassHit.value * 0.05);
    S.disc(ctx, cx, cy, r, c, 0.20 + this.bass.value * 0.35, 0.55);
    S.disc(ctx, cx, cy, r * 0.45, S.lighten(c, 0.35), 0.15 + this.bassHit.value * 0.35);
  }

  // ----------------------------------------------------------------- KICK
  // Une onde circulaire qui part du centre et s'ouvre. Elle ne remplit rien : c'est un
  // front qui passe, la pulsation qu'on suit du regard.
  drawKickWave(ctx, cx, cy, unit, c) {
    const k = this.kick.value;
    if (k < 0.02) return;
    const r = unit * (0.10 + (1 - k) * 0.42);
    S.ring(ctx, cx, cy, r, Math.max(1.5, unit * 0.012 * k), S.lighten(c, 0.25), k * 0.55);
  }

  // ------------------------------------------------------------------ VOIX
  // Des anneaux concentriques, dont le nombre de cotes vient du Camelot. C'est la seule
  // forme qui porte la tonalite du morceau, et c'est voulu : la voix et le corps du
  // piano sont ce qui chante.
  drawVoice(ctx, cx, cy, unit, c, sides) {
    const m = this.voice.value;
    if (m < 0.02 && this.voiceHit.value < 0.02) return;

    const base = unit * (0.20 + m * 0.06);
    for (let i = 0; i < 3; i++) {
      const r = base * (1 + i * 0.16) + this.voiceHit.value * unit * 0.02;
      const a = (0.10 + m * 0.30 + this.voiceHit.value * 0.25) * (1 - i * 0.28);
      S.polygon(ctx, cx, cy, r, sides, this.spin * (1 - i * 0.3),
                S.lighten(c, 0.2), a, false, Math.max(1, unit * 0.0035));
    }
  }

  // ------------------------------------------------------------ XYLOPHONE
  // Des triangles disperses dans le haut. Places par une suite deterministe : deux
  // appels de meme rang donnent le meme point, sinon ils danseraient d'une image a
  // l'autre et seraient illisibles.
  drawBells(ctx, w, h, unit, c) {
    const b = this.bells.value;
    const pulse = this.bellHit.value;
    if (b < 0.02 && pulse < 0.02) return;

    const count = 9;
    const tint = S.lighten(c, 0.55);

    for (let i = 0; i < count; i++) {
      const p = S.scatter(i, 3);
      const x = p.x * w;
      const y = h * (0.06 + p.y * 0.30);

      // Chaque triangle a sa propre phase : ils ne s'allument pas ensemble, ce qui
      // ferait un flash. Ils se repondent.
      const phase = i / count;
      const amp = b * 0.6 + pulse * (0.5 + 0.5 * Math.cos(phase * TAU - this.spin * 2));
      if (amp < 0.03) continue;

      const r = unit * (0.012 + amp * 0.022);
      S.triangle(ctx, x, y, r, this.spin * 0.6 + phase * TAU, tint, Math.min(0.85, amp));
    }
  }

  // ------------------------------------------------------------------ CLAP
  // Deux losanges, a gauche et a droite. Forme et place distinctes du reste : on ne peut
  // pas les confondre du coin de l'oeil avec le kick, qui est central et rond.
  drawClap(ctx, cx, cy, unit, c) {
    const a = this.clap.value;
    if (a < 0.02) return;
    const r = unit * (0.020 + a * 0.035);
    const dx = unit * (0.30 + (1 - a) * 0.05);
    const tint = S.lighten(c, 0.45);
    S.diamond(ctx, cx - dx, cy, r, tint, a * 0.8);
    S.diamond(ctx, cx + dx, cy, r, tint, a * 0.8);
  }

  // --------------------------------------------------------------- CHARLEY
  // Des traits courts en bas, dont la hauteur suit les bandes aigues. Discrets : ils
  // tombent souvent, et c'est ce qui donne le grain sans occuper le regard.
  drawHat(ctx, w, h, unit, c, bands) {
    const a = this.hat.value;
    if (a < 0.02) return;

    const n = 8;
    const y = h * 0.86;
    const tint = S.lighten(c, 0.3);
    for (let i = 0; i < n; i++) {
      const band = bands[Math.min(bands.length - 1, 6 + (i % 6))] ?? 0;
      const x = w * (0.14 + (i / (n - 1)) * 0.72);
      S.tick(ctx, x, y, unit * (0.008 + band * 0.020) * a,
             Math.max(1, unit * 0.0025), tint, a * 0.5);
    }
  }

  // -------------------------------------------------------------- NOUVEAUTE
  // Une barre qui traverse. Le seul geste horizontal du vocabulaire : un evenement qui
  // n'est ni une frappe ni une note merite une forme qui n'appartient qu'a lui.
  drawSweep(ctx, w, h, c) {
    if (this.sweep < 0) return;
    const x = (this.sweep * 1.4 - 0.2) * w;
    const fade = Math.max(0, 1 - Math.max(0, this.sweep - 0.8) * 5);
    S.sweepBand(ctx, x, w, h, w * 0.10, S.lighten(c, 0.7), 0.14 * fade);
  }
}

/** "8A" -> huit cotes. Une valeur absente laisse six, plutot que de reduire a un point. */
function sidesOf(camelot) {
  const m = /^(\d{1,2})([AB])$/.exec((camelot || '').trim().toUpperCase());
  return m ? Math.max(3, parseInt(m[1], 10)) : 6;
}

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec((hex || '').trim());
  if (!m) return null;
  const v = parseInt(m[1], 16);
  return { r: (v >> 16) & 255, g: (v >> 8) & 255, b: v & 255 };
}
