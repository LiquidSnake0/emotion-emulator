#!/usr/bin/env bash
# Chaque point d'entree demarre-t-il ? Rien de plus, et c'est deja ce qui manquait.
#
#   ./outils/fumee.sh
#
# POURQUOI CE SCRIPT EXISTE, ET IL A ETE PAYE CHER.
#
# Trois fois dans la meme journee, une fonctionnalite a ete annoncee prete et decouverte
# cassee par le DJ au premier lancement : le son qui ne sortait de nulle part, la selection
# qui coupait le melange, la fiche vide qui empechait le serveur de s'ouvrir. Les 187 tests
# etaient verts a chaque fois, et les rendus hors ecran aussi.
#
# La raison est simple et elle vaut d'etre ecrite : **on verifiait le code modifie, jamais la
# commande tapee.** Un defaut de cablage ne vit dans aucune unite ; il vit entre elles, a
# l'endroit exact ou un test unitaire ne regarde pas.
#
# Ce script ne teste aucune logique. Il tape ce que le DJ tape, et regarde si ca demarre.
#
# CE QU'IL NE FAIT PAS : Demucs (cinq minutes par morceau) et l'extraction des six sources.
# Ce sont des calculs, pas des cablages. On verifie qu'ils s'importent et se lancent, pas
# qu'ils finissent.
set -uo pipefail
cd "$(dirname "$0")/.."

PORT=5099
VERTS=0
ROUGES=0

libere() {
  for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
    kill "$pid" 2>/dev/null
  done
  sleep 1
}

note() {
  if [[ "$1" == "ok" ]]; then
    printf "  \033[32mok\033[0m    %s\n" "$2"; VERTS=$((VERTS + 1))
  else
    printf "  \033[31mCASSE\033[0m %s\n     %s\n" "$2" "$3"; ROUGES=$((ROUGES + 1))
  fi
}

# --- le moteur demarre-t-il, dans chacun de ses modes ---------------------------------
demarre() {
  local quoi="$1"; shift
  libere
  local log; log=$(mktemp)
  timeout 40 env "$@" dotnet run -c Release --no-build --project src/Emotion.Server \
    -- --sans-reseau > "$log" 2>&1
  if grep -qiE "Hosting failed|Unhandled exception" "$log"; then
    note ko "$quoi" "$(grep -iE 'Hosting failed|Unhandled' -A2 "$log" | sed -n 2p | cut -c1-100)"
  else
    note ok "$quoi"
  fi
  rm -f "$log"
}

echo "moteur"
# UN PROJET A LA FOIS : MSBuild n'accepte pas deux cibles sur la meme ligne, et le controle
# annoncait « compilation cassee » sur un projet qui compile tres bien.
for projet in src/Emotion.Server tools/Emotion.Probe; do
  if dotnet build -c Release "$projet" > /dev/null 2>&1; then
    note ok "compilation de $projet"
  else
    note ko "compilation de $projet" "voir dotnet build -c Release $projet"
  fi
done

demarre "signal fabrique"          Signal__Source=mock
# LA FICHE VIDE, ET C'EST ELLE QUI A CASSE. Un script qui ne connait pas le tempo ecrit une
# chaine vide ; le serveur refusait alors de s'ouvrir, pour une valeur facultative.
demarre "fiche vide"               Signal__Bpm=
demarre "fiche donnee"             Signal__Bpm=87.06
demarre "separation vide"          Signal__Separate=

if [[ -f "$HOME/.cache/emotion-emulator/05_Dead_Internet_Theory_.wav" ]]; then
  demarre "rejeu d'un fichier"     Signal__Source=fichier \
    Signal__Device="$HOME/.cache/emotion-emulator/05_Dead_Internet_Theory_.wav" Signal__Bpm=90.92
fi

# --- les outils s'importent-ils -------------------------------------------------------
echo
echo "outils"
for f in outils/*.py; do
  n=$(basename "$f" .py)
  err=$(cd outils && QT_QPA_PLATFORM=offscreen timeout 30 python3 -c "import $n" 2>&1 | tail -1)
  [[ -z "$err" ]] && note ok "import $n" || note ko "import $n" "$err"
done

# --- les reglages facultatifs peuvent-ils etre vides ou absurdes ----------------------
echo
echo "reglages facultatifs — vides ou absurdes, rien ne doit tomber"
for v in "EMOTION_AVANCE_MS=" "EMOTION_AVANCE_MS=abc"; do
  err=$(QT_QPA_PLATFORM=offscreen env "$v" timeout 25 python3 -c "
import sys; sys.path.insert(0, 'outils')
import fenetre
from PySide6.QtWidgets import QApplication
QApplication([]); fenetre.Mur()" 2>&1 | tail -1)
  [[ -z "$err" ]] && note ok "$v" || note ko "$v" "$err"
done
for v in "LISSAGE=" "RECOUVREMENT=x"; do
  err=$(env "$v" timeout 25 python3 -c "
import sys; sys.path.insert(0, 'outils')
import extraire" 2>&1 | tail -1)
  [[ -z "$err" ]] && note ok "$v" || note ko "$v" "$err"
done

# --- la fenetre rend-elle une image, sans lever ---------------------------------------
echo
echo "fenetre"
libere
Signal__Source=mock dotnet run -c Release --no-build --project src/Emotion.Server \
  -- --sans-reseau > /dev/null 2>&1 &
MOTEUR=$!
for _ in $(seq 1 40); do [[ -f /dev/shm/emotion-emulator ]] && break; sleep 1; done
sleep 3
err=$(QT_QPA_PLATFORM=offscreen timeout 40 python3 - <<'PYEOF' 2>&1 | tail -1
import sys, traceback
sys.path.insert(0, "outils")
import fenetre
from PySide6.QtWidgets import QApplication
from PySide6.QtGui import QImage
leve = []
vrai = fenetre.Mur.paintEvent
def garde(self, e):
    try: vrai(self, e)
    except Exception: leve.append(traceback.format_exc())
fenetre.Mur.paintEvent = garde
app = QApplication([])
m = fenetre.Mur(); m.anneau = fenetre.Anneau(); m.derniere_sequence = -1
m.resize(1180, 780)
img = QImage(1180, 780, QImage.Format.Format_RGB32)
for _ in range(8):
    m.battre(); m.render(img)
hors = fenetre.formes.verifier_chasse(m.mono)
if hors: print("glyphes hors gabarit :", hors)
elif leve: print(leve[0].strip().splitlines()[-1])
PYEOF
)
kill $MOTEUR 2>/dev/null
[[ -z "$err" ]] && note ok "elle rend une image sans lever" || note ko "le rendu" "$err"

libere
echo
printf "%d ok, %d casse(s)\n" "$VERTS" "$ROUGES"
exit $((ROUGES > 0))
