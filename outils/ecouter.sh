#!/usr/bin/env bash
# Entendre ce que chaque source entend : un WAV par source.
#
#   ./outils/ecouter.sh <morceau.wav> [bpm-de-la-fiche]
#
# Deux etapes, et la premiere n'ouvre aucun port : la sonde rejoue le morceau hors ligne et
# exporte les gabarits que la separation a appris ; l'extraction refait sa propre
# transformee et repartit le spectre entre les sources.
#
# LE BPM DE LA FICHE COMPTE, et ce n'est pas un detail : sans lui le moteur cherche son tempo
# dans le vide — 43 % de justesse au lieu de 99 — et ce qu'il apprend des sources en depend.
# Le donner s'il est connu.
set -uo pipefail
cd "$(dirname "$0")/.."

WAV="${1:-}"
[[ -f "$WAV" ]] || { sed -n '2,12p' "$0" | sed 's/^# \?//'; exit 1; }
NOM=$(basename "$WAV" .wav)
SORTIE="${SORTIE:-$HOME/Documents/emotion-sources/$NOM}"
FICHE=""; [[ -n "${2:-}" ]] && FICHE="fiche=$2"

mkdir -p "$SORTIE"
echo "sonde — le moteur ecoute $NOM et rend ses gabarits"
dotnet run -c Release --project tools/Emotion.Probe -- \
  "$WAV" 0 90 $FICHE inline profils="$SORTIE/gabarits.json" > /dev/null || exit 1

python3 outils/extraire.py "$SORTIE/gabarits.json" "$WAV" "$SORTIE" || exit 1
echo
echo "  $SORTIE"
