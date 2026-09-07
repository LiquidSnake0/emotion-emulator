// La couche visuelle nourrie par des fichiers : clips et images fixes, declenches au
// rythme et composes par-dessus la geometrie.
//
// Deux idees la tiennent :
//
// 1. Le depot ne contient aucune oeuvre. Le manifeste est versionne, les fichiers non.
//    La bibliotheque se debrouille donc d'un dossier vide : sans assets, elle ne fait
//    rien et le reste du visuel tourne quand meme.
//
// 2. Le declenchement vient des attaques, pas d'un minuteur. Un clip qui part sur le
//    kick est cale sur la musique meme si le disque est pitche.

export class ClipLibrary {
  constructor() {
    this.entries = [];   // { el, kinds, weight, blend, hold, every, kind:'clip'|'still' }
    this.active = [];    // { entry, until }  ce qui est visible en ce moment
    this.beatCount = 0;
    this.ready = false;
  }

  /**
   * Charge le manifeste et prepare les elements. Un manifeste absent ou vide n'est pas
   * une erreur : c'est l'etat normal tant que Selim n'a rien depose.
   */
  async load(url = 'assets/manifest.json') {
    let manifest;
    try {
      const res = await fetch(url);
      if (!res.ok) return;
      manifest = await res.json();
    } catch {
      return;                       // pas de dossier d'assets, on tourne sans
    }

    for (const c of manifest.clips ?? []) this.#add(c, 'clip');
    for (const s of manifest.stills ?? []) this.#add(s, 'still');
    this.ready = this.entries.length > 0;
  }

  #add(spec, kind) {
    if (!spec?.file) return;

    let el;
    if (kind === 'clip') {
      el = document.createElement('video');
      el.muted = true;              // le son vient de la table, jamais du navigateur
      el.playsInline = true;
      el.preload = 'auto';
    } else {
      el = new Image();
    }
    el.src = 'assets/' + spec.file;

    this.entries.push({
      el,
      kind,
      kinds: (spec.kinds ?? []).map((k) => String(k)),
      weight: Math.max(1, spec.weight ?? 1),
      blend: spec.blend ?? 'screen',
      hold: spec.hold ?? 240,
      every: Math.max(1, spec.every ?? 4),
    });
  }

  /**
   * Une attaque vient de tomber. Choisit peut-etre un visuel a declencher.
   *
   * @param sceneKind phenomene courant, pour ne piocher que ce qui lui correspond
   * @param intensity 0 a 1 : un M+ declenche plus souvent qu'un M-
   */
  onOnset(sceneKind, intensity) {
    if (!this.ready) return;
    this.beatCount++;

    const eligible = this.entries.filter(
      (e) => (e.kinds.length === 0 || e.kinds.includes(sceneKind)) &&
             this.beatCount % e.every === 0
    );
    if (!eligible.length) return;

    // L'intensite ne choisit pas le visuel, elle decide s'il part. Sur un M- la
    // moitie des occasions passe sans rien, ce qui laisse respirer.
    if (Math.random() > 0.35 + intensity * 0.6) return;

    const pick = weighted(eligible);
    if (!pick) return;

    if (pick.kind === 'clip') {
      try { pick.el.currentTime = 0; pick.el.play().catch(() => {}); } catch {}
    }

    this.active = this.active.filter((a) => a.entry !== pick);
    this.active.push({
      entry: pick,
      until: performance.now() + (pick.kind === 'clip' ? 4000 : pick.hold),
    });

    // Trois calques suffisent a entrelacer ; au-dela l'ecran devient une bouillie.
    while (this.active.length > 3) this.active.shift();
  }

  /** Compose ce qui est actif par-dessus le rendu geometrique. */
  draw(ctx, w, h, rms) {
    if (!this.active.length) return;
    const now = performance.now();
    this.active = this.active.filter((a) => a.until > now);

    for (const a of this.active) {
      const { entry, until } = a;
      const el = entry.el;

      // Le visuel s'efface sur sa fin plutot que de disparaitre d'un coup.
      const left = until - now;
      const fade = Math.min(1, left / 500);
      const alpha = fade * (0.35 + rms * 0.45);

      const ready = entry.kind === 'clip'
        ? el.readyState >= 2
        : el.complete && el.naturalWidth > 0;
      if (!ready) continue;

      ctx.save();
      ctx.globalCompositeOperation = entry.blend;
      ctx.globalAlpha = alpha;
      drawCover(ctx, el, w, h);
      ctx.restore();
    }
  }
}

/** Remplit l'ecran sans deformer : on recadre, on n'etire jamais. */
function drawCover(ctx, el, w, h) {
  const sw = el.videoWidth || el.naturalWidth || w;
  const sh = el.videoHeight || el.naturalHeight || h;
  const scale = Math.max(w / sw, h / sh);
  const dw = sw * scale, dh = sh * scale;
  ctx.drawImage(el, (w - dw) / 2, (h - dh) / 2, dw, dh);
}

function weighted(list) {
  let total = 0;
  for (const e of list) total += e.weight;
  let r = Math.random() * total;
  for (const e of list) {
    r -= e.weight;
    if (r <= 0) return e;
  }
  return list[list.length - 1];
}
