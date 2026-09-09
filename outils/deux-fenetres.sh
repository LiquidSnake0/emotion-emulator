#!/usr/bin/env bash
# Ouvre les deux fenetres : le GPU simule, et la mesure avec son selecteur de faces.
#
#   ./outils/deux-fenetres.sh [dossier-des-precalculs]
#
# Le moteur doit tourner a cote — ./run.sh pulse — car c'est lui qui ecrit l'anneau. Les
# deux fenetres ne font que lire /dev/shm ; seule la fiche remonte au moteur, en HTTP,
# quand on choisit une face dans la liste.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 outils/fenetre.py &
python3 outils/fenetre_reference.py "$@" &
wait
