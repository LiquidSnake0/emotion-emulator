#!/usr/bin/env bash
# Tout ce qu'il faut pour regarder : le moteur, et LA fenetre.
#
#   ./outils/voir.sh                      ecoute la sortie systeme
#   ./outils/voir.sh morceau.wav 90.92    rejoue un morceau, fiche comprise
#
# UNE SEULE FENETRE, ET LA SECONDE A ETE RETIREE. Elle confrontait le paquet du moteur a un
# rapport Python precalcule sur un fichier : en ecoute directe, les deux ne parlaient donc
# pas du meme instant, et elle ne pouvait rien dire. Elle reste sur le disque
# (`fenetre_reference.py`) pour les mesures hors ligne, ou elle a un sens.
#
# Ctrl-C ici arrete tout.
set -uo pipefail
cd "$(dirname "$0")/.."

PORT=5099
for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]] && kill "$pid" 2>/dev/null
done

if [[ -f "${1:-}" ]]; then
  NOM=$(basename "$1" .wav)
  echo "moteur — il rejoue $NOM${2:+, fiche $2 BPM}"
  ./run.sh fichier "$1" "${2:-}" > /tmp/emotion-moteur.log 2>&1 &
else
  NOM="direct"
  echo "moteur — il ecoute $(pactl get-default-sink).monitor"
  ./run.sh pulse > /tmp/emotion-moteur.log 2>&1 &
fi
MOTEUR=$!
trap 'kill $MOTEUR 2>/dev/null; kill 0 2>/dev/null' EXIT INT TERM

for _ in $(seq 1 60); do
  ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && break
  sleep 1
done
echo "fenetre — clic ou 1-6 pour isoler une source, espace pour marquer ce que tu entends"
python3 outils/fenetre.py "$NOM"
