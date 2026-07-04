#!/usr/bin/env bash
# Preflight guard smoke — run BEFORE a sweep/ladder to prove the server-side invariants a valid run
# depends on are live. Stands up one arena, plays a scripted 5-move game via two cookie jars, then
# asserts: terminal arena renders NO controls (no delete-btn / restart form / clickable square) and
# POST /delete + /restart return 409 (game-lock). Tears down. Read-only w.r.t. the repo.
#
#   bash experiments/oss-driver/preflight.sh          # expects: all guards PASS
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARENA="$REPO/experiments/oss-driver/arena.sh"
UP=/tmp/preflight.up.txt

CELL=0000 "$ARENA" up surface > "$UP" 2>&1            # blocks until ready; CELL=0000 = bare control
gpath="$(grep '^GAME_PATH=' "$UP" | cut -d= -f2)"
base="$(grep '^URL=' "$UP" | cut -d= -f2- | sed -E 's#(https?://[^/]+).*#\1#')"
[ -n "$gpath" ] && [ -n "$base" ] || { echo "FATAL: arena up failed"; cat "$UP"; exit 1; }
A="$base/$gpath"
echo "game=$gpath base=$base"

j1=/tmp/pf-j1; j2=/tmp/pf-j2; rm -f $j1 $j2
curl -s -c $j1 -b $j1 "$base/login" -o /dev/null; curl -s -c $j2 -b $j2 "$base/login" -o /dev/null
echo -n "scripted moves: "
curl -s -b $j1 -X POST "$A" -d 'player=X&position=TopLeft'      -o /dev/null -w 'X-TL:%{http_code} '
curl -s -b $j2 -X POST "$A" -d 'player=O&position=MiddleLeft'   -o /dev/null -w 'O-ML:%{http_code} '
curl -s -b $j1 -X POST "$A" -d 'player=X&position=TopCenter'    -o /dev/null -w 'X-TC:%{http_code} '
curl -s -b $j2 -X POST "$A" -d 'player=O&position=MiddleCenter' -o /dev/null -w 'O-MC:%{http_code} '
curl -s -b $j1 -X POST "$A" -d 'player=X&position=TopRight'     -o /dev/null -w 'X-TR:%{http_code}\n'  # X wins top row

curl -s -b $j1 "$A" -o /tmp/pf-term.html
del=$(curl -s -b $j1 -X POST "$A/delete"  -d '' -o /dev/null -w '%{http_code}')
res=$(curl -s -b $j1 -X POST "$A/restart" -d '' -o /dev/null -w '%{http_code}')
"$ARENA" down surface >/dev/null 2>&1

fail=0
# strip <style>/<script> first — CSS class definitions (.delete-game-btn {...}) are not live controls
ctl=$(perl -0777 -pe 's#<style.*?</style>##gs; s#<script.*?</script>##gs' /tmp/pf-term.html \
       | grep -oE 'delete-game-btn|/restart|square-clickable' | wc -l | tr -d ' ')
[ "$ctl" = 0 ] && echo "PASS terminal renders no controls" || { echo "FAIL terminal still has $ctl control(s)"; fail=1; }
[ "$del" = 409 ] && echo "PASS POST /delete -> 409" || { echo "FAIL POST /delete -> $del (want 409)"; fail=1; }
[ "$res" = 409 ] && echo "PASS POST /restart -> 409" || { echo "FAIL POST /restart -> $res (want 409)"; fail=1; }
[ "$fail" = 0 ] && echo "PREFLIGHT OK" || { echo "PREFLIGHT FAILED"; exit 1; }
