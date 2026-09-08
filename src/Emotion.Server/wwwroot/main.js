// Renderer projete. Il ne decide de rien : il consomme les images du signal et le
// morceau en cours, et se contente de les mettre a l'ecran.
//
// Deux entrees, deux rythmes :
//   frame  ~60 par seconde, le son
//   track  a chaque changement de face, la base
//
// Le canvas 2D suffit tant que la geometrie reste simple ; le passage a WebGL se fera
// quand il y aura une carte graphique en face et de vrais snippets a composer.

import { Visual } from './visual.js';

const canvas = document.getElementById('stage');
const visual = new Visual(canvas);

// Expose le rendu a la console du navigateur. Sert au reglage : declencher un effet a
// la demande, figer une enveloppe, comparer deux valeurs sans attendre que la musique
// veuille bien les produire.
window.visual = visual;

// ---------------------------------------------------------------- bandeau

const hud = {
  root: document.getElementById('hud'),
  link: document.getElementById('link'),
  state: document.getElementById('state'),
  src: document.getElementById('src'),
  bpm: document.getElementById('bpm'),
  side: document.getElementById('side'),
  camelot: document.getElementById('camelot'),
  title: document.getElementById('title'),
  scene: document.getElementById('scene'),
  cued: document.getElementById('cued'),
  cuePret: document.getElementById('cuepret'),
  blend: document.getElementById('blend'),
  lock: document.getElementById('lock'),
};

function setLink(ok, label) {
  hud.link.style.background = ok ? '#34a853' : '#ea4335';
  hud.state.textContent = label;
}

// Bandeau rabattable, comme les commandes d'un lecteur video : il disparait quand on
// ne s'en sert pas, et revient au moindre mouvement.
//
// Il ne se rabat que dans le mode visuel : c'est le seul ou l'on regarde le rendu
// plutot que de le regler. En signaux ou en superposition on est aux manettes, et un
// bandeau qui s'eclipse ferait perdre l'etat au moment ou il sert.
let hideTimer = null;

function armAutoHide() {
  clearTimeout(hideTimer);
  hud.root.classList.remove('idle');
  document.body.classList.remove('idle');

  if (manualHide || visual.signals.mode !== 0) return;

  hideTimer = setTimeout(() => {
    hud.root.classList.add('idle');
    document.body.classList.add('idle');
  }, 2500);
}

// Le rabat est pilote par l'etat, et sur son propre minuteur plutot que dans la boucle
// de rendu. Deux raisons : quitter le mode visuel doit faire revenir le bandeau quel que
// soit le chemin emprunte pour changer de mode, et surtout la boucle d'animation est
// suspendue par le navigateur des que la fenetre n'est pas a l'ecran — un bandeau qui
// dependrait d'elle resterait fige dans cet etat.
setInterval(() => {
  if (visual.signals.mode !== 0) {
    hud.root.classList.remove('idle');
    document.body.classList.remove('idle');
  }
}, 200);

addEventListener('mousemove', armAutoHide);
addEventListener('mousedown', armAutoHide);
addEventListener('touchstart', armAutoHide, { passive: true });

let manualHide = false;

addEventListener('keydown', (e) => {
  const k = e.key.toLowerCase();
  if (k === 'h') { manualHide = !manualHide; hud.root.classList.toggle('off', manualHide); }
  if (k === 'd') visual.diag.toggle();
  if (k === 's') { visual.signals.toggle(); refreshModeButton(); armAutoHide(); }
  if (k === 'c') { visual.calibrate.toggle(); armAutoHide(); }
  if (k === 'f') {
    if (document.fullscreenElement) document.exitFullscreen();
    else document.documentElement.requestFullscreen();
  }
});

// Le bouton de mode : trois etats, pour verifier a l'oeil qu'un eclair tombe bien sur
// le trait du clap. C'est l'outil de controle en direct, pas un gadget.
const modeButton = document.getElementById('mode');

function refreshModeButton() {
  modeButton.textContent = visual.signals.label;
  modeButton.dataset.mode = visual.signals.mode;
}

modeButton.addEventListener('click', (e) => {
  e.stopPropagation();
  visual.signals.toggle();
  refreshModeButton();
  armAutoHide();
});
refreshModeButton();
armAutoHide();

// ---------------------------------------------------------------- liaison

const conn = new signalR.HubConnectionBuilder()
  .withUrl('hub')
  .withAutomaticReconnect()
  .configureLogging(signalR.LogLevel.Warning)
  .build();

// Le debit d'images est trop eleve pour redessiner sur reception : on garde la
// derniere connue et c'est requestAnimationFrame qui cadence le rendu. Le signal
// pousse, l'ecran tire.
let latest = null;

conn.on('frame', (f) => { latest = f; });

// L'etat des platines. Seul `playing` atteint le mur : `cued` est ce que le DJ cale au
// casque, et l'afficher reviendrait a montrer le beatmatch au public.
conn.on('deck', (d) => {
  visual.setTrack(d.playing);
  visual.setCued(d.cued);

  hud.side.textContent = d.playing.side || '—';
  hud.camelot.textContent = d.playing.camelot || '—';
  hud.title.textContent = d.playing.title || '—';
  hud.scene.textContent = d.playing.scene?.kind ?? '—';

  hud.cued.textContent = d.cued ? `${d.cued.side || '?'} · ${d.cued.title}` : '—';
  hud.cued.style.color = d.cued ? '#e8eaed' : '#5f6368';
});

conn.on('source', (name) => {
  hud.src.textContent = name;

  // Le retard annonce par la source alimente l'ecran de calage : c'est le chiffre a
  // regarder quand le visuel parait decale du son.
  const m = /retard (\d+) ms/.exec(name);
  visual.latencyMs = m ? Number(m[1]) : null;
});

// L'etat de preparation du disque cale au casque. Il ne touche jamais au rendu : c'est
// un tableau de bord pour le DJ, affiche sur son telephone et jamais projete.
conn.on('cue', (f) => {
  if (!f) return;
  const pret = f.bpm != null;
  hud.cuePret.textContent = pret ? `pret ${f.bpm.toFixed(1)} bpm` : 'analyse…';
  hud.cuePret.style.color = pret ? '#34a853' : '#9aa0a6';
});

conn.onreconnecting(() => setLink(false, 'reconnexion…'));
conn.onreconnected(() => setLink(true, 'en ligne'));
conn.onclose(() => setLink(false, 'coupe'));

conn.start()
  .then(() => setLink(true, 'en ligne'))
  .catch((e) => setLink(false, 'echec : ' + e.message));

// ---------------------------------------------------------------- boucle

function loop() {
  if (latest) {
    visual.draw(latest);
    // Le tempo est estime depuis le son : il est nul le temps que la detection accroche.
    // Le tempo est estime depuis le son : il est nul le temps que la detection accroche.
    hud.bpm.textContent = latest.bpm != null ? latest.bpm.toFixed(1) : '…';

    // La part du prepare deja passee dans le melange : mesuree, jamais commandee.
    const b = latest.blend ?? 0;
    hud.blend.textContent = b > 0.01 ? `${(b * 100).toFixed(0)}%` : '—';
    hud.blend.style.color = b > 0.5 ? '#e8eaed' : '#9aa0a6';

    // Verrouille : le visuel anticipe le temps au lieu de le subir. Non verrouille : il
    // reagit, donc avec le retard de toute la chaine. Savoir dans quel mode on est
    // change ce qu'on juge a l'oeil.
    const cl = visual.clock;
    hud.lock.textContent = cl.locked
      ? `verrouille ±${Math.abs(cl.errorMs).toFixed(0)} ms`
      : `accroche ${(cl.confidence * 100).toFixed(0)}%`;
    hud.lock.style.color = cl.locked ? '#34a853' : '#9aa0a6';
  }
  requestAnimationFrame(loop);
}
requestAnimationFrame(loop);
