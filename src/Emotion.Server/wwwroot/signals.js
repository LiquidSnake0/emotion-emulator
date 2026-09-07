// Ecran des signaux, touche S.
//
// Il ne montre pas un visuel : il montre **le message qui partira a l'unite de rendu**,
// champ par champ, tel qu'il sera encode dans GpuPacket. C'est la representation la
// plus parlante tant que le GPU n'est pas branche, parce qu'elle rend visible ce que
// l'autre cote recevra — et donc ce qu'il sera possible d'en faire.
//
// Chaque courbe porte le nom exact du champ du paquet, pour qu'il n'y ait aucune
// traduction mentale entre ce qu'on regarde ici et ce qu'on lira la-bas.

const HISTORY = 300;             // ~6 s a 47 images/s

// Les grandeurs continues, dans l'ordre du paquet. Chacune vit dans son couloir.
const LANES = [
  { key: 'rms',       label: 'Level',       color: '#e8eaed', get: f => f.rms ?? 0 },
  { key: 'blend',     label: 'Blend',       color: '#ff8a65', get: f => f.blend ?? 0 },
  { key: 'tonality',  label: 'Tonality',    color: '#ba68c8', get: f => f.harmony?.tonality ?? 0 },
  { key: 'chord',     label: 'ChordChange', color: '#f06292', get: f => f.harmony?.change ?? 0 },
  { key: 'novelty',   label: 'Novelty',     color: '#4dd0e1', get: f => f.novelty ?? 0 },
  { key: 'vlow',      label: 'Voice.Low',   color: '#8d6e63', get: f => f.voices?.low ?? 0 },
  { key: 'vmid',      label: 'Voice.Mid',   color: '#66bb6a', get: f => f.voices?.mid ?? 0 },
  { key: 'vhigh',     label: 'Voice.High',  color: '#fff176', get: f => f.voices?.high ?? 0 },
];

const NOTES = ['do','do#','re','mib','mi','fa','fa#','sol','sol#','la','sib','si'];

/// Trois etats plutot qu'un interrupteur : le mode superpose est celui qui sert
/// vraiment au reglage, puisqu'il permet de voir <b>en meme temps</b> le visuel et le
/// signal qui le pilote. C'est la seule facon de verifier a l'oeil qu'un eclair tombe
/// bien sur le trait jaune du clap.
export const MODE_OFF = 0;      // le visuel seul, ce que verra le public
export const MODE_OVER = 1;     // les deux : le signal en surimpression
export const MODE_FULL = 2;     // le signal seul, sur fond opaque

export class Signals {
  constructor() {
    this.mode = MODE_OFF;
    this.series = {};
    for (const l of LANES) this.series[l.key] = [];
    this.hits = [];              // masque de bits par image, comme le champ Hits
    this.seq = 0;
    this.lastT = -1;             // horodatage de la derniere image retenue
  }

  /// Passe a l'etat suivant du cycle.
  toggle() { this.mode = (this.mode + 1) % 3; }

  set(mode) { this.mode = mode; }

  get on() { return this.mode !== MODE_OFF; }

  get label() {
    return ['visuel', 'visuel + signaux', 'signaux'][this.mode];
  }

  push(f) {
    // L'ecran est cadence par l'affichage, a 60 Hz, alors que les images arrivent a
    // 47 Hz : sans ce filtre, une image sur cinq serait comptee deux fois et les
    // attaques apparaitraient doublees. Un ecran qui ment sur le debit est pire
    // qu'aucun ecran.
    if (f.t === this.lastT) return;
    this.lastT = f.t;

    this.seq++;
    for (const l of LANES) {
      const s = this.series[l.key];
      s.push(l.get(f));
      if (s.length > HISTORY) s.shift();
    }

    const h = f.hits ?? {};
    let bits = 0;
    if (h.kick) bits |= 1;
    if (h.clap) bits |= 2;
    if (h.hat) bits |= 4;
    if (f.noveltyOnset) bits |= 8;
    this.hits.push(bits);
    if (this.hits.length > HISTORY) this.hits.shift();
  }

  draw(ctx, w, h, f) {
    if (this.mode === MODE_OFF) return;

    ctx.save();
    ctx.globalCompositeOperation = 'source-over';

    // En surimpression, le fond reste assez transparent pour laisser voir le visuel
    // dessous, et les courbes assez opaques pour rester lisibles par-dessus.
    // Opaque en mode signaux : a 0,90 le visuel transparaissait encore, et les deux
    // modes finissaient par se ressembler. En superposition, au contraire, il doit
    // rester franchement visible dessous.
    const overlay = this.mode === MODE_OVER;
    ctx.globalAlpha = 1;
    ctx.fillStyle = overlay ? 'rgba(0, 0, 0, 0.42)' : '#000';
    ctx.fillRect(0, 0, w, h);

    const pad = 28;
    const top = 74;
    const laneH = Math.min(58, (h - top - 150) / LANES.length);
    const gw = w - pad * 2;

    this.#header(ctx, pad, 30, f);

    let y = top;
    for (const l of LANES) {
      this.#lane(ctx, pad, y, gw, laneH, l);
      y += laneH + 10;
    }

    this.#hits(ctx, pad, y + 4, gw, 44);
    this.#bands(ctx, pad, y + 62, gw, 54, f);
    this.#packet(ctx, pad, h - 26, f);

    ctx.restore();
  }

  // ------------------------------------------------------------------ entete
  #header(ctx, x, y, f) {
    ctx.font = '600 15px ui-monospace, Menlo, monospace';
    ctx.fillStyle = '#e8eaed';
    ctx.fillText('GpuPacket — ce qui part à l\'unité de rendu', x, y);

    ctx.font = '12px ui-monospace, Menlo, monospace';
    ctx.fillStyle = '#9aa0a6';
    ctx.fillText(
      `96 octets · seq ${this.seq} · ${(f?.t ?? 0)} ms · ` +
      `Bpm ${f?.bpm != null ? f.bpm.toFixed(1) : '0 (non accroché)'} · ` +
      `Phase ${f?.phase != null ? f.phase.toFixed(2) : '0'} · ` +
      `Pitch ${f?.harmony?.pitch != null ? NOTES[f.harmony.pitch] : '255 (aucune)'}`,
      x, y + 20);
  }

  // ---------------------------------------------------------------- couloirs
  // Une grandeur continue par couloir, avec son nom de champ et sa valeur courante.
  #lane(ctx, x, y, w, h, lane) {
    const data = this.series[lane.key];

    ctx.strokeStyle = 'rgba(255,255,255,0.07)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x, y, w, h);

    ctx.font = '11px ui-monospace, Menlo, monospace';
    ctx.fillStyle = lane.color;
    ctx.fillText(lane.label, x + 6, y + 14);

    const last = data.length ? data[data.length - 1] : 0;
    ctx.fillStyle = '#9aa0a6';
    ctx.fillText(last.toFixed(3), x + w - 46, y + 14);

    if (data.length < 2) return;

    // Aire sous la courbe : une ligne seule se perd sur un fond noir a la projection.
    const step = w / (HISTORY - 1);
    ctx.beginPath();
    ctx.moveTo(x, y + h);
    for (let i = 0; i < data.length; i++)
      ctx.lineTo(x + i * step, y + h - Math.min(1, data[i]) * (h - 4));
    ctx.lineTo(x + (data.length - 1) * step, y + h);
    ctx.closePath();
    ctx.fillStyle = lane.color + '22';
    ctx.fill();

    ctx.beginPath();
    for (let i = 0; i < data.length; i++) {
      const px = x + i * step;
      const py = y + h - Math.min(1, data[i]) * (h - 4);
      i === 0 ? ctx.moveTo(px, py) : ctx.lineTo(px, py);
    }
    ctx.strokeStyle = lane.color;
    ctx.lineWidth = 1.5;
    ctx.stroke();
  }

  // ------------------------------------------------------------------- Hits
  // Le champ Hits est un masque de bits : on le montre comme tel, trois pistes.
  #hits(ctx, x, y, w, h) {
    const rows = [
      { bit: 1, label: 'Hits.Kick', color: '#5aa0ff' },
      { bit: 2, label: 'Hits.Clap', color: '#ffd23c' },
      { bit: 4, label: 'Hits.Hat',  color: '#9aa0a6' },
      { bit: 8, label: 'Hits.Novelty', color: '#4dd0e1' },
    ];
    const rh = h / rows.length;
    const step = w / (HISTORY - 1);

    rows.forEach((r, ri) => {
      const ry = y + ri * rh;
      ctx.font = '11px ui-monospace, Menlo, monospace';
      ctx.fillStyle = r.color;
      ctx.fillText(r.label, x + 6, ry + 11);

      ctx.strokeStyle = r.color;
      ctx.lineWidth = 2;
      for (let i = 0; i < this.hits.length; i++) {
        if (!(this.hits[i] & r.bit)) continue;
        const px = x + i * step;
        ctx.beginPath();
        ctx.moveTo(px, ry + 2);
        ctx.lineTo(px, ry + rh - 4);
        ctx.stroke();
      }
    });

    ctx.strokeStyle = 'rgba(255,255,255,0.07)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x, y, w, h);
  }

  // ------------------------------------------------------------------ Bands
  #bands(ctx, x, y, w, h, f) {
    const bands = f?.bands ?? [];
    ctx.font = '11px ui-monospace, Menlo, monospace';
    ctx.fillStyle = '#78bcff';
    ctx.fillText('Bands[12] — grave à aigu', x + 6, y - 4);

    if (!bands.length) return;
    const bw = w / bands.length;
    for (let i = 0; i < bands.length; i++) {
      const v = bands[i];
      ctx.fillStyle = `rgba(120, 190, 255, ${0.2 + v * 0.75})`;
      ctx.fillRect(x + i * bw + 1, y + h - v * h, bw - 2, v * h);

      ctx.fillStyle = '#5f6368';
      ctx.fillText(String(i), x + i * bw + bw / 2 - 3, y + h + 12);
    }
    ctx.strokeStyle = 'rgba(255,255,255,0.07)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x, y, w, h);
  }

  // --------------------------------------------------------- octets du paquet
  // La ligne la plus utile au moment du branchement : la valeur exacte des champs
  // scalaires, dans l'ordre et le type ou le lecteur CUDA les trouvera.
  #packet(ctx, x, y, f) {
    const h = f?.hits ?? {};
    let bits = 0;
    if (h.kick) bits |= 1;
    if (h.clap) bits |= 2;
    if (h.hat) bits |= 4;

    ctx.font = '11px ui-monospace, Menlo, monospace';
    ctx.fillStyle = '#5f6368';
    ctx.fillText(
      `Magic 0x454D5531 · Hits 0b${bits.toString(2).padStart(3, '0')} · ` +
      `Scene ${f?.sceneName ?? '—'} · Blend ${(f?.blend ?? 0).toFixed(3)} · ` +
      `Level ${(f?.rms ?? 0).toFixed(3)} · Tonality ${(f?.harmony?.tonality ?? 0).toFixed(3)}`,
      x, y);
  }
}
