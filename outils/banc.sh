#!/usr/bin/env bash
# Banc de pulsation : passe les treize morceaux avec une option donnee et rend
# la force et la stabilite moyennes. Le seul juge non circulaire dont on dispose.
cd "$(dirname "$0")/.."

# LE BAC DE MESURE SE DONNE, IL NE SE DEVINE PAS. Un chemin de session fige ici ne marchait
# que sur une machine et un jour donnes, et n'a rien a faire dans un depot public.
S="${EMOTION_BAC:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator/bac}"
[[ -d "$S" ]] || { echo "bac introuvable : $S" >&2
                   echo "    EMOTION_BAC=/chemin/vers/les/wav ./outils/banc.sh <etiquette>" >&2
                   exit 1; }
ETQ="$1"; shift
D=$S/banc/$ETQ; mkdir -p $D
for f in macro live instamata; do
  dotnet run -c Release --no-build --project tools/Emotion.Probe -- \
    $S/$f.wav 0 90 "$@" instants=$D/$f > /dev/null 2>&1
done
for w in $S/valid/t*.wav; do
  n=$(basename "$w" .wav)
  dotnet run -c Release --no-build --project tools/Emotion.Probe -- \
    "$w" 0 90 "$@" instants=$D/$n > /dev/null 2>&1
done
dotnet run -c Release --no-build --project tools/Emotion.Pulse -- $D/*-kicks.txt 2>/dev/null \
  | grep -- "-kicks" \
  | awk -v e="$ETQ" '{f+=$2; gsub(/%/,"",$3); s+=$3; gsub(/%/,"",$4); c+=$4; n++} END {printf "%-14s force %.3f  stable %3.0f%%  couvre %3.0f%%  (%d pistes)\n", e, f/n, s/n, c/n, n}'
