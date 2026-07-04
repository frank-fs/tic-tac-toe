#!/usr/bin/env bash
# Roll up a sweep's RAW per-game artifacts (q-/s-/log-* from sweep.sh) into clean.jsonl, one record
# per game, classify HARNESS ANOMALIES, and print per-cell stats over the CLEAN games only.
# Source of truth = the artifacts, never an inline file.
#   bash experiments/oss-driver/aggregate.sh /tmp/qwen-122b
#
# ANOMALY = an unfair game (harness hiccup), detected from artifacts; flagged, DROPPED from the
# denominator (policy: drop + renormalize), and LISTED so we can see if the harness needs work.
# A game is anomalous if ANY (VALIDITY IS JUDGED FROM THE SERVER LOG, never the driver .out):
#   A  <2 distinct seats got move_accepted     -> never seated 2 players
#   B  game_over count > 1                     -> replay contamination
#   C  dataAnomaly == true (q json)            -> log corruption (move on occupied cell)
# (O3 is the designated observer; its crash does not invalidate a 2-player game.)
# A seated driver's .out being empty/errored is a REPORTING GAP, not an anomaly: the driver was
# often killed at teardown AFTER a clean game (long draws poll past the wait wall). Such games are
# KEPT and graded from the server log; only their cost/token totals are unknown (flagged outMissing).
#
# PRIMARY metrics (per cell, clean games): mean cost, mean tokens, mean invalid-interaction attempts
#   invalidMove(400) + outOfTurn(403) + positionTaken(422)  -- agent failed to form a legal move.
# SECONDARY: completion% (terminal outcome). Structural rejects (NotAPlayer observer, MaxGamesReached)
# are reported but NOT counted as agent error.
set -uo pipefail
D="${1:-/tmp/ttt-sweep}"
OUT="$D/clean.jsonl"; : > "$OUT"

cnt() { jq -rc "$1" "$2" 2>/dev/null | grep -c . ; }   # count matching JSONL lines (0 if none/absent)

for q in "$D"/q-*.json; do
  [ -f "$q" ] || continue
  tag=$(basename "$q" .json | sed 's/^q-//'); cell=${tag%%-*}; run=${tag##*-r}
  log="$D/log-$tag.jsonl"

  seats=$(jq -rc 'select(.event_type=="move_accepted").role' "$log" 2>/dev/null | sort -u | grep -c .)
  overs=$(cnt 'select(.event_type=="game_over")' "$log")
  dataAnom=$(jq -r '.dataAnomaly // false' "$q" 2>/dev/null)

  # outMissing = a seated driver's result JSON is absent/errored. INFORMATIONAL ONLY (reporting gap,
  # not a validity judgement) — the game is still graded from the server log below.
  outMissing=false
  for s in X1 O2; do
    f="$D/s-$tag-$s.out"
    if [ ! -s "$f" ] || ! jq -e . "$f" >/dev/null 2>&1 || [ -n "$(jq -r '.error // empty' "$f" 2>/dev/null)" ]; then outMissing=true; fi
  done

  reason=""
  [ "$seats" -lt 2 ] && reason="${reason}seats<2 "
  [ "$overs" -gt 1 ] && reason="${reason}game_over>1 "
  [ "$dataAnom" = "true" ] && reason="${reason}logCorrupt "
  anomaly=false; [ -n "$reason" ] && anomaly=true

  invalidMove=$(cnt 'select(.status_code==400)' "$log")
  outOfTurn=$(cnt 'select(.status_code==403 and .rejection_reason=="OutOfTurn")' "$log")
  positionTaken=$(cnt 'select(.status_code==422)' "$log")
  structural=$(cnt 'select((.status_code==403 and .rejection_reason=="NotAPlayer") or .status_code==409)' "$log")

  cost=0; toks=0
  for s in X1 O2 O3; do
    f="$D/s-$tag-$s.out"
    if [ -s "$f" ] && jq -e . "$f" >/dev/null 2>&1; then
      cost=$(echo "$cost + $(jq -r '.costUsd // 0' "$f")" | bc -l)
      toks=$((toks + $(jq -r '.totalTokens // 0' "$f")))
    fi
  done

  seatTrunc=0
  for s in X1 O2; do
    f="$D/s-$tag-$s.out"
    if [ -s "$f" ] && jq -e . "$f" >/dev/null 2>&1; then
      out=$(jq -r '.outcome // ""' "$f")
      [ "$out" = "window_truncated" ] && seatTrunc=$((seatTrunc+1))
    fi
  done

  jq -c --arg cell "$cell" --argjson run "$run" --arg reason "${reason% }" \
        --argjson anomaly "$anomaly" --argjson outMissing "$outMissing" --argjson seats "$seats" --argjson overs "$overs" \
        --argjson cost "$cost" --argjson toks "$toks" \
        --argjson im "$invalidMove" --argjson ot "$outOfTurn" --argjson pt "$positionTaken" --argjson st "$structural" \
        --argjson seatTrunc "$seatTrunc" '
    {cell:$cell,run:$run,anomaly:$anomaly,reason:$reason,outMissing:$outMissing,outcome,moveCount,seats:$seats,gameOvers:$overs,
     xClean:.byRole.X.clean,oClean:.byRole.O.clean,
     gameCost:$cost,gameTokens:$toks,
     invalidMove:$im,outOfTurn:$ot,positionTaken:$pt,invalidTotal:($im+$ot+$pt),structural:$st,
     seatTrunc:$seatTrunc}' "$q" >> "$OUT" 2>/dev/null || true
done

total=$(wc -l < "$OUT" | tr -d ' ')
anom=$(jq -rc 'select(.anomaly)' "$OUT" | grep -c . || true)
echo "records: $total   anomalies(dropped): $anom   clean: $((total-anom))   (dir $D)"

echo "=== DROPPED anomalies (cell run reason) ==="
jq -r 'select(.anomaly) | "  \(.cell) r\(.run)  \(.reason)"' "$OUT" 2>/dev/null | sort
[ "$anom" -eq 0 ] && echo "  (none)"

gaps=$(jq -rc 'select((.anomaly|not) and .outMissing)' "$OUT" | grep -c . || true)
echo "=== reporting gaps (KEPT + graded; cost/tokens understated — seated driver .out missing) ==="
jq -r 'select((.anomaly|not) and .outMissing) | "  \(.cell) r\(.run)  outcome=\(.outcome)"' "$OUT" 2>/dev/null | sort
[ "$gaps" -eq 0 ] && echo "  (none)"

echo "=== per-cell over CLEAN games (n = clean count) ==="
echo "  cell   n  compl%  cost$/game  tok/game  |  invalid: total  (move/outturn/postaken)  struct"
jq -rc 'select(.anomaly|not) | [.cell,.outcome,.gameCost,.gameTokens,.invalidMove,.outOfTurn,.positionTaken,.invalidTotal,.structural] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; n[c]++;
  if($2=="draw"||$2=="x_wins"||$2=="o_wins") comp[c]++;
  cost[c]+=$3; tok[c]+=$4; im[c]+=$5; ot[c]+=$6; pt[c]+=$7; it[c]+=$8; st[c]+=$9 }
END{ for(c in n) printf "  %-5s %2d  %4d%%  %9.5f  %8.0f  |  %5.1f  (%.1f/%.1f/%.1f)  %5.1f\n",
       c, n[c], comp[c]*100/n[c], cost[c]/n[c], tok[c]/n[c], it[c]/n[c], im[c]/n[c], ot[c]/n[c], pt[c]/n[c], st[c]/n[c] }' | sort

echo "=== incomplete breakdown (seat-level window_truncated vs stalled) ==="
jq -rc 'select(.anomaly|not) | [.cell,.outcome,.seatTrunc] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; seen[c]=1; n[c]++
  st=$3+0; tot[c]+=st
  if(st>0) any[c]++
  if($2!="draw"&&$2!="x_wins"&&$2!="o_wins"&&$2!="window_truncated") stall[c]++ }
END{ for(c in seen) printf "  %-5s seat-truncations=%d  (of %d seated-seats)  games-with-any=%d  stalled=%d\n",
       c, tot[c]+0, n[c]*2, any[c]+0, stall[c]+0 }' | sort

echo "=== info-seeking per cell (mean agent GETs across clean-game seats) ==="
for cell in $(jq -r 'select(.anomaly|not).cell' "$OUT" | sort -u); do
  strat=0; prof=0; n=0
  for t in "$D"/t-$cell-r*.jsonl; do
    [ -f "$t" ] || continue; n=$((n+1))
    strat=$((strat + $(jq -r 'select(.role=="assistant").content' "$t" 2>/dev/null | grep -ic "GET /strategy")))
    prof=$((prof + $(jq -r 'select(.role=="assistant").content' "$t" 2>/dev/null | grep -ic "GET /profile")))
  done
  [ "$n" -gt 0 ] && printf "  %-5s /strategy=%.2f  /profile=%.2f  (seats=%d)\n" "$cell" "$(echo "$strat/$n"|bc -l)" "$(echo "$prof/$n"|bc -l)" "$n"
done
