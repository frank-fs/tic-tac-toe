#!/usr/bin/env bash
# Roll up a sweep's RAW per-game artifacts (q-/s-/log-* from sweep.sh) into clean.jsonl, one record
# per game, classify HARNESS ANOMALIES, and print per-cell stats over the CLEAN games only.
# Source of truth = the artifacts, never an inline file.
#   bash experiments/oss-driver/aggregate.sh /tmp/qwen-122b
#
# ANOMALY = an unfair game (harness hiccup), detected from artifacts; flagged, DROPPED from the
# denominator (policy: drop + renormalize), and LISTED so we can see if the harness needs work.
# A game is anomalous if ANY:
#   A  <2 distinct seats got move_accepted     -> never seated 2 players
#   B  game_over count > 1                     -> replay contamination
#   C  dataAnomaly == true (q json)            -> log corruption (move on occupied cell)
#   D  a SEATED driver (X1/O2) .out missing/invalid/errored -> mid-game crash
# (O3 is the designated observer; its crash does not invalidate a 2-player game.)
#
# PRIMARY metrics (per cell, clean games): mean cost, mean tokens, mean invalid-interaction attempts
#   invalidMove(400) + outOfTurn(403) + positionTaken(422)  -- agent failed to form a legal move.
# SECONDARY: completion% (terminal outcome). Structural rejects (NotAPlayer observer, MaxGamesReached)
# are reported but NOT counted as agent error.
set -uo pipefail
D="${1:-/tmp/ttt-sweep}"
# cap-hit threshold: env override > the cap sweep.sh recorded for this run > legacy default 25.
MAXACT="${MAXACT:-$( [ -f "$D/.maxactions" ] && cat "$D/.maxactions" || echo 25 )}"
                                        # a seated driver at this cap + incomplete = budget-exhausted
                                        # (harness limit), not a genuine stall. Annotated, NOT dropped.
OUT="$D/clean.jsonl"; : > "$OUT"

cnt() { jq -rc "$1" "$2" 2>/dev/null | grep -c . ; }   # count matching JSONL lines (0 if none/absent)

for q in "$D"/q-*.json; do
  [ -f "$q" ] || continue
  tag=$(basename "$q" .json | sed 's/^q-//'); cell=${tag%%-*}; run=${tag##*-r}
  log="$D/log-$tag.jsonl"

  seats=$(jq -rc 'select(.event_type=="move_accepted").role' "$log" 2>/dev/null | sort -u | grep -c .)
  overs=$(cnt 'select(.event_type=="game_over")' "$log")
  dataAnom=$(jq -r '.dataAnomaly // false' "$q" 2>/dev/null)

  seatBad=0; capHit=false
  for s in X1 O2; do
    f="$D/s-$tag-$s.out"
    if [ ! -s "$f" ] || ! jq -e . "$f" >/dev/null 2>&1 || [ -n "$(jq -r '.error // empty' "$f" 2>/dev/null)" ]; then seatBad=1; continue; fi
    a=$(jq -r '.actions // 0' "$f" 2>/dev/null); [ "$a" -ge "$MAXACT" ] 2>/dev/null && capHit=true
  done

  reason=""
  [ "$seats" -lt 2 ] && reason="${reason}seats<2 "
  [ "$overs" -gt 1 ] && reason="${reason}game_over>1 "
  [ "$dataAnom" = "true" ] && reason="${reason}logCorrupt "
  [ "$seatBad" -eq 1 ] && reason="${reason}seatCrash "
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

  jq -c --arg cell "$cell" --argjson run "$run" --arg reason "${reason% }" \
        --argjson anomaly "$anomaly" --argjson seats "$seats" --argjson overs "$overs" --argjson capHit "$capHit" \
        --argjson cost "$cost" --argjson toks "$toks" \
        --argjson im "$invalidMove" --argjson ot "$outOfTurn" --argjson pt "$positionTaken" --argjson st "$structural" '
    {cell:$cell,run:$run,anomaly:$anomaly,reason:$reason,outcome,moveCount,seats:$seats,gameOvers:$overs,capHit:$capHit,
     xClean:.byRole.X.clean,oClean:.byRole.O.clean,
     gameCost:$cost,gameTokens:$toks,
     invalidMove:$im,outOfTurn:$ot,positionTaken:$pt,invalidTotal:($im+$ot+$pt),structural:$st}' "$q" >> "$OUT" 2>/dev/null || true
done

total=$(wc -l < "$OUT" | tr -d ' ')
anom=$(jq -rc 'select(.anomaly)' "$OUT" | grep -c . || true)
echo "records: $total   anomalies(dropped): $anom   clean: $((total-anom))   (dir $D)"

echo "=== DROPPED anomalies (cell run reason) ==="
jq -r 'select(.anomaly) | "  \(.cell) r\(.run)  \(.reason)"' "$OUT" 2>/dev/null | sort
[ "$anom" -eq 0 ] && echo "  (none)"

echo "=== per-cell over CLEAN games (n = clean count) ==="
echo "  cell   n  compl%  cost$/game  tok/game  |  invalid: total  (move/outturn/postaken)  struct"
jq -rc 'select(.anomaly|not) | [.cell,.outcome,.gameCost,.gameTokens,.invalidMove,.outOfTurn,.positionTaken,.invalidTotal,.structural] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; n[c]++;
  if($2=="draw"||$2=="x_wins"||$2=="o_wins") comp[c]++;
  cost[c]+=$3; tok[c]+=$4; im[c]+=$5; ot[c]+=$6; pt[c]+=$7; it[c]+=$8; st[c]+=$9 }
END{ for(c in n) printf "  %-5s %2d  %4d%%  %9.5f  %8.0f  |  %5.1f  (%.1f/%.1f/%.1f)  %5.1f\n",
       c, n[c], comp[c]*100/n[c], cost[c]/n[c], tok[c]/n[c], it[c]/n[c], im[c]/n[c], ot[c]/n[c], pt[c]/n[c], st[c]/n[c] }' | sort

echo "=== incomplete breakdown (why not terminal): capped(budget, actions>=$MAXACT) vs stalled(genuine) ==="
jq -rc 'select(.anomaly|not) | [.cell,.outcome,.capHit] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; if($2!="draw"&&$2!="x_wins"&&$2!="o_wins"){ inc[c]++; if($3=="true")cap[c]++; else stall[c]++ } ; seen[c]=1 }
END{ for(c in seen) printf "  %-5s incomplete=%d  capped=%d  stalled=%d\n", c, inc[c]+0, cap[c]+0, stall[c]+0 }' | sort
