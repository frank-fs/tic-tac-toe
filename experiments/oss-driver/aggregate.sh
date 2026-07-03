#!/usr/bin/env bash
# Roll up a sweep's RAW per-game artifacts (q-/s-*.json from sweep.sh) into clean.jsonl, one record
# per game, and print completion coverage. Source of truth = the artifacts, never an inline file.
#   bash experiments/oss-driver/aggregate.sh /tmp/qwen-122b
set -uo pipefail
D="${1:-/tmp/ttt-sweep}"
OUT="$D/clean.jsonl"; : > "$OUT"
for q in "$D"/q-*.json; do
  [ -f "$q" ] || continue
  tag=$(basename "$q" .json | sed 's/^q-//'); cell=${tag%%-*}; run=${tag##*-r}
  cost=0; toks=0; ok=1
  for s in "$D"/s-$tag-X1.out "$D"/s-$tag-O2.out "$D"/s-$tag-O3.out; do
    if [ -s "$s" ] && jq -e . "$s" >/dev/null 2>&1; then
      cost=$(echo "$cost + $(jq -r '.costUsd // 0' "$s")" | bc -l)
      toks=$((toks + $(jq -r '.totalTokens // 0' "$s")))
    else ok=0; fi
  done
  jq -c --arg cell "$cell" --argjson run "$run" --argjson cost "$cost" --argjson toks "$toks" --argjson complete "$ok" '
    {cell:$cell,run:$run,outcome,moveCount,dataAnomaly,
     xClean:.byRole.X.clean,oClean:.byRole.O.clean,
     xMiss:(.byRole.X.missedBlock+.byRole.X.missedWin),oMiss:(.byRole.O.missedBlock+.byRole.O.missedWin),
     gameCost:$cost,gameTokens:$toks,seatComplete:($complete==1)}' "$q" >> "$OUT" 2>/dev/null || true
done
echo "records: $(wc -l < "$OUT")  (dir $D)"
echo "=== completion% per cell (complete = draw|decisive) ==="
jq -r '"\(.cell) \(.outcome)"' "$OUT" | awk '
{n[$1]++; if($2=="draw"||$2=="x_wins"||$2=="o_wins")c[$1]++}
END{for(k in n) printf "  %-5s %d%%  (n=%d)\n", k, c[k]*100/n[k], n[k]}' | sort
echo "=== anomalies: $(jq -r 'select(.dataAnomaly)|1' "$OUT"|wc -l|tr -d ' ')  incomplete-seat-data: $(jq -r 'select(.seatComplete|not)|1' "$OUT"|wc -l|tr -d ' ') ==="
