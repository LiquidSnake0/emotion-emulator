// Ce que le retroprojecteur affiche.
//
// Le principe qui tient tout : le son donne le mouvement, la base donne le caractere.
//
//   famille du crate  ->  phenomene       M- des vagues, M+ un orage
//   couleur mesuree   ->  palette
//   chiffre Camelot   ->  nombre de branches de la figure
//   lettre Camelot    ->  A anguleux, B arrondi
//   bandes            ->  relief
//   attaque (onset)   ->  declenchement
//
// Aucun tempo n'arrive de la base : tout ce qui bouge est declenche par le son.
// Pitcher un disque ne desynchronise donc rien.
//
// Deux phenomenes sont ecrits, Waves et Thunder, les deux exemples donnes par Selim.
// Les autres retombent sur la figure geometrique commune, le temps de les regler a
// l'ecoute famille par famille.

import { ClipLibrary } from './clips.js';
import { Diagnostics } from './diag.js';

const TAU = Math.PI * 2;

export class Visual {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false });

    // Ce qui joue.
    this.color = { r: 110, g: 110, b: 110 };
    this.kind = 'Rest';
    this.intensity = 0;
    this.sides = 6;
    this.round = false;

    // Ce qui est cale au casque. Il n'atteint le mur que dans la mesure ou il est
    // deja passe dans le master : c'est `blend` qui l'y autorise, pas un bouton.
    this.next = null;
    this.blend = 0;

    // Une enveloppe par registre : chaque instrument a son effet, et c'est ce qui
    // permet a l'oeil de raccrocher ce qu'il voit a ce qu'il entend.
    this.shock = 0;    // kick : la masse pulse, l'onde part du centre
    this.flash = 0;    // clap : l'eclair
    this.spark = 0;    // charleys : le scintillement
    this.spin = 0;
    this.swell = 0;    // avancee des vagues, propre a Waves

    // L'harmonie : ce qui sonne, par opposition a ce qui frappe. Le piano vit ici.
    this.chord = 0;    // impulsion sur un changement d'accord
    this.pitch = null; // classe de hauteur dominante, 0 a 11
    this.tonal = 0;    // 0 bruite, 1 franchement tonal — lisse, il ne doit pas sauter

    // Les clips et images deposes par Selim. La bibliotheque se debrouille d'un
    // dossier vide : sans assets, le visuel geometrique tourne seul.
    this.clips = new ClipLibrary();
    this.clips.load();

    // L'ecran de reglage, masque par defaut. Touche D.
    this.diag = new Diagnostics();

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

  // La face calee au casque. On la garde de cote sans rien changer a l'ecran : tant
  // que le fader est ferme, le public ne doit rien voir venir.
  setCued(t) {
    this.next = t ? {
      color: hexToRgb(t.colorHex) ?? { r: 110, g: 110, b: 110 },
      kind: t.scene?.kind ?? 'Rest',
      intensity: t.scene?.intensity ?? 0,
    } : null;
  }

  setTrack(t) {
    this.color = hexToRgb(t.colorHex) ?? { r: 110, g: 110, b: 110 };
    this.kind = t.scene?.kind ?? 'Rest';
    this.intensity = t.scene?.intensity ?? 0;

    // "8A" -> huit branches, anguleux. Une valeur absente laisse la figure en place
    // plutot que de la reduire a un point.
    const m = /^(\d{1,2})([AB])$/.exec((t.camelot || '').trim().toUpperCase());
    if (m) {
      this.sides = Math.max(3, parseInt(m[1], 10));
      this.round = m[2] === 'B';
    }
  }

  draw(f) {
    const { ctx, w, h } = this;

    // Sans ces enveloppes, un effet ne durerait qu'une image et ne se verrait pas.
    // `hit` et non `h` : dans cette methode, h est deja la hauteur du canvas.
    const hit = f.hits ?? {};
    if (hit.kick) this.shock = 1;
    if (hit.clap) this.flash = 1;
    if (hit.hat)  this.spark = 1;

    // Les clips partent sur le clap : c'est lui qui marque la phrase, le kick est
    // trop regulier pour servir de declencheur d'image.
    if (hit.clap) this.clips.onOnset(this.kind, this.intensity);

    // Le changement d'accord a une enveloppe beaucoup plus lente qu'une frappe : une
    // harmonie s'installe, elle ne claque pas.
    const ha = f.harmony ?? {};
    if ((ha.change ?? 0) > 0.35) this.chord = 1;
    if (ha.pitch != null) this.pitch = ha.pitch;
    this.tonal += ((ha.tonality ?? 0) - this.tonal) * 0.05;

    this.chord *= 0.965;
    this.shock *= 0.88;
    // Un eclair garde une remanence : a 0.72 il disparaissait en deux dixiemes,
    // trop vite pour que l'oeil le lise comme un eclair plutot qu'un scintillement.
    this.flash *= 0.86;
    this.spark *= 0.74;

    this.spin += 0.0015 + f.rms * 0.004;
    this.swell += 0.004 + f.rms * 0.010;

    // La transition, mesuree et non commandee. Tant que le fader est ferme, `blend`
    // vaut zero et rien ne change ; a mesure qu'il monte, la couleur glisse vers celle
    // de la face qui arrive, et le nouveau phenomene prend la main a mi-chemin.
    //
    // Il n'y a donc plus d'instant de bascule : le mur suit le geste, sur les huit ou
    // seize mesures que dure le fondu.
    this.blend = f.blend ?? 0;
    const c = this.next
      ? mixColor(this.color, this.next.color, this.blend)
      : this.color;

    // Au-dela de la moitie, c'est le phenomene de la nouvelle face qui s'affiche : le
    // morceau qui arrive est alors celui qu'on entend le plus.
    const kind = (this.next && this.blend > 0.5) ? this.next.kind : this.kind;
    const intensity = this.next
      ? this.intensity + (this.next.intensity - this.intensity) * this.blend
      : this.intensity;
    this.drawColor = c;
    this.drawIntensity = intensity;

    // Fond : jamais un noir pur, une teinte tres sombre de la famille. Le noir pur
    // fait ressortir la trame du videoprojecteur.
    ctx.fillStyle = `rgb(${c.r * 0.06 | 0}, ${c.g * 0.06 | 0}, ${c.b * 0.06 | 0})`;
    ctx.fillRect(0, 0, w, h);

    this.drawHarmony(f);

    switch (kind) {
      case 'Waves':   this.drawWaves(f);   break;
      case 'Thunder': this.drawThunder(f); break;
      default:        this.drawFigure(f);  break;
    }

    // Les clips passent par-dessus la geometrie, jamais dessous : c'est la forme qui
    // porte le rythme, l'image qui l'habille.
    this.clips.draw(ctx, w, h, f.rms);

    this.diag.push(f);
    this.diag.draw(ctx, w, h, f);
  }

  // ------------------------------------------------------- ce qui sonne
  // Le contenu tonal — piano, nappes, voix tenues — dessine une aureole large et lente
  // sous la geometrie. Sa teinte suit la note dominante, son ampleur la tonalite, et
  // elle enfle a chaque changement d'accord.
  //
  // Elle passe volontairement sous les percussions : l'harmonie porte, elle ne frappe
  // pas, et la mettre au-dessus reviendrait a lui donner la place du rythme.
  drawHarmony(f) {
    if (this.tonal < 0.04 && this.chord < 0.04) return;

    const { ctx, w, h } = this;
    const ha = f.harmony ?? {};
    const cx = w / 2, cy = h / 2;
    const unit = Math.min(w, h);

    // La note colore : on tourne d'un douzieme de tour par demi-ton autour de la
    // couleur de la famille, sans jamais la quitter tout a fait.
    const c = this.drawColor ?? this.color;
    const turn = this.pitch != null ? this.pitch / 12 : 0;
    const tint = rotate(c, turn * 0.55);

    const r = unit * (0.25 + this.tonal * 0.30 + this.chord * 0.12);
    const g = ctx.createRadialGradient(cx, cy, 0, cx, cy, r);
    const a = 0.12 + this.tonal * 0.22 + this.chord * 0.20;
    g.addColorStop(0, `rgba(${tint.r}, ${tint.g}, ${tint.b}, ${a})`);
    g.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, h);

    // Le profil de hauteurs en couronne fine : douze secteurs, un par demi-ton. C'est
    // discret a la projection mais cela rend l'accord lisible.
    const ch = ha.chroma;
    if (!ch || !ch.length) return;
    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate(-Math.PI / 2 + this.spin * 0.2);
    for (let i = 0; i < ch.length; i++) {
      const a0 = (i / ch.length) * TAU;
      const a1 = ((i + 0.7) / ch.length) * TAU;
      const rr = unit * (0.40 + ch[i] * 0.05);
      ctx.strokeStyle = `rgba(${tint.r}, ${tint.g}, ${tint.b}, ${0.06 + ch[i] * 0.30 * this.tonal})`;
      ctx.lineWidth = Math.max(1, unit * 0.004);
      ctx.beginPath();
      ctx.arc(0, 0, rr, a0, a1);
      ctx.stroke();
    }
    ctx.restore();
  }

  // ------------------------------------------------------------------ M-
  // Vagues : des crêtes qui traversent l'ecran, le relief vient des bandes graves.
  // Rien de percussif, le ressac ne frappe pas, il porte.
  drawWaves(f) {
    const { ctx, w, h } = this;
    const c = this.drawColor ?? this.color;
    const bands = f.bands || [];
    const rows = 7;

    ctx.lineWidth = Math.max(1.5, h * 0.0022);

    for (let r = 0; r < rows; r++) {
      const depth = r / (rows - 1);                 // 0 au fond, 1 devant
      const y0 = h * (0.30 + depth * 0.62);
      const amp = h * (0.030 + depth * 0.075) * (0.55 + f.rms);

      // Assez de cretes pour lire une mer, pas assez pour faire une grille : trois au
      // fond, huit devant. Une seule oscillation par ecran donnait une ligne molle.
      const lambda = w / (3 + depth * 5);
      const drift = this.swell * (0.35 + depth * 1.1);

      ctx.strokeStyle = `rgba(${c.r}, ${c.g}, ${c.b}, ${0.20 + depth * 0.65})`;
      ctx.beginPath();
      for (let x = 0; x <= w; x += 6) {
        const u = x / w;
        // Deux sinus de periodes differentes : une seule donnerait une onde de
        // manuel scolaire, pas une mer.
        const band = bands.length ? bands[Math.floor(u * (bands.length - 1))] : 0.3;
        const y = y0
          + Math.sin(x / lambda + drift) * amp
          + Math.sin(x / (lambda * 0.43) - drift * 1.7) * amp * 0.4
          - band * h * 0.03 * depth;

        x === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
  }

  // ------------------------------------------------------------------ M+
  // Orage : le noir domine, l'attaque déchire. La violence est dans le contraste,
  // pas dans le remplissage — un ecran sature ne laisse plus rien exploser.
  drawThunder(f) {
    const { ctx, w, h } = this;
    const c = this.drawColor ?? this.color;

    if (this.flash > 0.02) {
      // Nappe de lumiere, puis l'eclair par-dessus.
      ctx.fillStyle = `rgba(${c.r}, ${c.g}, ${c.b}, ${this.flash * 0.22 * (this.drawIntensity ?? this.intensity)})`;
      ctx.fillRect(0, 0, w, h);

      ctx.strokeStyle = `rgba(255, 255, 255, ${this.flash * 0.9})`;
      ctx.lineWidth = Math.max(1.5, h * 0.003 * this.flash);
      ctx.beginPath();

      let x = w * (0.28 + 0.44 * pseudo(Math.floor(this.swell * 3)));
      let y = 0;
      ctx.moveTo(x, y);
      while (y < h) {
        y += h * (0.06 + 0.05 * pseudo(y | 0));
        x += w * 0.055 * (pseudo((y | 0) * 7) - 0.5) * 2;
        ctx.lineTo(x, y);
      }
      ctx.stroke();
    }

    // Entre deux eclairs, la masse nuageuse respire sur les graves.
    const bands = f.bands || [];
    const low = bands.length ? (bands[0] + bands[1]) / 2 : f.rms;
    const r = Math.min(w, h) * (0.18 + low * 0.22);
    const g = ctx.createRadialGradient(w / 2, h * 0.42, 0, w / 2, h * 0.42, r);
    g.addColorStop(0, `rgba(${c.r}, ${c.g}, ${c.b}, ${0.30 + low * 0.35})`);
    g.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, h);
  }

  // ------------------------------------------------- figure commune, par defaut
  drawFigure(f) {
    const { ctx, w, h } = this;
    const cx = w / 2, cy = h / 2;
    const unit = Math.min(w, h) / 2;
    const c = this.drawColor ?? this.color;
    const bands = f.bands || [];

    // Couronne : une barre par bande.
    if (bands.length) {
      const inner = unit * 0.62;
      ctx.save();
      ctx.translate(cx, cy);
      ctx.rotate(this.spin * 0.5);
      ctx.lineCap = 'round';
      for (let i = 0; i < bands.length; i++) {
        const a = (i / bands.length) * TAU;
        const len = unit * 0.10 + bands[i] * unit * 0.26;
        ctx.strokeStyle = `rgba(${c.r}, ${c.g}, ${c.b}, ${0.25 + bands[i] * 0.7})`;
        ctx.lineWidth = Math.max(2, unit * 0.012);
        ctx.beginPath();
        ctx.moveTo(Math.cos(a) * inner, Math.sin(a) * inner);
        ctx.lineTo(Math.cos(a) * (inner + len), Math.sin(a) * (inner + len));
        ctx.stroke();
      }
      ctx.restore();
    }

    // Figure centrale : autant de branches que le chiffre Camelot.
    const r = unit * (0.24 + f.rms * 0.16 + this.shock * 0.05);
    const phase = f.phase ?? 0;

    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate(this.spin + phase * 0.35);
    ctx.beginPath();
    for (let i = 0; i <= this.sides; i++) {
      const a = (i / this.sides) * TAU - Math.PI / 2;
      const x = Math.cos(a) * r, y = Math.sin(a) * r;
      if (i === 0) { ctx.moveTo(x, y); continue; }
      if (this.round) {
        const prev = ((i - 1) / this.sides) * TAU - Math.PI / 2;
        const mid = (prev + a) / 2;
        ctx.quadraticCurveTo(Math.cos(mid) * r * 1.22, Math.sin(mid) * r * 1.22, x, y);
      } else {
        ctx.lineTo(x, y);
      }
    }
    ctx.closePath();
    ctx.fillStyle = `rgba(${c.r}, ${c.g}, ${c.b}, ${0.10 + f.rms * 0.20})`;
    ctx.fill();
    ctx.strokeStyle = `rgba(255, 255, 255, ${0.35 + this.shock * 0.5})`;
    ctx.lineWidth = Math.max(1.5, unit * 0.006);
    ctx.stroke();
    ctx.restore();

    // Onde de choc de l'attaque.
    if (this.shock > 0.02) {
      const rr = unit * (0.30 + (1 - this.shock) * 0.75);
      ctx.strokeStyle = `rgba(${c.r}, ${c.g}, ${c.b}, ${this.shock * 0.55})`;
      ctx.lineWidth = Math.max(2, unit * 0.02 * this.shock);
      ctx.beginPath();
      ctx.arc(cx, cy, rr, 0, TAU);
      ctx.stroke();
    }
  }
}

// Bruit deterministe : deux eclairs identiques pour une meme graine, ce qui permet
// de rejouer une sequence a l'identique quand on regle le rendu.
function pseudo(n) {
  const x = Math.sin(n * 12.9898) * 43758.5453;
  return x - Math.floor(x);
}

// Fondu entre deux couleurs de famille, au rythme du fader.
function mixColor(a, b, t) {
  const m = (x, y) => Math.round(x + (y - x) * t);
  return { r: m(a.r, b.r), g: m(a.g, b.g), b: m(a.b, b.b) };
}

// Rotation de teinte, en restant dans la famille : on melange vers la couleur
// complementaire sans jamais l'atteindre.
function rotate(c, t) {
  const m = (v, o) => Math.round(v + (o - v) * t);
  return { r: m(c.r, 255 - c.r), g: m(c.g, 255 - c.g), b: m(c.b, 255 - c.b) };
}

function hexToRgb(hex) {
  const m = /^#?([0-9a-f]{6})$/i.exec((hex || '').trim());
  if (!m) return null;
  const v = parseInt(m[1], 16);
  return { r: (v >> 16) & 255, g: (v >> 8) & 255, b: v & 255 };
}
