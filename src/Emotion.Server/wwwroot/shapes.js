// Le vocabulaire geometrique.
//
// Une regle, et elle vaut plus que n'importe quel effet : <b>une source de son, une
// forme</b>. Si le xylophone dessine des triangles, il ne doit rien dessiner d'autre, et
// rien d'autre ne doit dessiner de triangles. Faute de quoi l'oeil ne peut rien
// apprendre, et le visuel redevient une bouillie qui reagit vaguement a la musique.
//
//   basse       cercle plein qui respire au centre
//   voix        anneau concentrique
//   xylophone   triangles disperses
//   kick        onde circulaire qui part du centre
//   clap        losange sur les cotes
//   charley     traits courts en haut
//   nouveaute   barre qui traverse
//
// Toutes les formes sont tracees en coordonnees absolues et sans etat : leur mouvement
// vient des ressorts et des impulsions, jamais d'ici.

const TAU = Math.PI * 2;

/** Cercle plein, avec un degre de flou pour les masses. */
export function disc(ctx, x, y, r, color, alpha, soft = 0) {
  if (alpha <= 0.002 || r <= 0) return;
  ctx.save();
  if (soft > 0) {
    const g = ctx.createRadialGradient(x, y, r * (1 - soft), x, y, r);
    g.addColorStop(0, rgba(color, alpha));
    g.addColorStop(1, rgba(color, 0));
    ctx.fillStyle = g;
  } else {
    ctx.fillStyle = rgba(color, alpha);
  }
  ctx.beginPath();
  ctx.arc(x, y, r, 0, TAU);
  ctx.fill();
  ctx.restore();
}

/** Anneau. L'epaisseur porte l'intensite, pas l'opacite : un trait fin reste net. */
export function ring(ctx, x, y, r, width, color, alpha) {
  if (alpha <= 0.002 || r <= 0 || width <= 0) return;
  ctx.strokeStyle = rgba(color, alpha);
  ctx.lineWidth = width;
  ctx.beginPath();
  ctx.arc(x, y, r, 0, TAU);
  ctx.stroke();
}

/** Polygone regulier a n cotes, plein ou en trait. */
export function polygon(ctx, x, y, r, sides, rotation, color, alpha, fill = false, width = 2) {
  if (alpha <= 0.002 || r <= 0) return;
  const n = Math.max(3, sides | 0);

  ctx.beginPath();
  for (let i = 0; i <= n; i++) {
    const a = rotation + (i / n) * TAU - Math.PI / 2;
    const px = x + Math.cos(a) * r;
    const py = y + Math.sin(a) * r;
    i === 0 ? ctx.moveTo(px, py) : ctx.lineTo(px, py);
  }
  ctx.closePath();

  if (fill) {
    ctx.fillStyle = rgba(color, alpha);
    ctx.fill();
  } else {
    ctx.strokeStyle = rgba(color, alpha);
    ctx.lineWidth = width;
    ctx.stroke();
  }
}

/** Triangle equilateral pointe en haut, tourne autour de son centre. */
export function triangle(ctx, x, y, r, rotation, color, alpha, fill = true) {
  polygon(ctx, x, y, r, 3, rotation, color, alpha, fill, Math.max(1, r * 0.14));
}

/** Losange : un carre pose sur sa pointe. Assez distinct d'un triangle de loin. */
export function diamond(ctx, x, y, r, color, alpha, fill = true) {
  polygon(ctx, x, y, r, 4, 0, color, alpha, fill, Math.max(1, r * 0.16));
}

/** Trait vertical court. Le vocabulaire des charleys. */
export function tick(ctx, x, y, half, width, color, alpha) {
  if (alpha <= 0.002) return;
  ctx.strokeStyle = rgba(color, alpha);
  ctx.lineWidth = width;
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(x, y - half);
  ctx.lineTo(x, y + half);
  ctx.stroke();
}

/** Bande verticale a bords fondus, qui traverse. Reserve a la nouveaute. */
export function sweepBand(ctx, x, w, h, width, color, alpha) {
  if (alpha <= 0.002) return;
  const g = ctx.createLinearGradient(x - width, 0, x + width, 0);
  g.addColorStop(0, rgba(color, 0));
  g.addColorStop(0.5, rgba(color, alpha));
  g.addColorStop(1, rgba(color, 0));
  ctx.fillStyle = g;
  ctx.fillRect(x - width, 0, width * 2, h);
}

/**
 * Suite deterministe pour placer des formes. Deux appels de meme rang donnent le meme
 * point : les triangles ne dansent donc pas au hasard d'une image a l'autre, ce qui
 * serait illisible. Suite de Weyl, qui repartit sans jamais se repeter.
 */
export function scatter(i, seed = 0) {
  const gx = 0.6180339887498949;
  const gy = 0.7548776662466927;
  return {
    x: ((i + 1) * gx + seed * 0.137) % 1,
    y: ((i + 1) * gy + seed * 0.271) % 1,
  };
}

export function rgba(c, a) {
  return `rgba(${c.r | 0}, ${c.g | 0}, ${c.b | 0}, ${a})`;
}

/** Melange deux couleurs. Sert au fondu entre deux faces. */
export function mix(a, b, t) {
  const m = (x, y) => x + (y - x) * t;
  return { r: m(a.r, b.r), g: m(a.g, b.g), b: m(a.b, b.b) };
}

/** Eclaircit vers le blanc sans jamais l'atteindre. */
export function lighten(c, t) {
  return mix(c, { r: 255, g: 255, b: 255 }, t);
}
