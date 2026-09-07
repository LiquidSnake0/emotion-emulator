// Ecran de calage, touche C.
//
// Sa raison d'etre tient a une remarque de Selim : « l'orbe au milieu est le seul truc
// bien cale, les eclairs c'est trop chelou ». Il avait raison, et la cause etait un
// retard de 128 ms. Mais il manquait aussi de quoi en juger : dans un visuel ou tout
// bouge en permanence, l'oeil ne sait pas dire ce qui est declenche par le son et ce
// qui derive tout seul.
//
// D'ou cet ecran : <b>tout est immobile, sauf ce que le son declenche.</b> Un cercle
// pour le kick, un carre pour le clap, un trait pour le charley. Rien d'autre a
// l'ecran. Si une forme s'allume en meme temps que la frappe s'entend, c'est cale ; si
// elle traine, ca se voit immediatement.
//
// C'est un instrument de mesure, pas un visuel.

export class Calibrate {
  constructor() {
    this.on = false;

    // Une enveloppe par registre. Elles retombent vite : une forme qui persiste
    // empeche de juger la suivante.
    this.kick = 0;
    this.clap = 0;
    this.hat = 0;

    // Battement du tempo detecte, pour comparer les frappes reelles a la grille.
    this.beatPhase = 0;
  }

  toggle() { this.on = !this.on; }

  draw(ctx, w, h, f, latencyMs) {
    if (!this.on) return;

    const hit = f.hits ?? {};
    if (hit.kick) this.kick = 1;
    if (hit.clap) this.clap = 1;
    if (hit.hat) this.hat = 1;

    // Retombee franche : en 120 ms la forme a disparu, donc deux frappes proches
    // restent distinctes a l'oeil.
    this.kick *= 0.86;
    this.clap *= 0.86;
    this.hat *= 0.80;

    ctx.save();
    ctx.globalCompositeOperation = 'source-over';
    ctx.globalAlpha = 1;
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, w, h);

    const cx = w / 2, cy = h / 2;
    const unit = Math.min(w, h);

    // KICK : un disque au centre. C'est la pulsation, on la met la ou l'oeil se pose.
    if (this.kick > 0.02) {
      ctx.fillStyle = `rgba(90, 160, 255, ${this.kick})`;
      ctx.beginPath();
      ctx.arc(cx, cy, unit * 0.16 * (0.6 + this.kick * 0.4), 0, Math.PI * 2);
      ctx.fill();
    }

    // CLAP : un carre franc a gauche. Forme et place differentes du kick, pour qu'on
    // ne puisse pas les confondre du coin de l'oeil.
    if (this.clap > 0.02) {
      const s = unit * 0.13;
      ctx.fillStyle = `rgba(255, 210, 60, ${this.clap})`;
      ctx.fillRect(cx - unit * 0.34 - s / 2, cy - s / 2, s, s);
    }

    // HAT : un trait fin a droite. Discret, parce qu'il tombe souvent.
    if (this.hat > 0.02) {
      ctx.strokeStyle = `rgba(230, 230, 230, ${this.hat})`;
      ctx.lineWidth = Math.max(2, unit * 0.006);
      ctx.beginPath();
      ctx.moveTo(cx + unit * 0.32, cy - unit * 0.06);
      ctx.lineTo(cx + unit * 0.32, cy + unit * 0.06);
      ctx.stroke();
    }

    // La grille du tempo, en bas : un curseur qui balaie une mesure. Les traits
    // marquent les quatre temps. Si les disques bleus tombent sur les traits, le tempo
    // est bon ; s'ils derivent, il ne l'est pas — et cela se voit sans rien mesurer.
    const gy = h - unit * 0.10;
    const gx = w * 0.2, gw = w * 0.6;

    ctx.strokeStyle = 'rgba(255,255,255,0.14)';
    ctx.lineWidth = 1;
    for (let i = 0; i <= 4; i++) {
      const x = gx + (i / 4) * gw;
      ctx.beginPath();
      ctx.moveTo(x, gy - 10);
      ctx.lineTo(x, gy + 10);
      ctx.stroke();
    }

    if (f.phase != null) {
      const x = gx + f.phase * gw;
      ctx.fillStyle = '#34a853';
      ctx.beginPath();
      ctx.arc(x, gy, 5, 0, Math.PI * 2);
      ctx.fill();
    }

    // Le chiffre qui compte : le retard annonce entre le son et l'image.
    ctx.font = '13px ui-monospace, Menlo, monospace';
    ctx.fillStyle = latencyMs > 40 ? '#ea4335' : '#34a853';
    ctx.fillText(`retard analyse ${latencyMs ?? '—'} ms`, 24, h - 24);

    ctx.fillStyle = '#5f6368';
    ctx.fillText('disque = kick · carre = clap · trait = charley · vert = grille du tempo',
                 24, h - 6);

    ctx.restore();
  }
}
