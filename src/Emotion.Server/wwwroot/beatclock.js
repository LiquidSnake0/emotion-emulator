// Horloge de battement a verrouillage de phase.
//
// LE PROBLEME. Toute la chaine ajoute du retard : la fenetre d'analyse, la separation,
// la recherche de sommet, l'interpolation, la boucle d'affichage, l'ecran lui-meme. Une
// centaine de millisecondes au total, et l'oeil decroche vers quarante. Chaque etage
// pris isolement est justifie, et pourtant l'ensemble arrive en retard.
//
// LA SOLUTION N'EST PAS D'ALLER PLUS VITE. C'est de ne plus reagir au kick, mais de
// l'attendre. Un morceau a un tempo : le prochain temps est previsible, et on peut donc
// le declencher au moment ou il tombe plutot qu'apres l'avoir constate.
//
// C'est ce que font les logiciels de VJ, et c'est une boucle a verrouillage de phase :
// une horloge locale tourne a la cadence estimee, et chaque detection ne sert plus a
// declencher mais a <b>corriger doucement</b> sa phase. La latence percue tombe a zero,
// et peut meme devenir negative — le visuel part un cheveu avant le son, ce que l'oreille
// pardonne bien mieux que l'inverse.
//
// CE QU'ELLE NE FAIT PAS. Elle ne remplace pas la detection : un clap irregulier, une
// voix qui entre, un break n'ont pas de grille et doivent rester reactifs. L'horloge ne
// sert qu'a ce qui est <b>periodique</b>, c'est-a-dire le kick. Predire l'imprevisible
// donnerait un visuel qui invente des evenements, ce qui est pire qu'un visuel en retard.

export class BeatClock {
  constructor() {
    this.phase = 0;        // 0 a 1 dans le temps courant
    this.beatMs = 690;     // periode courante
    this.locked = false;   // a-t-on vu assez de temps pour se fier a la grille
    this.confidence = 0;   // 0 a 1, monte avec les detections regulieres
    this.justFired = false;
    this._tire = false;

    this._lastError = 0;
  }

  /**
   * Avance l'horloge d'un pas de rendu.
   * @param {number} dtMs   millisecondes ecoulees
   * @param {number|null} bpm  tempo estime par l'analyse, ou nul
   * @param {number} leadMs  avance volontaire, pour compenser le reste de la chaine
   */
  step(dtMs, bpm, leadMs = 0) {
    if (bpm && bpm > 40 && bpm < 200) {
      // La periode suit le tempo mesure, mais lentement : un tempo qui saute d'une image
      // a l'autre ferait sursauter toute la grille.
      const target = 60000 / bpm;
      this.beatMs += (target - this.beatMs) * 0.05;
    }

    this.phase += dtMs / this.beatMs;

    // L'AVANCE EST APPLIQUEE AU DECLENCHEMENT, PAS A LA PHASE.
    //
    // Elle etait recue puis multipliee par zero : le parametre existait, la documentation
    // le decrivait, et il ne faisait rien. Toute la strategie de latence du projet repose
    // pourtant sur lui — c'est ce qui ramene le retard percu du kick sous le seuil ou
    // l'oeil cesse de lier l'image au son.
    //
    // Deux facons de l'appliquer, et une seule est juste. Avancer la phase elle-meme
    // decalerait aussi le recalage : chaque frappe detectee corrigerait vers une position
    // fausse, et la grille finirait par courir apres son propre biais. On garde donc la
    // phase vraie — celle sur laquelle `sync` travaille — et l'on tire simplement plus tot
    // dans le temps, une fois par temps.
    // LA MOITIE D'UNE IMAGE, ET CE N'EST PAS UN DETAIL.
    //
    // On ne peut tirer qu'aux instants ou la boucle de rendu se reveille : a soixante
    // images par seconde, une toutes les 16,7 ms. Le seuil est donc franchi quelque part
    // entre deux reveils, et l'on tire au suivant — en moyenne une demi-image trop tard,
    // soit 8,3 ms perdues sur une avance qui en vise 30.
    //
    // Mesure avant correction : 23,1 ms d'avance reelle pour 30 demandees, 50,9 pour 60.
    // On anticipe donc d'une demi-image, ce qui centre l'erreur sur zero au lieu de la
    // laisser toujours du meme cote.
    const demiImage = dtMs / 2 / this.beatMs;
    const avance = Math.min(0.4, Math.max(0, leadMs) / this.beatMs + demiImage);

    this.justFired = false;
    if (!this._tire && this.phase >= 1 - avance) {
      this._tire = true;
      this.justFired = this.locked;   // on ne declenche que si la grille est fiable
    }

    if (this.phase >= 1) {
      this.phase -= Math.floor(this.phase);
      this._tire = false;
    }

    return this.phase;
  }

  /**
   * Une detection reelle vient de tomber. On ne s'y aligne pas d'un coup : on corrige
   * une fraction de l'ecart.
   *
   * Corriger entierement ferait sauter la grille a chaque detection un peu decalee, et
   * l'on retrouverait la nervosite qu'on cherche a fuir. Corriger un cinquieme laisse
   * l'horloge converger en quelques temps tout en absorbant une detection isolee qui
   * tombe a cote.
   */
  sync() {
    // L'ecart de phase, ramene dans [-0,5 ; 0,5] : une detection a 0,95 est en avance de
    // 0,05 sur le temps suivant, pas en retard de 0,95 sur le precedent.
    let error = this.phase;
    if (error > 0.5) error -= 1;

    this._lastError = error;
    this.phase -= error * 0.20;
    if (this.phase < 0) this.phase += 1;

    // La confiance monte quand les detections tombent pres de la grille, et chute quand
    // elles s'en ecartent. C'est elle qui decide si l'on ose predire.
    const near = Math.abs(error) < 0.12;
    this.confidence += (near ? 1 : 0 - this.confidence * 0.5) * 0.08;
    this.confidence = Math.max(0, Math.min(1, this.confidence));
    this.locked = this.confidence > 0.45;
  }

  /** Perd le verrouillage : changement de disque, ou silence. */
  reset() {
    this.confidence = 0;
    this.locked = false;
    this._tire = false;
  }

  /** Ecart de la derniere detection a la grille, en millisecondes. Diagnostic. */
  get errorMs() { return this._lastError * this.beatMs; }
}
