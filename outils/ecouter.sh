#!/usr/bin/env bash
# Entendre ce que chaque source entend : un WAV par source, et son temoin.
#
#   ./outils/ecouter.sh <morceau.wav> [bpm-de-la-fiche]
#
# Deux etapes, et la premiere n'ouvre aucun port : la sonde rejoue le morceau hors ligne et
# exporte les six profils spectraux que la separation a appris ; l'extraction refait sa
# propre transformee et repartit le spectre entre les six.
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
# DEUX FOIS, ET CE N'EST PAS DU GASPILLAGE. Le second apprentissage ne sert qu'a repondre a
# une question qui decide de tout ce que l'oreille pourra conclure : « la source 3 » designe-
# t-elle le meme objet d'une lecture a l'autre ? Quinze secondes pour le savoir.
echo "sonde — le moteur ecoute $NOM deux fois et rend ses six profils"
for f in profils profils-bis; do
  dotnet run -c Release --project tools/Emotion.Probe -- \
    "$WAV" 0 90 $FICHE profils="$SORTIE/$f.json" > /dev/null || exit 1
done

python3 outils/extraire.py "$SORTIE/profils.json" "$WAV" "$SORTIE" \
  "$SORTIE/profils-bis.json" || exit 1
echo
echo "  $SORTIE"
