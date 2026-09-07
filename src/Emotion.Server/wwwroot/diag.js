// Ecran de diagnostic, touche D.
//
// Sa raison d'etre : on ne regle pas ce qu'on ne voit pas. Quand un eclair part au
// mauvais moment, la seule question utile est « qu'est-ce qui a declenche ? », et elle
// n'a de reponse qu'en montrant la grandeur qui decide — le flux spectral — a cote du
// seuil qu'elle doit franchir.
//
// Ce n'est pas un joli graphique : c'est l'outil qui permet de choisir la marge du
// detecteur en connaissance de cause plutot qu'a tatons.

const HISTORY = 240;            // ~5 s a 47 images/s

export class Diagnostics {
  constructor() {
    this.on = false;
    this.flux = [];
    this.threshold = [];
    this.onsets = [];           // index dans l'historique ou une attaque est tombee
    this.lastOnsetAt = 0;
    this.gaps = [];             // ecarts entre attaques, en ms
    this.nKick = 0; this.nClap = 0; this.nHat = 0;
  }

  toggle() { this.on = !this.on; }

  push(f) {
    this.flux.push(f.flux ?? 0);
    this.threshold.push(f.threshold ?? 0);
    const h = f.hits ?? {};
    this.onsets.push(h.clap ? 2 : (h.kick ? 1 : 0));   // 1 kick, 2 clap
    if (h.kick) this.nKick++;
    if (h.clap) this.nClap++;
    if (h.hat) this.nHat++;

    if (f.onset) {
      if (this.lastOnsetAt) this.gaps.push(f.t - this.lastOnsetAt);
      this.lastOnsetAt = f.t;
      if (this.gaps.length > 16) this.gaps.shift();
    }

    while (this.flux.length > HISTORY) {
      this.flux.shift();
      this.threshold.shift();
      this.onsets.shift();
    }
  }

  draw(ctx, w, h, f) {
    if (!this.on) return;

    const pad = 16;
    const panelH = Math.min(260, h * 0.42);
    const y0 = h - panelH - pad;

    ctx.save();
    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = 1;

    // Fond opaque : superpose au visuel, un graphe translucide est illisible.
    ctx.fillStyle = 'rgba(0, 0, 0, 0.82)';
    ctx.fillRect(pad, y0, w - pad * 2, panelH);

    const gx = pad + 12;
    const gw = w - pad * 2 - 24;
    const gh = panelH - 96;
    const gy = y0 + 58;

    this.#drawCurves(ctx, gx, gy, gw, gh);
    this.#drawBands(ctx, gx, gy + gh + 10, gw, 22, f);
    this.#drawText(ctx, gx, y0 + 22, f);

    ctx.restore();
  }

  // Le flux en blanc, le seuil en vert, les attaques en traits verticaux. Une attaque
  // doit toujours coincider avec un franchissement : si ce n'est pas le cas, le
  // probleme est dans le detecteur et pas dans le rendu.
  #drawCurves(ctx, x, y, w, h) {
    ctx.strokeStyle = 'rgba(255,255,255,0.12)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x, y, w, h);

    const n = this.flux.length;
    if (n < 2) return;
    const step = w / (HISTORY - 1);

    for (let i = 0; i < n; i++) {
      if (!this.onsets[i]) continue;
      // Bleu pour le kick, jaune pour le clap : on voit d'un coup d'oeil quel
      // instrument a declenche quel effet.
      ctx.strokeStyle = this.onsets[i] === 2
        ? 'rgba(255, 210, 60, 0.65)'
        : 'rgba(90, 160, 255, 0.45)';
      ctx.beginPath();
      ctx.moveTo(x + i * step, y);
      ctx.lineTo(x + i * step, y + h);
      ctx.stroke();
    }

    const line = (data, color, width) => {
      ctx.strokeStyle = color;
      ctx.lineWidth = width;
      ctx.beginPath();
      for (let i = 0; i < n; i++) {
        const px = x + i * step;
        const py = y + h - Math.min(1, data[i]) * h;
        i === 0 ? ctx.moveTo(px, py) : ctx.lineTo(px, py);
      }
      ctx.stroke();
    };

    line(this.threshold, 'rgba(52, 168, 83, 0.9)', 1.5);
    line(this.flux, 'rgba(255,255,255,0.85)', 1.5);
  }

  // Les douze bandes, grave a gauche. Permet de voir dans quel registre l'energie
  // arrive, donc quel instrument declenche.
  #drawBands(ctx, x, y, w, h, f) {
    const bands = f?.bands ?? [];
    if (!bands.length) return;
    const bw = w / bands.length;

    for (let i = 0; i < bands.length; i++) {
      const v = bands[i];
      ctx.fillStyle = `rgba(120, 190, 255, ${0.25 + v * 0.7})`;
      ctx.fillRect(x + i * bw + 1, y + h - v * h, bw - 2, v * h);
    }
    ctx.strokeStyle = 'rgba(255,255,255,0.12)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x, y, w, h);
  }

  #drawText(ctx, x, y, f) {
    const med = this.#medianGap();
    const impliedBpm = med ? (60000 / med).toFixed(1) : '—';

    ctx.font = '12px ui-monospace, Menlo, monospace';
    ctx.fillStyle = '#e8eaed';
    ctx.fillText(
      `flux ${(f?.flux ?? 0).toFixed(2)}   seuil ${(f?.threshold ?? 0).toFixed(2)}   ` +
      `niveau ${(f?.rms ?? 0).toFixed(2)}   ` +
      `bpm annonce ${f?.bpm != null ? f.bpm.toFixed(1) : '…'}   ` +
      `bpm des ecarts ${impliedBpm}   ` +
      `ecart median ${med ? med + ' ms' : '—'}   ` +
      `kick ${this.nKick}  clap ${this.nClap}  hat ${this.nHat}`,
      x, y
    );

    ctx.fillStyle = '#9aa0a6';
    ctx.fillText('blanc = flux · vert = seuil · bleu = kick · jaune = clap · bas = bandes, grave a gauche',
                 x, y + 18);
  }

  // Mediane et non moyenne : une attaque manquee double un ecart, et la moyenne suivrait.
  #medianGap() {
    if (this.gaps.length < 4) return null;
    const s = [...this.gaps].sort((a, b) => a - b);
    return Math.round(s[Math.floor(s.length / 2)]);
  }
}
