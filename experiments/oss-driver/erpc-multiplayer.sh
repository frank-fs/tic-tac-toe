#!/usr/bin/env bash
# 3-seat multiplayer ERPC sweep (fair harness: reads-free budget, whose-turn wording parity).
# The ERPC analog of sweep.sh — one shared MCP connection, server-regulated round-robin, n runs.
# Each run: fresh MCP server subprocess (ErpcDriver.runMultiplayer), pre-created shared game,
# app-blind SHA-locked cold-start prompt (erpc-coldstart-prompt.md).
#
#   MODEL=qwen/qwen3.5-9b RUNS="1 2 3 4 5" OUT=/tmp/erpc-9b bash experiments/oss-driver/erpc-multiplayer.sh
#
# Config via env:
#   MODEL   OpenRouter slug        (default qwen/qwen3.5-9b)
#   RUNS    space-separated ids    (default "1 2 3 4 5")
#   OUT     artifact dir           (default /tmp/erpc-mp)
#   TURNS   productive-turn budget (default 80; backstop = 3x, reads free)
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BIN="$REPO/experiments/oss-driver/bin/Debug/net10.0/TicTacToe.OssDriver"
[ -x "$BIN" ] || { echo "building driver…"; dotnet build "$REPO/experiments/oss-driver" -v q --nologo || exit 1; }
PROMPT="$REPO/experiments/oss-driver/erpc-coldstart-prompt.md"
LOCK="$REPO/experiments/oss-driver/erpc-coldstart-prompt.md.sha256"
# guardrail: refuse to run against a mutated/unlocked cold-start prompt
# (compare hashes directly — the lock stores a bare filename, so `shasum -c` is cwd-sensitive)
LOCKED_SHA="$(cut -d' ' -f1 "$LOCK")"
ACTUAL_SHA="$(shasum -a 256 "$PROMPT" | cut -d' ' -f1)"
if [ "$LOCKED_SHA" != "$ACTUAL_SHA" ]; then
  echo "ABORT: erpc-coldstart-prompt.md ($ACTUAL_SHA) != locked SHA ($LOCKED_SHA)"; exit 1
fi
MODEL="${MODEL:-qwen/qwen3.5-9b}"
RUNS="${RUNS:-1 2 3 4 5}"
TURNS="${TURNS:-80}"
OUT="${OUT:-/tmp/erpc-mp}"; mkdir -p "$OUT"
printf 'arm=erpc\nmode=3-seat multiplayer FAIR (reads-free budget, whose-turn wording parity)\nmodel=%s\nerpc_prompt_sha=%s\n' \
  "$MODEL" "$(shasum -a 256 "$PROMPT" | cut -d' ' -f1)" > "$OUT/.instrument"
printf 'productive=%s backstop=%s (reads free)\n' "$TURNS" "$((TURNS * 3))" > "$OUT/.bounds"
cd "$REPO"
for r in $RUNS; do
  echo "=== run $r ($MODEL) ==="
  WORKER_MODEL="$MODEL" \
  ERPC_LOG_PATH="$OUT/log-r$r.jsonl" \
  TRANSCRIPT_PREFIX="$OUT/t-r$r" \
    "$BIN" --arm erpc --multiplayer --coldstart --max-turns "$TURNS" \
    > "$OUT/result-r$r.json" 2> "$OUT/err-r$r.log"
  echo "  -> $(cat "$OUT/result-r$r.json")"
done
echo "DONE: $OUT"
