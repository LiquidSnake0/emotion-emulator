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

// La premiere rangee commence sous le bandeau de reglage, qui occupe le haut de l'ecran.
// A 3 % elle passait dessous et ses trois titres — donc les trois jauges de maturite —
// etaient invisibles : la case avait l'air anonyme alors qu'elle ne l'etait pas.
const CASE = {
  r0: { x: 0.02,  y: 0.075, w: 0.31, h: 0.245, nom: '1' },
  r1: { x: 0.345, y: 0.075, w: 0.31, h: 0.245, nom: '2' },
  r2: { x: 0.67,  y: 0.075, w: 0.31, h: 0.245, nom: '3' },
  r3: { x: 0.02,  y: 0.335, w: 0.31, h: 0.245, nom: '4' },
  r4: { x: 0.345, y: 0.335, w: 0.31, h: 0.245, nom: '5' },
  r5: { x: 0.67,  y: 0.335, w: 0.31, h: 0.245, nom: '6' },
  grave: { x: 0.02,  y: 0.595, w: 0.31, h: 0.20, nom: 'GRAVE' },
  grain: { x: 0.345, y: 0.595, w: 0.31, h: 0.20, nom: 'GRAIN' },
  spec:  { x: 0.67,  y: 0.595, w: 0.31, h: 0.20, nom: 'SPECTRE' },
  bas:   { x: 0.02,  y: 0.815, w: 0.96, h: 0.125, nom: 'FRAPPES' },
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

// SIX FORMES DISTINCTES, ET NON SIX FOIS LA MEME.
//
// Les six registres partageaient un seul dessin — une colonne qui montait — avec pour
// seule difference leur couleur et leur caractere de remplissage. Six colonnes identiques
// ne se comparent pas : l'oeil doit lire une legende pour savoir laquelle il regarde,
// alors que six silhouettes se reconnaissent sans y penser. Le DJ l'avait dit sur la
// premiere version : « le delire pour les levres en ascii c'etait super, cercle pour la
// basse c'etait super aussi ».
//
// TOUT EST EN COORDONNEES NORMALISEES, ET C'EST CE QUI EMPECHE LE DEBORDEMENT.
//
// Chaque forme travaille dans un carre de -1 a 1, quelles que soient les dimensions
// reelles de la grille. Une forme de rayon r y tient par construction tant que r ne
// depasse pas 1, et le contour melodique ne deplace le centre que de ce qui reste libre.
// Les debordements repetes des cases 4, 5 et 6 venaient tous de dessins calcules en
// cellules, ou une meme constante donnait un motif tenu dans une case et deborde dans une
// autre.
// L'ordre par defaut, du grave a l'aigu. Il ne vaut que tant que la fiche n'a rien dit :
// des qu'elle parle, elle a le dernier mot. Le paquet transporte un octet par source, et
// c'est cet octet qui commande ici — le rang n'est qu'un repli.
const FORMES = ['anneau', 'onde', 'levres', 'losange', 'etoile', 'grain'];

// Ce que chaque forme occupe verticalement, en fraction de la demi-hauteur. Sert a lui
// reserver sa place avant de la deplacer : couper un motif qui deborde le mutile, lui
// laisser sa place le garde entier.
const EXTENSION = {
  anneau: 0.62, onde: 0.42, levres: 0.55, losange: 0.62, etoile: 0.66, grain: 1.0,
};

// Le caractere d'une forme a une distance donnee de son trait : plein, puis estompe.
const trait = (d, e, plein, pale) => d < e ? plein : (d < e * 2.2 ? pale : null);

function forme(ctx, b, nom, teinte, v, pos, frappe, tempo){
  const G=grille(b, 9);
  police(ctx, G.taille);
  const cxg=(G.cols-1)/2, cyg=(G.lignes-1)/2;
  const ratio=cyg/Math.max(1,cxg);
  const force=Math.min(1, v+frappe*0.6);

  // LA FORME RESERVE SA PLACE, LE DEPLACEMENT PREND CE QUI RESTE.
  //
  // C'etait l'inverse : la forme glissait librement, puis son rayon se calculait a part —
  // si bien qu'elle sortait des que les deux se cumulaient. Le decoupage l'empechait de
  // deborder, mais en la rognant, ce qui est pire : une forme coupee ne se reconnait plus.
  //
  // On calcule donc d'abord son encombrement vertical reel, puis on ne la fait glisser que
  // dans l'espace qui reste. Elle est entiere a tout instant, quel que soit le niveau et
  // quelle que soit la hauteur de ce qu'elle joue.
  const RMAX=Math.min(cxg*ratio, cyg)*0.55;
  const rayon = {
    anneau : (0.25+force*0.75)*RMAX + 1.7,
    onde   : RMAX*(0.20+force*0.80) + 1.3,
    levres : Math.max(0.35,(0.10+force*0.95)*RMAX)*1.20 + 0.4,
    losange: (0.30+force*0.70)*RMAX + 0.9,
    etoile : (0.30+force*0.70)*RMAX + 0.6,
    grain  : RMAX*1.00,
  }[nom] || RMAX;

  const libre=Math.max(0, cyg-rayon);
  const cy=cyg+(0.5-pos)*2*libre;

  ctx.fillStyle = S.rgba(teinte, 0.22 + force * 0.70);

  for(let l=0;l<G.lignes;l++){
    let ligne='';
    for(let c=0;c<G.cols;c++){
      const dx=(c-cxg)*ratio, dy=l-cy;
      let ch=' ';

      switch(nom){
        // ANNEAU — il respire et monte. Son rayon suit l'activation.
        case 'anneau': {
          const d=Math.sqrt(dx*dx+dy*dy);
          const r=(0.25+force*0.75)*RMAX;
          const ep=0.8+frappe*1.4;
          ch = Math.abs(d-r)<ep ? '●' : (Math.abs(d-r)<ep+0.9 ? '·' : ' ');
          break;
        }
        // ONDE — elle ondule, defile, et se cambre vers le haut quand ca monte.
        case 'onde': {
          const y=cy + Math.sin(c*0.62 - tempo*2.4)*RMAX*(0.20+force*0.80);
          const d=Math.abs(l-y);
          ch = d<0.55 ? '≈' : (d<1.3 ? '~' : ' ');
          break;
        }
        // BOUCHE — un ovale qui s'ouvre et se ferme.
        //
        // Deux rangees de blocs alignes ne font pas une bouche : elles font deux rangees
        // de blocs. Une bouche est un contour ferme, dont la hauteur varie et dont les
        // coins se rejoignent — c'est ce qui la rend reconnaissable a l'ouverture comme a
        // la fermeture.
        //
        // On trace donc le bord d'une ellipse : son demi-grand axe s'elargit quand la voix
        // monte, son demi-petit axe s'ouvre avec le niveau. Fermee, elle devient un trait ;
        // ouverte, un ovale franc. Les caracteres de filet en dessinent le contour.
        case 'levres': {
          const a=(0.55+pos*0.45)*cxg*ratio;            // largeur : elle s'elargit en montant
          const bb=Math.max(0.35,(0.10+force*0.95)*RMAX); // hauteur : elle s'ouvre au niveau
          const q=(dx*dx)/(a*a)+(dy*dy)/(bb*bb);
          if(q>1.35) break;

          // Le contour : les cellules dont la distance a l'ellipse est faible.
          const bord=Math.abs(Math.sqrt(q)-1)<0.28;
          if(!bord){ ch = q<1 ? (force>0.55?'·':' ') : ' '; break; }

          const pente=Math.abs(dx)/Math.max(0.001,a) > 0.72;
          if(pente) ch='│';
          else ch = dy<0 ? '─' : '─';
          if(Math.abs(dx)/a>0.45 && Math.abs(dy)/bb>0.45)
            ch = (dx<0) === (dy<0) ? (dy<0?'╭':'╰') : (dy<0?'╮':'╯');
          break;
        }
        // LOSANGE — il pulse et monte.
        case 'losange': {
          const d=Math.abs(dx)+Math.abs(dy);
          const r=(0.30+force*0.70)*RMAX;
          ch = Math.abs(d-r)<0.9 ? '⬥' : (d<r&&frappe>0.25 ? '⬦' : ' ');
          break;
        }
        // ETOILE — elle eclate ; ses branches tournent avec le contour.
        case 'etoile': {
          const d=Math.sqrt(dx*dx+dy*dy);
          const r=(0.30+force*0.70)*RMAX;
          if(d>r+0.6) break;
          const ang=Math.atan2(dy,dx)+pos*3.14;
          const branche=Math.abs(Math.cos(ang*3));
          ch = (branche>0.88 || d<0.9) ? (d<0.9?'⁕':'⁎') : ' ';
          break;
        }
        // GRAIN — il scintille, et se concentre a la hauteur du contour.
        case 'grain': {
          const i=l*G.cols+c, phase=(i*0.618)%1;
          const pres=Math.max(0,1-Math.abs(l-cy)/Math.max(1,rayon));
          const eclat=Math.max(0,1-Math.abs(((force+phase)%1)-0.5)*2.6)*pres;
          ch = eclat>0.55 ? '⁕' : (eclat>0.34 ? '·' : (eclat>0.18 ? '˙' : ' '));
          break;
        }
      }
      ligne+=ch;
    }
    ctx.fillText(ligne, G.x0, G.y0+l*G.ch);
  }
}


// La grille d'une case retranche la place du titre et une marge basse, une bonne fois.
// C'est ce qui faisait deborder le grain, dont les lignes etaient comptees sur la hauteur
// entiere puis dessinees sous la legende.
// LA GRILLE PART DES LIGNES, ET NON DES COLONNES.
//
// Elle faisait l'inverse : on fixait le nombre de colonnes, on en tirait la largeur d'une
// cellule, puis sa hauteur — et le nombre de lignes tombait de ce qui restait. Sur une
// case large et basse, il en restait deux. Impossible d'y dessiner un anneau ou un ovale :
// tout s'ecrasait en une barre, et les six sources se ressemblaient.
//
// On demande donc d'abord combien de lignes la forme reclame, on en deduit la hauteur
// d'une cellule, puis sa largeur — un caractere monospace etant environ deux fois plus haut
// que large. La police vaut ensuite huit dixiemes de l'interligne, ce qui laisse un glyphe
// entier dans sa cellule au lieu de le faire mordre sur la suivante. La matrice est centree
// dans sa case : le reste de largeur se partage des deux cotes.
function grille(b, lignesVoulues) {
  const titre = b.h * 0.22;
  const dispo = Math.max(8, b.h - titre - 3);
  const ch = dispo / lignesVoulues;
  const cw = ch * 0.52;
  const cols = Math.max(5, Math.floor((b.w - 4) / cw));
  return {
    cols, lignes: lignesVoulues, cw, ch,
    x0: b.x + Math.max(2, (b.w - cols * cw) / 2), y0: b.y + titre, taille: ch * 0.82,
  };
}

// ON DECOUPE, ON NE CALCULE PLUS.
//
// Trois fois des rayons, des marges et des hauteurs de glyphe ont ete verifies, et trois
// fois des formes sont sorties de leur case. La geometrie se demontre mal et se constate
// mal ; un decoupage, lui, ne discute pas : ce qui depasse n'est pas dessine, quelle que
// soit la cause. Les controles restent utiles pour comprendre, le decoupage garantit.
function police(ctx, taille) {
  ctx.font = `${Math.max(6, taille).toFixed(0)}px ui-monospace, "JetBrains Mono", Menlo, monospace`;
  ctx.textAlign = 'left';
  ctx.textBaseline = 'top';
}

function dansLaCase(ctx, b, trace) {
  ctx.save();
  ctx.beginPath();
  ctx.rect(b.x + 1, b.y + 1, b.w - 2, b.h - 2);
  ctx.clip();
  trace();
  ctx.restore();
}

// TOUTE LA PALETTE DOIT TENIR DANS LE MEME CHASSE, ET RIEN NE LE GARANTISSAIT.
//
// Le rendu dessine des lignes entieres d'un coup — `fillText(ligne, x0, y)` — et la
// grille compte les colonnes en supposant une avance constante. C'est vrai d'une fonte
// monospace, et faux des le premier glyphe qu'elle ne contient pas : le navigateur va le
// chercher dans une fonte de repli, avec l'avance de cette fonte-la.
//
// Ce n'est pas une hypothese. Mesure dans le navigateur, grille reglee sur 9,00 px :
//
//     ◆ ◇   18,00 px   le double
//     ✳ ✷   12,57 px
//
// La case GRAIN faisait 595 px et sa ligne en mesurait 804 : 213 px hors du cadre, chez
// le voisin. Et le glyphe suivant est decale d'autant, si bien que la forme entiere
// derive — ce qu'on prenait pour un effet de champ etait un defaut de fonte.
//
// Le decoupage empeche desormais de deborder, mais il masquerait le desalignement sans
// le dire. Ce controle, lui, le dit. Il tourne une fois au demarrage et ne coute rien.
const PALETTE = '~·˙…≈─│╭╮╯╰▁▂▃▄▅▆▇█░▒▓▔▚▞▪▬⬥⬦●⁕⁎ ';

export function verifierPalette(ctx) {
  ctx.font = '15px ui-monospace, "JetBrains Mono", Menlo, monospace';
  const ref = ctx.measureText('M').width;
  const hors = [...PALETTE].filter(c => Math.abs(ctx.measureText(c).width - ref) > 0.01);
  if (hors.length) {
    console.warn(`[visual] glyphes hors gabarit, la grille va deriver : ${hors.join(' ')} ` +
                 `(reference ${ref.toFixed(2)} px)`);
  }
  return hors;
}

export class Visual {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false });

    // Une fois, au demarrage : la fonte disponible tient-elle toute la palette ?
    verifierPalette(this.ctx);

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

    // CE QUE CHAQUE SOURCE SAIT D'ELLE-MEME, ET QUI ARRIVE PLUS TARD QUE LE RESTE.
    //
    // Le niveau et le contour sont justes des la premiere image ; l'empreinte, elle, se
    // forme sur quelques secondes de jeu effectif — plus longtemps encore pour une source
    // qui n'entre qu'au refrain. Chaque case avance donc a son rythme, et le mur montre
    // cette progression au lieu de tout afficher avec le meme aplomb.
    //
    // Sur un morceau du crate, les six murissent a 4,5 s, 7,0 s, 7,2 s et 15,8 s ; les
    // deux registres graves restent inconnus, partages entre le kick et la basse. Une
    // bande partagee qui refuse de se laisser nommer est le bon resultat, pas une panne.
    // Ce que la source sait d'elle-meme, en deux grandeurs qui ne disent pas la meme
    // chose. « Assez ecoutee » est une question de duree et se resout en quelques
    // secondes ; « nette » est une propriete du disque et ne bouge pas avec le temps.
    // Confondues dans un seul chiffre, elles donnaient une jauge qui trompait.
    this.regEcoute = new Array(6).fill(0);
    this.regNet = new Array(6).fill(0);
    this.regNom = new Array(6).fill(0);

    // La forme voulue, indice dans FORMES. Elle vient de la fiche et non du rang.
    this.regForme = Array.from({ length: 6 }, (_, r) => r);

    // LE TEMPO ANNONCE, ET L'ECART A CE QU'ON CROYAIT SAVOIR.
    //
    // Quand un disque a ete cale au casque, son tempo est deja connu et n'a pas a etre
    // recalcule. Mais un vinyle derive : le plateau bouge, le pitch glisse sous les
    // doigts. Un seul BPM d'ecart deplace la grille d'un sixieme de temps en seize temps
    // et d'un temps entier en une minute — c'est invisible en chiffres et flagrant a
    // l'ecran, donc c'est a l'ecran qu'il faut le montrer.
    this.annonce = new Pulse(4);       // le ping tient quatre temps
    this.annonceBpm = 0;
    this.derive = new Lue();

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

    // L'AVANCE SE CALCULE, ELLE NE SE DEVINE PAS.
    //
    // Elle doit valoir la somme de ce qui reste EN AVAL de l'analyse, puisque c'est
    // exactement ce qu'il s'agit d'annuler : la boucle d'affichage, l'unite de rendu et la
    // dalle ou le videoprojecteur. En amont, les 48 ms mesurees de la chaine d'analyse sont
    // deja comptees dans l'horloge, qui se cale sur les frappes telles qu'elles arrivent.
    //
    // CINQUANTE-QUATRE MILLISECONDES, ET LE CHIFFRE SE CALCULE.
    //
    // La chaine visee est celle du set : 48 ms d'analyse mesurees, une dizaine pour l'unite
    // de rendu aller-retour, seize pour un videoprojecteur de mapping en mode faible
    // latence. Soit 74 ms, dont il faut retrancher les 20 en deca desquels l'oeil cesse de
    // lier l'image au son — 54.
    //
    // Sur un simple ecran d'ordinateur, sans unite externe, la chaine est plus courte d'une
    // vingtaine de millisecondes et l'avance devient excessive : le visuel part alors trop
    // tot, ce qui se detecte a partir de 45 ms d'apres l'ITU. `?lead=30` remet le reglage
    // d'un ecran ordinaire.
    //
    // La borne n'est pas la perception mais la previsibilite du tempo. A 87 BPM un temps
    // dure 690 ms, donc 60 ms d'avance en representent 9 % : tant que le plateau tient a
    // 1 % pres, l'erreur de position reste sous la milliseconde. L'horloge refuse au-dela
    // de 40 % d'un temps, ou l'on ne predirait plus mais inventerait.
    const reglages = new URLSearchParams(location.search);

    // LE SON MET DU TEMPS A TRAVERSER LA SALLE, LA LUMIERE NON.
    //
    // C'est le poste le plus gros du budget, et il avait ete oublie parce qu'il n'est ni
    // dans le code ni dans la machine : il est dans la piece. Le son parcourt 343 metres par
    // seconde ; le public place a dix metres des enceintes l'entend donc <b>29 ms apres</b>
    // qu'il en soit sorti, alors qu'il voit le mur a l'instant meme.
    //
    // Le retard qui compte n'est pas celui du visuel par rapport au son qui sort de la
    // table, mais par rapport au son qui arrive aux oreilles. Ces 29 ms viennent donc en
    // deduction : la chaine mesuree a 48 ms devient 19 de retard percu, soit deja sous le
    // seuil des 20.
    //
    // Et l'avance doit en tenir compte, sans quoi elle ferait partir le visuel trop tot —
    // avec 54 ms d'avance a dix metres, le mur precederait le son de 35 ms, ce qui se
    // detecte aussi. `?salle=10` donne la distance moyenne du public aux enceintes.
    const salleM = Number(reglages.get('salle'));
    this.volSonMs = Number.isFinite(salleM) && salleM > 0
        ? Math.min(80, salleM / 343 * 1000)
        : 0;

    // L'ordre des sources : ce qui est demande dans l'adresse, puis ce qui a ete regle a
    // l'oeil la derniere fois, puis le calcul theorique. Une valeur reglee sur place vaut
    // toujours mieux qu'une valeur deduite, puisqu'elle englobe ce qu'on n'a pas mesure.
    const leadDemande = Number(reglages.get('lead'));
    let garde = NaN;
    try { garde = Number(localStorage.getItem('emotion.lead')); } catch { /* mode prive */ }

    this.leadMs = Number.isFinite(leadDemande) && leadDemande > 0
        ? Math.max(0, leadDemande - this.volSonMs)
        : Number.isFinite(garde) && garde > 0
            ? garde
            : Math.max(0, 54 - this.volSonMs);

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
    // LA FICHE CHOISIT LES FORMES, ET ELLE LE FAIT AU CHANGEMENT DE DISQUE.
    //
    // Un octet par source, transporte tel quel depuis la base. Le renderer ne decide de
    // rien : il lit. Une fiche muette laisse l'ordre par defaut, du grave a l'aigu, qui ne
    // garantit que la distinguabilite — ce n'est pas un choix musical.
    if (Array.isArray(t.shapes)) {
      for (let r = 0; r < 6; r++) {
        const v = t.shapes[r] | 0;
        this.regForme[r] = v >= 1 && v <= FORMES.length ? v - 1 : r;
      }
    }

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
    this.clock.step(dtMs, frame.bpm, this.leadMs);
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

      // Ni l'ecoute ni la nettete ne s'interpolent : elles montent deja lentement en
      // amont, et un lissage de plus ne ferait que retarder ce qu'elles annoncent.
      const lane = v.lanes?.[r];
      this.regEcoute[r] = lane?.heard ?? 0;
      this.regNet[r] = lane?.sharpness ?? 0;
      this.regNom[r] = v.labels?.[r] ?? 0;
    }

    // L'annonce de tempo : « on est a 87,9 », puis « 88,1 ». Elle ne dure qu'une image
    // cote analyse, on la tient quatre temps a l'ecran pour qu'elle soit lisible.
    if (frame.tempoAnnounce) {
      this.annonce.fire();
      this.annonceBpm = frame.announcedBpm ?? 0;
    }
    this.annonce.step(beatMs, dtMs);
    this.derive.step(frame.driftVisible ?? 0);

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
    // LA GRILLE NE SUIT PAS L'ENERGIE, ET C'EST UNE CORRECTION.
    //
    // L'ouverture du filtre pilotait l'echelle de la scene entiere. Mesure en direct :
    // le facteur oscillait entre 0,979 et 1,000 <b>a chaque image</b>, parce que
    // `openness` est un descripteur de timbre calcule toutes les 21 ms et qu'il n'etait
    // lisse par rien. Sur 1536 px de large, cela fait une trentaine de pixels de
    // battement sur les bords, en permanence, au rythme du morceau.
    //
    // Le defaut n'etait pas l'amplitude mais la nature du signal choisi. Les cases sont
    // une grille de lecture : l'oeil s'y ancre pour comparer une source a sa voisine, et
    // une grille qui respire empeche exactement cela. Ce qui a le droit de deplacer le
    // cadre, ce sont les gestes — une montee sur huit mesures, une rupture — pas une
    // mesure par fenetre. Le filtre, lui, agit toujours, mais <b>a l'interieur</b> des
    // cases, par `coupe`.
    const swell = 1 + this.tension.value * 0.08 + this.drop.value * 0.08;
    const shrink = swell;
    ctx.translate(w / 2, h / 2); ctx.scale(shrink, shrink); ctx.translate(-w / 2, -h / 2);

    this.drawTension(ctx, w, h);
    this.drawCadres(ctx, w, h, c);
    for (let r = 0; r < 6; r++) this.drawRegistre(ctx, boite(CASE['r' + r], w, h), r);
    // Chaque case decoupe la sienne. Les registres le faisaient deja ; ces quatre-la non,
    // et c'est par GRAIN que le debordement s'est vu.
    const bGrave = boite(CASE.grave, w, h);
    const bGrain = boite(CASE.grain, w, h);
    const bSpec = boite(CASE.spec, w, h);
    const bBas = boite(CASE.bas, w, h);
    dansLaCase(ctx, bGrave, () => this.drawGrave(ctx, bGrave));
    dansLaCase(ctx, bGrain, () => this.drawGrain(ctx, bGrain, coupe));
    dansLaCase(ctx, bSpec, () => this.drawSpectre(ctx, bSpec, bands));
    dansLaCase(ctx, bBas, () => this.drawFrappes(ctx, bBas));
    this.drawSweep(ctx, w, h, c);
    ctx.restore();

    this.clips.draw(ctx, w, h, this.level.value);

    // ---- ecrans de reglage, par-dessus tout ----
    this.diag.push(frame);
    this.diag.draw(ctx, w, h, frame);
    this.signals.push(frame);
    this.signals.draw(ctx, w, h, { ...frame, sceneName: this.kindName });
    this.calibrate.draw(ctx, w, h, frame, this.latencyMs, {
      avanceMs: this.leadMs,
      volSonMs: this.volSonMs,
      verrouille: this.clock.locked,
      fiabilite: this.clock.confidence,
    });
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
      police(ctx, Math.max(8, Math.min(13, b.w * 0.075)));
      ctx.fillStyle = S.rgba(teinte, 0.80);

      // LE TITRE D'UNE CASE DE REGISTRE PORTE SA MATURITE.
      //
      // Quatre crans qui se remplissent a mesure que la source se tient. Tant qu'ils sont
      // vides, ce qui joue dans cette bande n'est pas identifiable — souvent parce que
      // plusieurs instruments s'y relaient — et aucun nom ne doit y etre pose. Pleins, la
      // source est assez stable pour en porter un.
      //
      // AUCUN NOM N'EST INVENTE ICI. La case affiche un numero, et le nom seulement si la
      // fiche en a pose un : ce qui joue dans une bande change d'un disque a l'autre, et
      // annoncer un piano la ou passe un saxophone est pire que ne rien annoncer.
      if (k[0] === 'r' && k.length === 2) {
        const r = +k[1];
        const verdict = this.regEcoute[r] < 0.99 ? '…'
                      : this.regNet[r] > 0.6 ? '● nette'
                      : this.regNet[r] > 0.3 ? '◐ mêlée'
                                             : '○ partagée';
        const nom = this.regNom[r] ? `${b.nom}·${this.regNom[r]}` : b.nom;
        ctx.fillText(`${nom}   ${verdict}`, b.x + 5, b.y + 4);
      } else {
        ctx.fillText(b.nom, b.x + 5, b.y + 4);
      }
    }
  }

  // ------------------------------------------------------------ REGISTRE
  // Une source, dans sa case, avec sa couleur et SA FORME.
  //
  // La forme n'est plus deduite du rang : elle vient de la fiche du crate, qui la choisit
  // par morceau. L'analyse sait separer six sources et decrire chacune ; elle ne sait pas,
  // et n'a pas a savoir, laquelle merite une bouche — sur un morceau feutre c'est la voix
  // qu'on veut voir respirer, sur un morceau dense c'est la frappe.
  drawRegistre(ctx, b, r) {
    const niv = this.regNiveau[r].value;
    const pos = this.regPos[r].value;
    const frappe = this.regCoup[r].value;
    if (niv < 0.02 && frappe < 0.02) return;

    const nom = FORMES[this.regForme[r]] ?? FORMES[r % FORMES.length];
    dansLaCase(ctx, b,
      () => forme(ctx, b, nom, REG[r].c, niv, pos, frappe, this.spin));
  }

  // --------------------------------------------------------------- GRAVE
  // Un anneau de caracteres qui respire. La seule forme que le DJ ait dite bonne.
  drawGrave(ctx, b) {
    const v = this.bass.value + this.bassHit.value * 0.5;
    const G = grille(b, 7);
    police(ctx, G.taille);

    // Mesure en pixels, pour la meme raison que les registres : une normalisation par le
    // nombre de cellules donnait deux rangees de points au lieu d'un anneau.
    const cxg = (G.cols - 1) / 2, cyg = (G.lignes - 1) / 2;
    const unite = Math.max(1, Math.min(cxg * G.cw, cyg * G.ch));
    const r = (0.25 + v * 0.70);

    ctx.fillStyle = S.rgba(SRC.grave, 0.30 + v * 0.6);
    for (let l = 0; l < G.lignes; l++) {
      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const dx = (col - cxg) * G.cw / unite, dy = (l - cyg) * G.ch / unite;
        const d = Math.sqrt(dx * dx + dy * dy);
        ligne += Math.abs(d - r) < 0.16 ? '●' : (Math.abs(d - r) < 0.32 ? '·' : ' ');
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

    const G = grille(b, 7);
    police(ctx, G.taille);
    ctx.fillStyle = S.rgba(SRC.hat, 0.25 + v * 0.7);

    for (let l = 0; l < G.lignes; l++) {
      let ligne = '';
      for (let col = 0; col < G.cols; col++) {
        const i = l * G.cols + col, phase = (i * 0.618) % 1;
        const eclat = Math.max(0,
          1 - Math.abs(((this.hat.value * 0.7 + fond * 0.3 + phase) % 1) - 0.5) * 2.6);
        ligne += eclat > 0.62 ? '⁕' : (eclat > 0.42 ? '·' : (eclat > 0.25 ? '˙' : ' '));
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
    const G = grille(b, 12);
    police(ctx, G.taille);
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
    const G = grille(b, 3);
    police(ctx, G.taille);

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

    // L'ANNONCE DE TEMPO, LA OU LA GRILLE SE VOIT.
    //
    // Elle se pose sous les frappes, parce que c'est la que l'ecart se constate : le motif
    // du kick tombe a cote, et le chiffre dit pourquoi. La derive, elle, teinte l'annonce
    // — plus la grille a glisse, plus elle s'impose.
    const a = this.annonce.value;
    if (a > 0.02 && this.annonceBpm > 0) {
      const der = this.derive.value;
      ctx.fillStyle = S.rgba(SRC.grave, 0.25 + a * 0.55 + der * 0.20);
      ctx.fillText(`▸ ${this.annonceBpm.toFixed(1)} BPM`,
                   G.x0, G.y0 + Math.min(G.lignes - 1, l + 1) * G.ch);
    }

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
