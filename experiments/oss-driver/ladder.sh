#!/usr/bin/env bash
# Model-ladder runner. Descends the Qwen 3.5 rungs (MODELS.md), running the committed sweep.sh
# once per rung so every run uses the SAME reads-free harness, prompts, and bounds — no hand-rolled
# forks (that is what confounded the 2026-07-03 pilot; see FINDINGS.md "Rungs 3 & 4").
#
# Each rung writes to $SWEEP_OUT/<tag>/ with .bounds + .prompt receipts; aggregate.sh rolls up per dir.
#
# Config via env (everything else passes through to sweep.sh):
#   MODELS     space-separated slugs   (default: anchor -> floor descent)
#   PROMPT     plain | browser         (default plain — the cold-start instrument)
#   CELLS      space-separated cells   (default sweep.sh's full set)
#   RUNS       run ids                 (default sweep.sh's "1 2 3 4 5")
#   SWEEP_OUT  parent artifact dir      (default /tmp/ttt-ladder)
#
#   PROMPT=plain CELLS="0000 1000" RUNS=1 bash experiments/oss-driver/ladder.sh
set -uo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SWEEP="$REPO/experiments/oss-driver/sweep.sh"
MODELS="${MODELS:-qwen/qwen3.5-122b-a10b qwen/qwen3.5-35b-a3b qwen/qwen3.5-27b qwen/qwen3.5-9b qwen/qwen3.5-flash-02-23}"
PROMPT="${PROMPT:-plain}"
PARENT="${SWEEP_OUT:-/tmp/ttt-ladder}"; mkdir -p "$PARENT"

for model in $MODELS; do
  tag="$(printf '%s' "$model" | sed 's#.*/##; s/[^a-zA-Z0-9]/_/g')-$PROMPT"
  out="$PARENT/$tag"; rm -rf "$out"
  echo "=== [$(date +%H:%M:%S)] rung $model  prompt=$PROMPT  -> $out ==="
  MODEL="$model" PROMPT="$PROMPT" SWEEP_OUT="$out" bash "$SWEEP" \
    || echo "=== [$(date +%H:%M:%S)] rung $model FAILED (continuing) ==="
  echo "=== [$(date +%H:%M:%S)] done $tag  (bounds: $(cat "$out/.bounds" 2>/dev/null) | prompt: $(cat "$out/.prompt" 2>/dev/null)) ==="
done
echo "LADDER COMPLETE $(date +%H:%M:%S) -> $PARENT"
