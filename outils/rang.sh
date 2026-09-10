#!/usr/bin/env bash
# Extrait les six sources de chaque morceau du bac, puis mesure ce qu'elles se partagent.
#
#   ./outils/rang.sh              tout ce qui est decode dans le cache
#   ./outils/rang.sh 05 08        seulement ces pistes
#
# POURQUOI UN SCRIPT ET NON UNE BOUCLE A LA MAIN.
#
# Les six sources dependent des profils de la SESSION qui les ecoute : elles n'existent que
# si le moteur tourne sur ce morceau-la. Mesurer l'album demande donc, pour chaque piste,
# d'ouvrir un moteur, d'attendre qu'il ait appris, d'extraire, puis de le fermer. Une
# quinzaine de minutes pour onze morceaux, et rien qui puisse se paralleliser : il n'y a
# qu'un anneau dans /dev/shm.
#
# CE QU'ON CHERCHE. Le rang effectif — combien d'objets la factorisation trouve vraiment sur
# les six qu'on lui demande. Deux morceaux mesures a la main donnaient 2,4 et 4,3, ce qui est
# trop different pour conclure. On regarde donc tout l'album avant de corriger quoi que ce
# soit.
set -uo pipefail
cd "$(dirname "$0")/.."

CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
CRATE="${EMOTION_CRATE:-$HOME/Documents/crate/src/data/seed.json}"
PORT=5099

libere() {
  for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
    kill "$pid" 2>/dev/null
  done
  sleep 1
}

fiche() {   # le tempo du crate, par le titre reconstruit depuis le nom du fichier
  CRATE="$CRATE" python3 - "$1" <<'PYEOF'
import json, os, re, sys
base = os.path.splitext(os.path.basename(sys.argv[1]))[0]
titre = re.sub(r"^\d+_", "", base).replace("_", " ").strip()
try:
    faces = json.load(open(os.environ["CRATE"], encoding="utf-8"))
except (OSError, ValueError):
    faces = []
for f in faces:
    t = (f.get("title") or "")
    # Le nom de fichier a remplace la ponctuation par des blancs : on compare sur les seules
    # lettres et chiffres, sans quoi « NeoAtlas (Intro Theme) » ne se retrouverait jamais.
    if re.sub(r"[^a-z0-9]", "", t.lower()) == re.sub(r"[^a-z0-9]", "", titre.lower()):
        print(f["bpm"]); break
PYEOF
}

CIBLES=()
for w in "$CACHE"/*.wav; do
  [[ -f "$w" ]] || continue
  if [[ $# -gt 0 ]]; then
    for motif in "$@"; do [[ "$(basename "$w")" == *"$motif"* ]] && CIBLES+=("$w"); done
  else
    CIBLES+=("$w")
  fi
done
[[ ${#CIBLES[@]} -eq 0 ]] && { echo "aucun morceau decode dans $CACHE" >&2; exit 1; }

dotnet build -c Release src/Emotion.Server > /dev/null 2>&1 || { echo "compilation cassee" >&2; exit 1; }

for w in "${CIBLES[@]}"; do
  base=$(basename "$w" .wav)
  bpm=$(fiche "$w")
  if compgen -G "$CACHE/stems/$base-6.wav" > /dev/null; then
    echo "── $base   pistes deja extraites"
    continue
  fi
  echo "── $base${bpm:+   fiche $bpm BPM}"
  libere
  # PAR `env`, ET NON PAR UN PREFIXE D'AFFECTATION.
  #
  # Bash ne reconnait comme affectation qu'un prefixe LITTERAL. Issu d'une expansion,
  # `${bpm:+Signal__Bpm=$bpm}` devient un mot ordinaire : bash tentait d'executer
  # « Signal__Bpm=84.74 » comme une commande, le moteur ne demarrait jamais, et stems.py
  # attendait quatre-vingt-dix secondes des profils que personne ne publiait.
  #
  # ET LA SORTIE NE VA PLUS DANS /dev/null. C'est elle qui aurait dit « command not found »
  # des le premier morceau ; la jeter a coute une mesure entiere lancee dans le vide.
  env Signal__Source=fichier Signal__Device="$w" ${bpm:+Signal__Bpm="$bpm"} \
    dotnet run -c Release --no-build --project src/Emotion.Server > "$CACHE/moteur-$base.log" 2>&1 &
  ouvert=""
  for _ in $(seq 1 60); do
    ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && { ouvert=oui; break; }
    sleep 1
  done
  if [[ -z "$ouvert" ]]; then
    echo "   le moteur n'a pas demarre : $(tail -2 "$CACHE/moteur-$base.log" | head -1)"
    continue
  fi
  python3 outils/stems.py "$w" "$CACHE/stems" 2>&1 | sed 's/^/   /'
  libere
done

echo
python3 outils/rang.py "$CACHE/stems"
