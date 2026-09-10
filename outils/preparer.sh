#!/usr/bin/env bash
# Le pre-calcul d'un morceau : la reference nommee, et les frappes du moteur.
#
#   ./outils/preparer.sh 5              la piste 5 de l'album
#   ./outils/preparer.sh passepartout   par un bout de son titre
#   ./outils/preparer.sh --tout         tout l'album (compter une heure)
#
# CE QUI SE CALCULE ICI NE FAIT PAS TOURNER LE MOTEUR, et c'est la regle a ne pas perdre de
# vue. Un set n'est pas determine : un bonus track tombe sans prevenir, et le temps reel doit
# marcher sans rien savoir de lui. Ces fichiers ne servent qu'a NOTER ce que le moteur trouve,
# sur les disques qu'on a pris le temps d'analyser.
#
# Deux pistes en sortent, et elles se repondent :
#
#   la BATTERIE telle qu'un algorithme exterieur l'entend   (Demucs, cinq minutes)
#   les FRAPPES telles que notre detecteur les retient      (la sonde, une minute)
#
# Basculer de l'une a l'autre dans la fenetre — touches 7 et 8 — fait entendre en dix secondes
# ce que le detecteur rate ou invente. Aucun chiffre ne dit ca aussi vite.
set -uo pipefail
cd "$(dirname "$0")/.."

ALBUM="${EMOTION_ALBUM:-$HOME/Downloads/Macroblank & slowerpace 音楽 - The Era of Information}"
CRATE="${EMOTION_CRATE:-$HOME/Documents/crate/src/data/seed.json}"
CACHE="${EMOTION_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/emotion-emulator}"
mkdir -p "$CACHE/stems"

preparer() {
  local piste="$1" titre="$2" bpm="$3"
  local wav="$CACHE/$(echo "$titre" | tr -c '[:alnum:]._-' '_').wav"
  [[ -f "$wav" ]] || ffmpeg -v error -y -i "$piste" -ac 1 -ar 48000 "$wav" || return 1
  echo "── $titre${bpm:+   fiche $bpm BPM}"
  python3 outils/reference.py "$wav" "$CACHE/stems" || echo "   (pas de reference)"
  python3 outils/frappe.py "$wav" "$CACHE/stems" "$bpm" || echo "   (pas de frappes)"
}

lister() {
  ALBUM="$ALBUM" CRATE="$CRATE" python3 - "$1" <<'PYEOF'
import glob, json, os, re, sys
voulu = (sys.argv[1] if len(sys.argv) > 1 else "").strip().lower()
pistes = sorted(glob.glob(os.path.join(os.environ["ALBUM"], "*.aiff")) +
                glob.glob(os.path.join(os.environ["ALBUM"], "*.wav")))
def decoupe(c):
    base = os.path.splitext(os.path.basename(c))[0]
    m = re.search(r"(\d{2})\s+(.+)$", base)
    return (int(m.group(1)), m.group(2)) if m else (0, base)
try:
    faces = json.load(open(os.environ["CRATE"], encoding="utf-8"))
except (OSError, ValueError):
    faces = []
def fiche(titre):
    for f in faces:
        if (f.get("title") or "").lower() == titre.lower():
            return f"{f['bpm']}"
    return ""
for p in pistes:
    rang, titre = decoupe(p)
    if voulu in ("", "--tout") or voulu == str(rang) or voulu in titre.lower():
        print(f"{p}\t{rang:02d} {titre}\t{fiche(titre)}")
PYEOF
}

CHOIX="${1:---tout}"
LIGNES=$(lister "$CHOIX")
[[ -z "$LIGNES" ]] && { echo "aucune piste ne correspond a « $CHOIX »" >&2; exit 1; }
while IFS=$'\t' read -r piste titre bpm; do
  [[ -n "$piste" ]] && preparer "$piste" "$titre" "$bpm"
done <<< "$LIGNES"
echo
echo "  $CACHE/stems"
