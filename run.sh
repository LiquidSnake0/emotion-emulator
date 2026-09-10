#!/usr/bin/env bash
# Lance le serveur en liberant d'abord le port.
#
# Sans cela, un serveur laisse en vie fait echouer le suivant sur un
# AddressInUseException de soixante lignes, ou le motif reel — « il y en a deja un qui
# tourne » — n'apparait qu'a la troisieme. On a perdu trois allers-retours dessus.
#
#   ./run.sh                     signal fabrique
#   ./run.sh pulse               ecoute la sortie systeme
#   ./run.sh fichier x.wav [bpm] rejoue un morceau, sans carte son
#   ./run.sh <device>            ecoute un peripherique nomme
#
# LE MODE FICHIER EST CELUI QUI SERT A REGARDER. En pulse, une case eteinte peut vouloir
# dire deux choses — le moteur ne voit rien, ou il n'y a pas de son sur le monitor — et l'on
# ne sait pas laquelle. En rejeu, la question ne se pose plus : le son est dans le fichier.
set -euo pipefail
cd "$(dirname "$0")"

PORT=5099

# On ne tue que ce qui ecoute sur ce port ET qui est ce serveur : jamais un processus
# quelconque qui passerait par la.
for pid in $(ss -lptnH "sport = :$PORT" 2>/dev/null | grep -o 'pid=[0-9]*' | cut -d= -f2 | sort -u); do
  if [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == *Emotion.Server* ]]; then
    echo "port $PORT occupe par le serveur $pid — on l'arrete"
    kill "$pid" 2>/dev/null || true
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN || break
      sleep 0.3
    done
    ss -lptnH "sport = :$PORT" 2>/dev/null | grep -q LISTEN && kill -9 "$pid" 2>/dev/null || true
  else
    echo "port $PORT tenu par le processus $pid, qui n'est pas ce serveur — on n'y touche pas" >&2
    exit 1
  fi
done

case "${1:-mock}" in
  mock)  ;;
  pulse) export Signal__Source=pulse
         export Signal__Device="$(pactl get-default-sink).monitor" ;;
  fichier)
         [[ -f "${2:-}" ]] || { echo "usage : ./run.sh fichier <morceau.wav> [bpm]" >&2; exit 1; }
         export Signal__Source=fichier
         export Signal__Device="$2"
         # LA FICHE COMPTE : sans elle le moteur cherche son tempo dans le vide — 43 % de
         # justesse au lieu de 99 — et tout ce qu'on regarde ensuite en depend.
         [[ -n "${3:-}" ]] && export Signal__Bpm="$3" ;;
  *)     export Signal__Source=pulse
         export Signal__Device="$1" ;;
esac

echo "api sur :$PORT (crate)  ·  rendu : python3 outils/fenetre.py  ·  source : ${Signal__Device:-signal fabrique}"
exec dotnet run -c Release --project src/Emotion.Server
