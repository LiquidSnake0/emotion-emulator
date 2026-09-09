#!/usr/bin/env bash
# Ouvre les deux fenetres : le rendu en direct, et ce que l'analyse prealable attend.
#
#   ./outils/deux-fenetres.sh reference/t08.json 63.5
#
# Le serveur doit tourner a cote (./run.sh pulse). Rien ici ne passe par le reseau : les
# deux fenetres lisent le meme anneau dans /dev/shm.
set -euo pipefail
cd "$(dirname "$0")/.."
[ $# -ge 1 ] || { echo "usage: $0 <rapport.json> [bpm-du-crate]"; exit 1; }
python3 outils/fenetre.py &
python3 outils/fenetre_reference.py "$@" &
wait
