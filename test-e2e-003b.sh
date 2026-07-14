#!/usr/bin/env bash
# Acceptance spec for issue #9 (003b) — retire the Surface twin; fold the 16-cell
# A/C/Sd/So factorial surface into the PRIMARY TicTacToe.Web app.
#
# Thesis: ONE real app, toggle-able (ADR 001 §1). Today the factorial surface lives only
# in the experiments/src/TicTacToe.Web.Surface twin (route /arenas); the primary app
# (route /games) ignores TICTACTOE_CELL entirely. After #9 the primary app must render
# every cell 0000..1111, with each factor present/absent exactly per its bit.
#
# Cell bit order is MSB->LSB: A C Sd So  (spec 001 §4; e.g. 1010 = A=1 C=0 Sd=1 So=0).
#
# Factor markers on the primary app:
#   A  (affordances)   -> AFFORDANCE GATING, per the banked instrument (Surface game.fs:103-104):
#                           A=0: ALL 9 squares rendered as form-POST buttons (naive design;
#                                occupied/inactive carry HTML `disabled`, which an HTTP agent IGNORES
#                                -- it sees 9 equally submittable forms, including illegal ones).
#                           A=1: ONLY the caller's currently-legal moves rendered as forms.
#                        This only discriminates from a NON-ACTIVE viewer's page: on an empty board the
#                        active player has 9 legal moves, so A=0 and A=1 both render 9 forms. We
#                        therefore assert against the SECOND (non-active) player's view:
#                           A=0 -> 9 POST forms;  A=1 -> 0 POST forms (they have no legal move).
#                        Do NOT redefine A as forms present/absent -- that is a DIFFERENT instrument
#                        and breaks comparability with every banked ttt result.
#   C  (accessibility) -> the game page carries aria-*/role= structure
#   Sd (semantic disc) -> GET /profile is 200 (ALPS); 404 when off
#   So (ontology)      -> game page response carries Link rel="describedby"; absent when off
#
# Free (no LLM). RED until the surface is ported.
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; cd "$REPO"

PROJ="src/TicTacToe.Web"
PORT="${PORT:-5399}"
BASE="http://127.0.0.1:$PORT"
SURFACE_DIR="experiments/src/TicTacToe.Web.Surface"

pass=0; fail=0
ok(){ echo "  PASS: $1"; pass=$((pass+1)); }
no(){ echo "  FAIL: $1"; fail=$((fail+1)); }
hdr(){ echo; echo "=== $1 ==="; }

cleanup(){ for p in $(lsof -ti "tcp:$PORT" 2>/dev/null); do kill -9 "$p" 2>/dev/null; done; }
trap cleanup EXIT

# Launch the primary app on one cell; echo the game id (empty on failure).
start_cell(){
  cleanup; sleep 0.3
  TICTACTOE_CELL="$1" TICTACTOE_LOCK_GAME=1 TICTACTOE_INITIAL_GAMES=1 TICTACTOE_MAX_GAMES=1 \
    dotnet run --project "$PROJ" --no-build --urls "$BASE" >/tmp/003b-server.log 2>&1 &
  for _ in $(seq 1 60); do curl -fsS "$BASE/login" >/dev/null 2>&1 && break; sleep 0.5; done
}

# ---------------------------------------------------------------- AC2: twin deleted
hdr "AC2  Surface twin deleted; solution builds"
if [ -d "$SURFACE_DIR" ]; then
  no "Surface twin still present at $SURFACE_DIR (must be deleted)"
else
  ok "Surface twin deleted"
fi
if dotnet build TicTacToe.sln >/tmp/003b-build.log 2>&1; then ok "solution builds"; else no "solution build failed (/tmp/003b-build.log)"; fi

# ---------------------------------------------------------------- AC1: 16 cells
hdr "AC1  primary app renders all 16 cells; each factor present/absent per bit"
if ! dotnet build "$PROJ" >/tmp/003b-buildweb.log 2>&1; then
  no "primary app does not build — cannot test cells"
else
  for a in 0 1; do for c in 0 1; do for sd in 0 1; do for so in 0 1; do
    cell="$a$c$sd$so"
    start_cell "$cell"
    jar=$(mktemp); curl -sc "$jar" -b "$jar" "$BASE/login" >/dev/null 2>&1
    home=$(curl -s -b "$jar" -c "$jar" "$BASE/" 2>/dev/null)
    gid=$(echo "$home" | grep -oE 'games/[0-9a-f-]{36}' | head -1 | cut -d/ -f2)
    if [ -z "$gid" ]; then no "cell $cell: no game discovered (server up?)"; rm -f "$jar"; continue; fi
    page=$(curl -s -b "$jar" -c "$jar" -D /tmp/003b-hdrs "$BASE/games/$gid" 2>/dev/null)

    # observe each factor.
    # A = GATING, judged from a NON-ACTIVE viewer.
    # Two traps (both hit on the first cut of this spec):
    #  (a) count MATCHES not LINES — the board renders on one line, so `grep -c` is always 1.
    #  (b) self-seat (resolveViewer/callerRole) makes ANY unseated visitor the claimable
    #      current-turn player, so on a FRESH board a 2nd jar sees 9 forms even at A=1.
    #      A viewer is only non-active once BOTH seats are taken. So: seat X, seat O, then
    #      judge from a 3rd (spectator) jar.
    #   ungated (A=0) -> spectator still gets a form on all 9 squares
    #   gated   (A=1) -> spectator gets none
    jarX=$(mktemp); jarO=$(mktemp); jarS=$(mktemp)
    curl -sc "$jarX" -b "$jarX" "$BASE/login" >/dev/null 2>&1
    curl -s -b "$jarX" -c "$jarX" -X POST "$BASE/games/$gid" -d "player=X&position=TopLeft"    >/dev/null 2>&1
    curl -sc "$jarO" -b "$jarO" "$BASE/login" >/dev/null 2>&1
    curl -s -b "$jarO" -c "$jarO" -X POST "$BASE/games/$gid" -d "player=O&position=TopMiddle" >/dev/null 2>&1
    curl -sc "$jarS" -b "$jarS" "$BASE/login" >/dev/null 2>&1
    pageS=$(curl -s -b "$jarS" -c "$jarS" "$BASE/games/$gid" 2>/dev/null)
    nforms=$(echo "$pageS" | grep -oiE '<form[^>]*method=["'"'"']?post' | wc -l | tr -d ' ')
    rm -f "$jarX" "$jarO" "$jarS"
    if   [ "$nforms" -eq 0 ]; then gotA=1
    elif [ "$nforms" -ge 9 ]; then gotA=0
    else gotA="?($nforms forms — neither gated(0) nor naive(9))"; fi
    echo "$page" | grep -qiE 'aria-[a-z]+=|role=["'"'"']' && gotC=1 || gotC=0
    [ "$(curl -s -o /dev/null -w '%{http_code}' -b "$jar" "$BASE/profile" 2>/dev/null)" = "200" ] && gotSd=1 || gotSd=0
    grep -qi 'rel="\?describedby' /tmp/003b-hdrs 2>/dev/null && gotSo=1 || gotSo=0
    rm -f "$jar"

    obs="$gotA$gotC$gotSd$gotSo"
    if [ "$obs" = "$cell" ]; then
      ok "cell $cell: A/C/Sd/So render exactly per bits"
    else
      no "cell $cell: expected A/C/Sd/So=$cell but observed $obs"
    fi
  done; done; done; done
fi
cleanup

# ---------------------------------------------------------------- AC3: tests pass
hdr "AC3  engine + web tests pass"
if dotnet test test/TicTacToe.Engine.Tests/ >/tmp/003b-engine.log 2>&1; then ok "engine tests pass"; else no "engine tests FAIL (/tmp/003b-engine.log)"; fi
if dotnet test test/TicTacToe.Web.Tests/ >/tmp/003b-web.log 2>&1; then ok "web tests pass"; else no "web tests FAIL (/tmp/003b-web.log)"; fi

# ---------------------------------------------------------------- AC4: harness adapter
hdr "AC4  ttt ships the harness adapter (experiment/ dir, spec 001 §2)"
for f in experiment/manifest.json experiment/directed-facts.json; do
  [ -f "$f" ] && ok "$f present" || no "$f missing"
done
if ls experiment/groundtruth/cell-*.json >/dev/null 2>&1; then
  n=$(ls experiment/groundtruth/cell-*.json | wc -l | tr -d ' ')
  [ "$n" -ge 16 ] && ok "per-cell ground truth present ($n files)" || no "only $n GT cells (need >=16)"
else
  no "experiment/groundtruth/cell-*.json missing"
fi
if ls experiment/scorer/*.fsproj >/dev/null 2>&1 && grep -rqi 'IQualityScorer' experiment/scorer/ 2>/dev/null; then
  ok "sample scorer implements IQualityScorer"
else
  no "experiment/scorer implementing IQualityScorer missing (minimax relocates here)"
fi

hdr "RESULT"
echo "  $pass passed, $fail failed"
[ "$fail" -eq 0 ] && { echo "  003b GREEN"; exit 0; } || { echo "  003b RED"; exit 1; }
