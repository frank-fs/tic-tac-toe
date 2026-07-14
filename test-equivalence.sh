#!/usr/bin/env bash
# EQUIVALENCE HARNESS — the acceptance bar for issue #9 (003b).
#
# GOAL (owner, verbatim): "Once we finish migrating, we should with 100% confidence be able to
# claim that the harness and app matches what produced the results. It just moved."
#
# Marker-based checks cannot prove that. This does: it runs the ORIGINAL Surface app (the thing
# that produced every banked result, checked out from git) and the MERGED app side by side, drives
# an IDENTICAL request sequence against both on the /arenas surface, and DIFFS the wire responses.
#
# Any difference is a deviation that must be justified as mechanically-necessary or reverted.
#
# Why /arenas on both: the banked results were produced against Surface's /arenas. The merged app
# must serve /arenas with byte-faithful wire behavior (the owner's alias requirement). /games is
# the product alias of the same handlers and is checked separately by the app's own test suite.
#
# The wire facts the DVs depend on (do NOT let these drift):
#   * 422 PositionTaken   -> half of illegalMoves (Surface/Handlers.fs:266)
#   * 403 out-of-turn     -> the other half
#   * ETag + 304          -> read-friction affordance (Surface/Handlers.fs:142-149)
#   * 400 InvalidMove     -> format-error DV
#   * Link headers (Sd/So), 303 ld+json conneg
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; cd "$REPO"
REF_WT="${REF_WT:-/tmp/ttt-twin-ref}"        # worktree of the pre-migration commit (has Surface)
REF_COMMIT="${REF_COMMIT:-main}"
TWIN_PROJ="experiments/src/TicTacToe.Web.Surface"
NEW_PROJ="src/TicTacToe.Web"
TWIN_PORT=5401; NEW_PORT=5402
CELLS="${CELLS:-0000 1000 0100 0010 0001 1111}"   # override to all 16 with CELLS="$(...)"

diffs=0; checks=0
note(){ echo "  $1"; }
same(){ checks=$((checks+1)); }
delta(){ echo "  DIFF [$1] cell=$2  twin=$3  merged=$4"; diffs=$((diffs+1)); checks=$((checks+1)); }

cleanup(){ for p in $(lsof -ti "tcp:$TWIN_PORT" -ti "tcp:$NEW_PORT" 2>/dev/null); do kill -9 "$p" 2>/dev/null; done; }
trap cleanup EXIT

# --- reference worktree (the app that produced the results) ---------------------
if [ ! -d "$REF_WT" ]; then
  echo "Creating reference worktree of $REF_COMMIT at $REF_WT (the pre-migration Surface app)"
  git worktree add --detach "$REF_WT" "$REF_COMMIT" >/dev/null 2>&1 || { echo "FATAL: cannot create ref worktree"; exit 2; }
fi
[ -d "$REF_WT/$TWIN_PROJ" ] || { echo "FATAL: $REF_WT/$TWIN_PROJ missing — wrong REF_COMMIT?"; exit 2; }

start(){ # start <dir> <proj> <port> <cell>
  TICTACTOE_CELL="$4" TICTACTOE_LOCK_GAME=1 TICTACTOE_INITIAL_GAMES=1 TICTACTOE_MAX_GAMES=1 \
  TICTACTOE_DISABLE_JS=1 \
    dotnet run --project "$1/$2" --no-build --urls "http://127.0.0.1:$3" >"/tmp/eq-$3.log" 2>&1 &
  for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:$3/login" >/dev/null 2>&1 && return 0; sleep 0.5; done
  return 1
}

# Drive one app; echo a normalized wire-fact record (one `key=value` per line).
probe(){ # probe <port>
  local B="http://127.0.0.1:$1" jar id etag
  jar=$(mktemp)
  echo "login=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" "$B/login")"
  local home; home=$(curl -s -c "$jar" -b "$jar" "$B/")
  id=$(echo "$home" | grep -oE 'arenas/[0-9a-f-]{36}' | head -1 | cut -d/ -f2)
  [ -z "$id" ] && { echo "game_discovered=NO"; rm -f "$jar"; return; }
  echo "game_discovered=YES"

  # GET the game: status + the headers the DVs read
  local hdrs; hdrs=$(mktemp)
  echo "get_game=$(curl -s -o /dev/null -w '%{http_code}' -D "$hdrs" -c "$jar" -b "$jar" "$B/arenas/$id")"
  etag=$(grep -i '^etag:' "$hdrs" | tr -d '\r' | cut -d' ' -f2-)
  echo "etag_present=$([ -n "$etag" ] && echo YES || echo NO)"
  echo "link_headers=$(grep -ci '^link:' "$hdrs" | tr -d ' ')"
  echo "link_profile=$(grep -i '^link:' "$hdrs" | grep -ci 'profile' | tr -d ' ')"
  echo "link_describedby=$(grep -i '^link:' "$hdrs" | grep -ci 'describedby' | tr -d ' ')"
  echo "form_count=$(curl -s -c "$jar" -b "$jar" "$B/arenas/$id" | grep -oiE '<form[^>]*method=["'"'"']?post' | wc -l | tr -d ' ')"
  echo "aria_present=$(curl -s -c "$jar" -b "$jar" "$B/arenas/$id" | grep -ciE 'aria-[a-z]+=|role=["'"'"']' | tr -d ' ')"

  # 304 read-friction affordance
  if [ -n "$etag" ]; then
    echo "if_none_match=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -H "If-None-Match: $etag" "$B/arenas/$id")"
  else
    echo "if_none_match=NO_ETAG"
  fi

  # discovery documents
  echo "profile=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" "$B/profile")"
  echo "wellknown_home=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" "$B/.well-known/home")"
  echo "ldjson_conneg=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -H 'Accept: application/ld+json' "$B/arenas/$id")"
  echo "type_doc=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" "$B/arenas/$id/type")"

  # ---- the illegal-move DVs (this is what the banked numbers are made of) ----
  echo "post_legal=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -X POST "$B/arenas/$id" -d 'player=X&position=TopLeft')"
  # same square again -> POSITION TAKEN (twin: 422)
  echo "post_taken=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -X POST "$B/arenas/$id" -d 'player=X&position=TopLeft')"
  # X moves again out of turn -> OUT OF TURN (twin: 403)
  echo "post_out_of_turn=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -X POST "$B/arenas/$id" -d 'player=X&position=TopRight')"
  # malformed -> 400
  echo "post_malformed=$(curl -s -o /dev/null -w '%{http_code}' -c "$jar" -b "$jar" -X POST "$B/arenas/$id" -d 'player=X&position=Nowhere')"
  rm -f "$jar" "$hdrs"
}

echo "Building both apps..."
dotnet build "$REF_WT/$TWIN_PROJ" >/tmp/eq-build-twin.log 2>&1 || { echo "FATAL: twin build failed"; exit 2; }
dotnet build "$NEW_PROJ"          >/tmp/eq-build-new.log  2>&1 || { echo "FATAL: merged build failed"; exit 2; }

for cell in $CELLS; do
  echo; echo "=== cell $cell ==="
  cleanup; sleep 0.3
  start "$REF_WT" "$TWIN_PROJ" "$TWIN_PORT" "$cell" || { echo "  FATAL: twin failed to start"; exit 2; }
  start "."       "$NEW_PROJ"  "$NEW_PORT"  "$cell" || { echo "  FATAL: merged failed to start"; exit 2; }

  t=$(probe "$TWIN_PORT"); n=$(probe "$NEW_PORT")
  # compare key-by-key
  while IFS='=' read -r k tv; do
    nv=$(echo "$n" | grep "^$k=" | cut -d= -f2-)
    if [ "$tv" = "$nv" ]; then same; else delta "$k" "$cell" "$tv" "$nv"; fi
  done <<< "$t"
  cleanup
done

echo; echo "=== RESULT ==="
echo "  $checks wire facts compared, $diffs differ"
if [ "$diffs" -eq 0 ]; then
  echo "  EQUIVALENT — the merged app's /arenas surface matches the app that produced the results."
  exit 0
else
  echo "  NOT EQUIVALENT — each DIFF above must be justified as mechanically-necessary or reverted."
  exit 1
fi
