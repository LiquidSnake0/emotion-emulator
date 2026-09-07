// Le mouvement : comment une valeur du signal devient une valeur a l'ecran.
//
// Deux problemes distincts, et deux outils differents. Les confondre donne soit du
// saccade, soit de la bouillie.
//
// 1. LE SIGNAL ARRIVE MOINS VITE QUE L'ECRAN NE DESSINE.
//    47 images d'analyse par seconde contre 60 de rendu : une image de rendu sur cinq
//    n'a rien de neuf a montrer. Dessiner la derniere valeur connue produit des paliers
//    de 21 ms, qu'on voit comme un tremblement. On interpole donc entre les deux
//    dernieres images d'analyse, en fonction du temps reellement ecoule.
//
// 2. UNE VALEUR QUI SAUTE RESTE DURE MEME INTERPOLEE.
//    L'interpolation comble les trous mais ne lisse pas les a-coups : si le niveau passe
//    de 0,2 a 0,8 d'une image a l'autre, il le fera toujours, juste en cinq etapes au
//    lieu d'une. Pour ce qui doit paraitre physique — une masse qui gonfle, une forme
//    qui suit — il faut un ressort.

/**
 * Ressort critiquement amorti.
 *
 * Pourquoi celui-ci plutot qu'un lissage exponentiel : un lissage simple arrive toujours
 * en retard et n'a pas d'elan, si bien que tout paraît mou. Un ressort a une vitesse,
 * donc de l'inertie — il part vite, ralentit en approchant, et s'arrete sans osciller
 * puisqu'il est critiquement amorti. C'est le mouvement d'une masse reelle, et l'oeil le
 * reconnait immediatement comme naturel.
 *
 * Un seul reglage, la raideur : plus elle est haute, plus la forme colle au signal.
 */
export class Spring {
  /**
   * @param {number} stiffness  raideur. 8 est mou et flottant, 30 est vif, 60 quasi direct.
   * @param {number} value      valeur de depart.
   */
  constructor(stiffness = 18, value = 0) {
    this.k = stiffness;
    this.value = value;
    this.velocity = 0;
  }

  /** @param {number} target  valeur visee  @param {number} dt  secondes */
  step(target, dt) {
    // Amortissement critique : c = 2*sqrt(k). C'est la valeur exacte qui ramene la
    // masse au repos le plus vite possible sans jamais depasser la cible. En dessous
    // elle oscillerait, au-dessus elle trainerait.
    const c = 2 * Math.sqrt(this.k);
    const a = this.k * (target - this.value) - c * this.velocity;

    this.velocity += a * dt;
    this.value += this.velocity * dt;
    return this.value;
  }
}

/**
 * Enveloppe d'impulsion : monte d'un coup sur un evenement, retombe sur une fraction du
 * temps musical.
 *
 * La retombee suit le tempo et non une constante — sans quoi un effet meurt en 120 ms
 * quel que soit le morceau, laissant l'ecran eteint les quatre cinquiemes d'un temps a
 * 87 BPM. C'est ce qui rendait le visuel stroboscopique sur un morceau calme.
 */
export class Pulse {
  /** @param {number} partOfBeat  duree de vie, en fraction d'un temps. */
  constructor(partOfBeat = 0.5) {
    this.part = partOfBeat;
    this.value = 0;
  }

  fire() { this.value = 1; }

  /** @param {number} beatMs  duree d'un temps  @param {number} dtMs  millisecondes */
  step(beatMs, dtMs) {
    this.value *= Math.exp(-dtMs / (beatMs * this.part));
    if (this.value < 1e-4) this.value = 0;
    return this.value;
  }
}

/**
 * Garde les deux dernieres images d'analyse et rend leur interpolation a l'instant
 * courant.
 *
 * C'est ce qui supprime les paliers de 21 ms. On ne devine rien : on se place entre deux
 * mesures reelles, jamais au-dela de la plus recente — extrapoler inventerait du
 * mouvement qui n'existe pas dans le son, et cela se verrait au premier silence.
 */
export class FrameLerp {
  constructor() {
    this.prev = null;
    this.curr = null;
    this.prevAt = 0;
    this.currAt = 0;
  }

  /** Une nouvelle image d'analyse vient d'arriver. */
  push(frame, nowMs) {
    if (this.curr && frame.t === this.curr.t) return;   // deja vue
    this.prev = this.curr;
    this.prevAt = this.currAt;
    this.curr = frame;
    this.currAt = nowMs;
  }

  /**
   * Position entre les deux dernieres images, 0 a 1. Bornee a 1 : on n'extrapole jamais.
   */
  alpha(nowMs) {
    if (!this.prev || !this.curr) return 1;
    const span = this.currAt - this.prevAt;
    if (span <= 0) return 1;
    return Math.min(1, (nowMs - this.currAt) / span + 1);
  }

  /** Interpole un champ scalaire entre les deux images. */
  scalar(pick, nowMs) {
    if (!this.curr) return 0;
    if (!this.prev) return pick(this.curr) ?? 0;
    const a = this.alpha(nowMs);
    const p = pick(this.prev) ?? 0;
    const c = pick(this.curr) ?? 0;
    return p + (c - p) * a;
  }

  /** Interpole les douze bandes dans un tableau fourni, pour ne rien allouer. */
  bands(out, nowMs) {
    const c = this.curr?.bands;
    if (!c) return out;
    const p = this.prev?.bands;
    if (!p) { for (let i = 0; i < out.length; i++) out[i] = c[i] ?? 0; return out; }

    const a = this.alpha(nowMs);
    for (let i = 0; i < out.length; i++) out[i] = p[i] + (c[i] - p[i]) * a;
    return out;
  }
}
