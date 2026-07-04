#!/usr/bin/env bash
# Factor-isolation sweep harness (hardened). Runs cells x runs of 3-seat cold-start games with the
# PLAIN prompt (the cold-start instrument; the browser-conduct variant was dropped 2026-07-03 —
# see FINDINGS.md), one game per server with bulletproof teardown, and writes RAW per-game
# artifacts (q-/s-/c-*) that aggregate.sh rolls up. No inline aggregation.
#
# Config via env:
#   MODEL      OpenRouter slug         (default anthropic/claude-haiku-4.5)
#   CELLS      space-separated cells   (default "0000 1000 0100 0010 0001 1111")
#   RUNS       space-separated run ids (default "1 2 3 4 5")
#   SWEEP_OUT  artifact dir            (default /tmp/ttt-sweep)
#
#   MODEL=qwen/qwen3.5-122b-a10b SWEEP_OUT=/tmp/qwen-122b bash experiments/oss-driver/sweep.sh
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ARENA="$REPO/experiments/oss-driver/arena.sh"
BIN="$REPO/experiments/oss-driver/bin/Debug/net10.0/TicTacToe.OssDriver"   # exec directly (signal delivery)
[ -x "$BIN" ] || { echo "building driver (BIN missing)…"; dotnet build "$REPO/experiments/oss-driver" -v q --nologo || exit 1; }
PROXY_BASE=http://127.0.0.1:6328
MODEL="${MODEL:-anthropic/claude-haiku-4.5}"
CELLS="${CELLS:-0000 1000 0100 0010 0001 1111}"
RUNS="${RUNS:-1 2 3 4 5}"
MAX_ATTEMPTS="${MAX_ATTEMPTS:-30}"      # POST-attempt budget (mutations). Generous: play never nears it.
MAX_TURNS="${MAX_TURNS:-80}"            # total-turn backstop incl. free GETs (R10). Agent-invisible.
D="${SWEEP_OUT:-/tmp/ttt-sweep}"; mkdir -p "$D"
printf 'attempts=%s turns=%s\n' "$MAX_ATTEMPTS" "$MAX_TURNS" > "$D/.bounds"
PROMPT="${PROMPT:-plain}"                # plain (cold-start instrument) | browser (re-test arm)
if [ "$PROMPT" = "browser" ]; then
  export COLDSTART_PROMPT_PATH="$REPO/experiments/oss-driver/coldstart-browser-prompt.md"
  export DIRECTED_PROMPT_PATH="$REPO/experiments/oss-driver/directed-browser-prompt.md"
fi
echo "$PROMPT" > "$D/.prompt"
cd "$REPO"

PIDF="$D/.sweep.pid"
if [ -f "$PIDF" ] && kill -0 "$(cat "$PIDF" 2>/dev/null)" 2>/dev/null; then
  echo "ABORT: sweep already running (pid $(cat "$PIDF"))"; exit 1
fi
echo $$ > "$PIDF"; trap 'rm -f "$PIDF"' EXIT

ensure_clean() {                      # kill stray seats + free ports 5328/6328 (bounded 20s)
  pkill -f "oss-driver.*--role" 2>/dev/null || true
  local i=0
  while [ $i -lt 20 ]; do
    local held; held="$(lsof -tiTCP:5328 -tiTCP:6328 2>/dev/null || true)"
    [ -z "$held" ] && return 0
    echo "$held" | xargs -r kill -9 2>/dev/null || true
    sleep 1; i=$((i+1))
  done
  echo "WARN: ports still held after 20s" >&2
}

run_game() {
  local cell=$1 run=$2 tag="$1-r$2"
  echo "[$(date +%H:%M:%S)] START $tag"
  ensure_clean
  CELL=$cell bash "$ARENA" up surface > "$D/up-$tag.txt" 2>"$D/up-$tag.err" || { echo "UPFAIL $tag"; return 1; }
  local gid; gid=$(grep '^GAME_ID=' "$D/up-$tag.txt" | cut -d= -f2)
  [ -n "$gid" ] || { echo "NOGID $tag"; bash "$ARENA" down surface >/dev/null 2>&1; ensure_clean; return 1; }
  local pids=()
  for spec in X:1 O:2 O:3; do          # 3 staggered seats; server seats first two, 3rd observes
    local role=${spec%%:*} n=${spec##*:}
    # Exec the built binary directly (NOT `dotnet run`): the teardown kill must hit the app itself so
    # its SIGTERM handler fires — `dotnet run` swallows the signal at the launcher. RESULT_PATH: the
    # driver writes its result JSON to the .out directly (flushed, and re-flushed on SIGTERM) so
    # cost/tokens survive a teardown kill of a long game; stdout goes to a throwaway .log.
    env TRANSCRIPT_PATH="$D/t-$tag-$role$n.jsonl" RESULT_PATH="$D/s-$tag-$role$n.out" \
      "$BIN" \
        --role "$role" --arm http --base "$PROXY_BASE" --route arenas --game "$gid" \
        --coldstart --model "$MODEL" --max-attempts "$MAX_ATTEMPTS" --max-turns "$MAX_TURNS" \
        > "$D/s-$tag-$role$n.log" 2>"$D/s-$tag-$role$n.err" &
    pids+=($!); sleep 4
  done
  local w=0                            # bounded wait for self-termination (up to 400s)
  while [ $w -lt 80 ]; do
    local alive=0; for p in "${pids[@]}"; do kill -0 "$p" 2>/dev/null && alive=1; done
    [ $alive -eq 0 ] && break; sleep 5; w=$((w+1))
  done
  for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done
  cp /tmp/arena-surface.jsonl "$D/log-$tag.jsonl" 2>/dev/null || true
  dotnet run --project experiments/oss-driver --no-build -- quality --game "$gid" --log "$D/log-$tag.jsonl" --out "$D/q-$tag.json" >/dev/null 2>&1 || true
  for spec in X:1 O:2 O:3; do
    local role=${spec%%:*} n=${spec##*:}
    dotnet run --project experiments/oss-driver --no-build -- code --transcript "$D/t-$tag-$role$n.jsonl" --role "$role" > "$D/c-$tag-$role$n.json" 2>/dev/null || echo '{}' > "$D/c-$tag-$role$n.json"
  done
  bash "$ARENA" down surface >/dev/null 2>&1; ensure_clean
  echo "[$(date +%H:%M:%S)] DONE  $tag"
}

echo "sweep: model=$MODEL cells=[$CELLS] runs=[$RUNS] out=$D"
for cell in $CELLS; do for run in $RUNS; do run_game "$cell" "$run"; done; done
echo "SWEEP COMPLETE $(date +%H:%M:%S) -> $D"
