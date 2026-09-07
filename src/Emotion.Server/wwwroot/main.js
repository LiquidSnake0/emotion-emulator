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
};

function setLink(ok, label) {
  hud.link.style.background = ok ? '#34a853' : '#ea4335';
  hud.state.textContent = label;
}

addEventListener('keydown', (e) => {
  const k = e.key.toLowerCase();
  if (k === 'h') hud.root.classList.toggle('off');
  if (k === 'd') visual.diag.toggle();
  if (k === 'f') {
    if (document.fullscreenElement) document.exitFullscreen();
    else document.documentElement.requestFullscreen();
  }
});

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

// L'etat des platines. Seul `playing` atteint le mur : `cued` est ce que Selim cale au
// casque, et l'afficher reviendrait a montrer le beatmatch au public.
conn.on('deck', (d) => {
  visual.setTrack(d.playing);

  hud.side.textContent = d.playing.side || '—';
  hud.camelot.textContent = d.playing.camelot || '—';
  hud.title.textContent = d.playing.title || '—';
  hud.scene.textContent = d.playing.scene?.kind ?? '—';

  hud.cued.textContent = d.cued ? `${d.cued.side || '?'} · ${d.cued.title}` : '—';
  hud.cued.style.color = d.cued ? '#e8eaed' : '#5f6368';
});

conn.on('source', (name) => { hud.src.textContent = name; });

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
  }
  requestAnimationFrame(loop);
}
requestAnimationFrame(loop);
