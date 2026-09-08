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
// UNE CASE PAR SOURCE, ET SIX CASES POUR SIX REGISTRES.
//
// Empiler les formes sur un axe unique les faisait se recouvrir quoi qu'on fasse, et six
// lignes dans une meme case ne se distinguent pas davantage : ce qu'on gagne en
// separation d'analyse se reperd au rendu. Chaque source a donc sa case, sa couleur et
// son caractere — trois separations valent mieux qu'une.
//
// RIEN N'EST NOMME PAR SON INSTRUMENT. Les cases s'appelaient PIANO, VOIX, AIGUES ; c'est
// une faute, parce que ce qui joue dans une bande change d'un disque a l'autre et
// qu'annoncer un piano la ou passe un saxophone est pire que ne rien annoncer. Une bande
// porte son numero, et c'est tout ce qu'on en sait.
//
//   ┌─────┬─────┬─────┐
//   │  1  │  2  │  3  │   six octaves, de 100 Hz a 6,4 kHz
//   ├─────┼─────┼─────┤
//   │  4  │  5  │  6  │
//   ├─────┼─────┼─────┤
//   │GRAVE│GRAIN│SPEC │
//   ├─────┴─────┴─────┤
//   │     FRAPPES     │
//   └─────────────────┘
const SRC = {
  kick:  { r: 244, g: 246, b: 244 },
  grave: { r: 53,  g: 208, b: 127 },
  hat:   { r: 150, g: 158, b: 155 },
  spec:  { r: 130, g: 138, b: 135 },
  cadre: { r: 110, g: 118, b: 115 },
};

// Six teintes distinctes, aucune rouge, chacune avec son caractere de remplissage.
const REG = [
  { c: { r: 53,  g: 208, b: 127 }, ch: '█' },
  { c: { r: 77,  g: 217, b: 232 }, ch: '▓' },
  { c: { r: 107, g: 168, b: 245 }, ch: '▒' },
  { c: { r: 167, g: 139, b: 232 }, ch: '▚' },
  { c: { r: 212, g: 139, b: 212 }, ch: '▞' },
  { c: { r: 232, g: 200, b: 77  }, ch: '░' },
];

const CASE = {
  r0: { x: 0.02,  y: 0.03, w: 0.31, h: 0.27, nom: '1' },
  r1: { x: 0.345, y: 0.03, w: 0.31, h: 0.27, nom: '2' },
  r2: { x: 0.67,  y: 0.03, w: 0.31, h: 0.27, nom: '3' },
  r3: { x: 0.02,  y: 0.32, w: 0.31, h: 0.27, nom: '4' },
  r4: { x: 0.345, y: 0.32, w: 0.31, h: 0.27, nom: '5' },
  r5: { x: 0.67,  y: 0.32, w: 0.31, h: 0.27, nom: '6' },
  grave: { x: 0.02,  y: 0.61, w: 0.31, h: 0.21, nom: 'GRAVE' },
  grain: { x: 0.345, y: 0.61, w: 0.31, h: 0.21, nom: 'GRAIN' },
  spec:  { x: 0.67,  y: 0.61, w: 0.31, h: 0.21, nom: 'SPECTRE' },
  bas:   { x: 0.02,  y: 0.84, w: 0.96, h: 0.13, nom: 'FRAPPES' },
};

const boite = (c, w, h) => ({
  x: c.x * w, y: c.y * h, w: c.w * w, h: c.h * h,
  cx: (c.x + c.w / 2) * w, cy: (c.y + c.h / 2) * h,
  u: Math.min(c.w * w, c.h * h), nom: c.nom,
});

// LE RENDU EST EN CARACTERES, ET C'EST UNE DECISION DE MESURE AUTANT QUE DE STYLE.
//
// Tant qu'il emploie des degrades et des arcs, un retard percu peut toujours venir de lui.
// Reduit a du texte monospace — une chaine par ligne, un remplissage par chaine — il
// devient trop rapide pour etre suspect, et ce qui reste se mesure ailleurs.
//
// C'est aussi ce qui prepare l'unite CUDA : un GPU qui affiche une grille de caracteres
// n'a rien a reimplementer. Il lit une matrice et l'affiche.
const BLOCS = [' ', '▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
const bloc = (v) => BLOCS[Math.max(0, Math.min(8, Math.round(v * 8)))];

// La grille d'une case retranche la place du titre et une marge basse, une bonne fois.
// C'est ce qui faisait deborder le grain, dont les lignes etaient comptees sur la hauteur
// entiere puis dessinees sous la legende.
function grille(b, cellules) {
  const cw = b.w / cellules, ch = cw * 1.9;
  const titre = Math.max(ch, b.h * 0.16), dispo = b.h - titre - ch * 0.4;
  return {
    cols: cellules, lignes: Math.max(1, Math.floor(dispo / ch)),
    cw, ch, x0: b.x + cw * 0.5, y0: b.y + titre,
  };
}

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

    // Six registres suivis a part. Trois confondaient un piano et un saxophone de la meme
    // octave : ils devenaient une seule grandeur, donc une seule forme, et tout ce que
    // l'oreille distingue entre eux disparaissait.
    this.regNiveau = Array.from({ length: 6 }, () => new Lue());
    this.regPos = Array.from({ length: 6 }, () => new Lue(0.5));
    this.regCoup = Array.from({ length: 6 }, () => new Pulse(0.5));

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

    // Les six registres : une attaque par bit, un niveau et un contour par bande.
    const hits = v.hits | 0;
    for (let r = 0; r < 6; r++) {
      if ((hits >> r) & 1) this.regCoup[r].fire();
      this.regNiveau[r].step(this.lerp.scalar(f => f.voices?.levels?.[r] ?? 0, now));
      this.regPos[r].step(this.lerp.scalar(f => f.voices?.pitches?.[r] ?? 0.5, now));
    }

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
                     this.bassHit, this.voiceHit, this.bellHit, ...this.regCoup])
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
    // Le filtre contracte et trouble la scene entiere sans deplacer les cases les unes
    // par rapport aux autres : elles restent lisibles meme fermees.
    const swell = 1 + this.tension.value * 0.08 + this.drop.value * 0.08;
    const shrink = (0.90 + open * 0.10) * swell;
    ctx.translate(w / 2, h / 2); ctx.scale(shrink, shrink); ctx.translate(-w / 2, -h / 2);

    this.drawTension(ctx, w, h);
    this.drawCadres(ctx, w, h, c);
    for (let r = 0; r < 6; r++) this.drawRegistre(ctx, boite(CASE['r' + r], w, h), r);
    this.drawGrave(ctx, boite(CASE.grave, w, h));
    this.drawGrain(ctx, boite(CASE.grain, w, h), coupe);
    this.drawSpectre(ctx, boite(CASE.spec, w, h), bands);
    this.drawFrappes(ctx, boite(CASE.bas, w, h));
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

    const g = ctx.createLinearGradient(0, h, 0, h * 0.4);
    g.addColorStop(0, S.rgba(SRC.grave, t * 0.20 + d * 0.26));
    g.addColorStop(1, S.rgba(SRC.grave, 0));
    ctx.fillStyle = g;
    ctx.fillRect(0, h * 0.4, w, h * 0.6);
  }

  // -------------------------------------------------------------- CADRES
  // Le bord de chaque case et son nom. Sans bord, des formes eparses paraissent flotter
  // au hasard plutot qu'occuper des places ; et sans nom, on ne sait pas ce qu'on regarde.
  drawCadres(ctx, w, h, c) {
    for (const k of Object.keys(CASE)) {
      const b = boite(CASE[k], w, h);
      const teinte = (k[0] === 'r' && k.length === 2) ? REG[+k[1]].c : c;
      ctx.strokeStyle = S.rgba(teinte, 0.20 + this.level.value * 0.10);
      ctx.lineWidth = 1;
      ctx.strokeRect(Math.round(b.x) + 0.5, Math.round(b.y) + 0.5,
                     Math.round(b.w), Math.round(b.h));
      this.police(ctx, Math.max(8, Math.min(13, b.w * 0.075)));
      ctx.fillStyle = S.rgba(teinte, 0.80);
      ctx.fillText(b.nom, b.x + 5, b.y + 4);
    }
  }

  police(ctx, taille) {
    ctx.font = `${Math.max(6, taille).toFixed(0)}px ui-monospace, "JetBrains Mono", Menlo, monospace`;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
  }

  // ------------------------------------------------------------ REGISTRE
  // Une octave, dans sa case, avec sa couleur et son caractere.
  //
  // Le dessin est le meme pour les six, pour qu'on les compare : la colonne monte avec le
  // niveau, sa position horizontale suit le contour melodique — ou l'instrument joue dans
  // sa bande — et une attaque l'ouvre en largeur.
  drawRegistre(ctx, b, r) {
    const niv = this.regNiveau[r].value;
    const pos = this.regPos[r].value;
    const frappe = this.regCoup[r].value;
    if (niv < 0.015 && frappe < 0.02) return;

    const G = grille(b, 13);
    this.police(ctx, G.cw * 1.55);

    const colonne = Math.round((G.cols - 1) * (0.10 + pos * 0.80));
    const hautes = Math.round(niv * G.lignes);
    const large = Math.max(0, Math.round(frappe * 2.5));

    ctx.fillStyle = S.rgba(REG[r].c, 0.25 + niv * 0.65 + frappe * 0.3);
    for (let l = 0; l < G.lignes; l++) {
      const depuisBas = G.lignes - 1 - l;
      if (depuisBas >= hautes && large === 0) continue;

      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const d = Math.abs(col - colonne);
        if (d === 0 && depuisBas < hautes) ligne += REG[r].ch;
        else if (d <= large && depuisBas < Math.max(1, hautes)) ligne += '│';
        else ligne += ' ';
      }
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // --------------------------------------------------------------- GRAVE
  // Un anneau de caracteres qui respire. La seule forme que Selim ait dite bonne.
  drawGrave(ctx, b) {
    const v = this.bass.value + this.bassHit.value * 0.5;
    const G = grille(b, 22);
    this.police(ctx, G.cw * 1.5);

    const cxg = (G.cols - 1) / 2, cyg = (G.lignes - 1) / 2;
    const r = (0.25 + v * 0.70) * Math.min(cxg, cyg);

    ctx.fillStyle = S.rgba(SRC.grave, 0.30 + v * 0.6);
    for (let l = 0; l < G.lignes; l++) {
      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const dx = (col - cxg) * (cyg / Math.max(1, cxg)), dy = l - cyg;
        const d = Math.sqrt(dx * dx + dy * dy);
        ligne += Math.abs(d - r) < 0.8 ? '●' : (Math.abs(d - r) < 1.6 ? '·' : ' ');
      }
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // --------------------------------------------------------------- GRAIN
  // Les charleys.
  //
  // Ils ne montraient rien : ils ne dependaient que d'une impulsion, laquelle retombe en
  // moins d'un sixieme de temps — invisible entre deux frappes. Le fond suit desormais les
  // registres aigus en continu, et l'impulsion ne fait que l'aviver.
  drawGrain(ctx, b, coupe) {
    const fond = (this.regNiveau[4].value + this.regNiveau[5].value) * 0.5;
    const v = Math.max(fond * 0.7, this.hat.value);
    if (v < 0.01 || coupe < 0.05) return;

    const G = grille(b, 20);
    this.police(ctx, G.cw * 1.5);
    ctx.fillStyle = S.rgba(SRC.hat, 0.25 + v * 0.7);

    for (let l = 0; l < G.lignes; l++) {
      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const i = l * G.cols + col, phase = (i * 0.618) % 1;
        const eclat = Math.max(0,
          1 - Math.abs(((this.hat.value * 0.7 + fond * 0.3 + phase) % 1) - 0.5) * 2.6);
        ligne += eclat > 0.62 ? '✳' : (eclat > 0.42 ? '·' : (eclat > 0.25 ? '˙' : ' '));
      }
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // ------------------------------------------------------------- SPECTRE
  // Douze jauges couchees, une par bande, remplies depuis la gauche.
  //
  // Les colonnes de blocs empilees etaient illisibles. C'est la lecture d'un mixeur, et
  // l'oeil y suit une bande sans effort.
  drawSpectre(ctx, b, bands) {
    const G = grille(b, 14);
    this.police(ctx, G.cw * 1.5);
    const parLigne = Math.max(1, Math.floor(G.lignes / 12));

    for (let i = 0; i < 12; i++) {
      const l = Math.min(G.lignes - 1, i * parLigne);
      const v = bands[i] ?? 0, n = Math.round(v * (G.cols - 2));
      let ligne = '';
      for (let col = 0; col < G.cols - 2; col++) ligne += col < n ? '▬' : '·';
      ctx.fillStyle = S.rgba(SRC.spec, 0.20 + v * 0.7);
      ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);
    }
  }

  // ------------------------------------------------------------- FRAPPES
  // Le kick s'ecarte du centre, les claps allument les bords, la tension s'ecrit dessous.
  drawFrappes(ctx, b) {
    const k = this.kick.value, cl = this.clap.value;
    const G = grille(b, 70);
    this.police(ctx, G.cw * 1.7);

    const l = Math.max(0, Math.floor(G.lignes / 2));
    const centre = (G.cols - 1) / 2, d = (1 - k) * centre;
    const bord = Math.round(cl * G.cols * 0.12);

    let ligne = '';
    for (let col = 0; col < G.cols; col++) {
      const dist = Math.abs(col - centre);
      if (k > 0.02 && Math.abs(dist - d) < 0.8 + this.onOne) ligne += '█';
      else if (cl > 0.02 && (col < bord || col >= G.cols - bord)) ligne += '▪';
      else if (k > 0.02 && this.onOne > 0.9 && dist < 0.7) ligne += '│';
      else ligne += '·';
    }
    ctx.fillStyle = S.rgba(SRC.kick, 0.14 + k * 0.8);
    ctx.fillText(ligne, G.x0, G.y0 + l * G.ch);

    if (this.tension.value > 0.03) {
      let t = '';
      const n = Math.round(this.tension.value * G.cols);
      for (let col = 0; col < G.cols; col++) t += col < n ? '▔' : ' ';
      ctx.fillStyle = S.rgba(SRC.grave, 0.25 + this.tension.value * 0.5);
      ctx.fillText(t, G.x0, G.y0 + Math.min(G.lignes - 1, l + 1) * G.ch);
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
