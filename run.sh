#!/usr/bin/env bash
# Lance le serveur en liberant d'abord le port.
#
# Sans cela, un serveur laisse en vie fait echouer le suivant sur un
# AddressInUseException de soixante lignes, ou le motif reel — « il y en a deja un qui
# tourne » — n'apparait qu'a la troisieme. On a perdu trois allers-retours dessus.
#
#   ./run.sh            signal fabrique
#   ./run.sh pulse      ecoute la sortie systeme
#   ./run.sh <device>   ecoute un peripherique nomme
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
  *)     export Signal__Source=pulse
         export Signal__Device="$1" ;;
esac

echo "http://localhost:$PORT  ·  source : ${Signal__Device:-signal fabrique}"
exec dotnet run -c Release --project src/Emotion.Server
