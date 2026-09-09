#!/usr/bin/env bash
# Tout ce qu'il faut pour regarder : le moteur, le GPU simule, la mesure.
#
#   ./outils/voir.sh [dossier-des-precalculs]
#
# Le moteur ecoute la sortie systeme : joue ce que tu veux avec ton lecteur habituel, il
# suivra. Ctrl-C ici arrete tout.
set -uo pipefail
cd "$(dirname "$0")/.."

PORT=5099
for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]] && kill "$pid" 2>/dev/null
done

echo "moteur — il ecoute $(pactl get-default-sink).monitor"
./run.sh pulse > /tmp/emotion-moteur.log 2>&1 &
MOTEUR=$!
trap 'kill $MOTEUR 2>/dev/null; kill 0 2>/dev/null' EXIT INT TERM

for _ in $(seq 1 60); do
  ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && break
  sleep 1
done
echo "fenetres — le GPU simule et la mesure"
python3 outils/fenetre.py &
python3 outils/fenetre_reference.py "$@" &
wait
