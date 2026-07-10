#!/usr/bin/env bash
# Roll up a sweep's RAW per-game artifacts (q-/s-/log-/g-* from sweep.sh) into clean.jsonl, one record
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

  # seats<2 is NOT an anomaly: under the embed-free instrument a seat can burn its whole attempt budget
  # without landing a single legal move. Dropping these hid embed-free's worst failures and biased
  # completion upward. Record it as a first-class BOOTSTRAP-FAILURE outcome (kept in stats — its invalid
  # attempts are the primary DV) instead of discarding the game. Only a corrupt/double-terminal log is
  # a true anomaly.
  bootstrapFail=false; [ "$seats" -lt 2 ] && bootstrapFail=true
  reason=""
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
        --argjson anomaly "$anomaly" --argjson bootstrapFail "$bootstrapFail" --argjson outMissing "$outMissing" --argjson seats "$seats" --argjson overs "$overs" \
        --argjson cost "$cost" --argjson toks "$toks" \
        --argjson im "$invalidMove" --argjson ot "$outOfTurn" --argjson pt "$positionTaken" --argjson st "$structural" \
        --argjson seatTrunc "$seatTrunc" '
    {cell:$cell,run:$run,anomaly:$anomaly,bootstrapFail:$bootstrapFail,reason:$reason,outMissing:$outMissing,outcome,moveCount,seats:$seats,gameOvers:$overs,
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

bfail=$(jq -rc 'select(.bootstrapFail and (.anomaly|not))' "$OUT" | grep -c . || true)
echo "=== bootstrap failures (KEPT — a seat never landed a legal move; embed-free thrash, a real outcome) ==="
jq -r 'select(.bootstrapFail and (.anomaly|not)) | "  \(.cell) r\(.run)  moveCount=\(.moveCount) invalidTotal=\(.invalidTotal)"' "$OUT" 2>/dev/null | sort
[ "$bfail" -eq 0 ] && echo "  (none)"

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

echo ""
echo "############ DIMENSION 1 — DISCOVERY: can the agent tell WHAT it is playing + HOW to play it? ############"
echo "############   (recognize = the 2-moment report; info-seeking = did it fetch the contract;    ############"
echo "############    format-discovery = did it get the wire FORMAT right. Owned by Sd/So + nudge.)  ############"

# WIRE-TRUTHED (was self-report): count real /profile + /type dereferences from the proxy HTTPLOG
# (http-*.jsonl), matching the grader's path.Contains semantics — NOT grep of the agent's narrated
# "GET /profile" in the transcript (which counted intent, and disagreed with the wire). Per game (the
# proxy log aggregates all seats; it does not attribute to a seat), so this is mean dereferences/game.
echo "=== info-seeking per cell (mean /profile + /type dereferences from proxy wire, per game) ==="
for cell in $(jq -r 'select(.anomaly|not).cell' "$OUT" | sort -u); do
  strat=0; prof=0; n=0
  for h in "$D"/http-$cell-r*.jsonl; do
    [ -f "$h" ] || continue; n=$((n+1))
    prof=$((prof + $(jq -rc 'select(.path|type=="string" and test("/profile"))' "$h" 2>/dev/null | grep -c . || true)))
    strat=$((strat + $(jq -rc 'select(.path|type=="string" and test("/type"))' "$h" 2>/dev/null | grep -c . || true)))
  done
  if [ "$n" -gt 0 ]; then printf "  %-5s /type=%.2f  /profile=%.2f  (games=%d)\n" "$cell" "$(echo "$strat/$n"|bc -l)" "$(echo "$prof/$n"|bc -l)" "$n"
  else printf "  %-5s (no proxy HTTPLOG — pre-hardening run?)\n" "$cell"; fi
done

# RECOGNIZE (discovery axis) — the DV that carries P1/P3/P4/P6. Per cell, mean correct verdicts from
# the g-*.json grades (pre = {appIs,goal,isMultiplayer,howToJoin}/4 discovery; post =
# {myRole,canIMove,myAffordances}/3 role-discrimination). Seats globbed per cell to match the
# info-seeking loop above (floor runs have 0 anomalies; anomalous games are rare and not filtered here).
echo "=== recognize per cell (mean correct verdicts across clean-game seats) ==="
for cell in $(jq -r 'select(.anomaly|not).cell' "$OUT" | sort -u); do
  preC=0; postC=0; present=0; nseat=0
  for g in "$D"/g-$cell-r*.json; do
    [ -f "$g" ] && jq -e . "$g" >/dev/null 2>&1 || continue
    nseat=$((nseat+1))
    preC=$((preC + $(jq '[.pre.appIs.verdict,.pre.goal.verdict,.pre.isMultiplayer.verdict,.pre.howToJoin.verdict]|map(select(.=="correct"))|length' "$g" 2>/dev/null || echo 0)))
    postC=$((postC + $(jq '[.post.myRole.verdict,.post.canIMove.verdict,.post.myAffordances.verdict]|map(select(.=="correct"))|length' "$g" 2>/dev/null || echo 0)))
    present=$((present + $(jq 'if (.reportsPresent.pre and .reportsPresent.post) then 1 else 0 end' "$g" 2>/dev/null || echo 0)))
  done
  [ "$nseat" -gt 0 ] && printf "  %-5s pre-correct=%.2f/4  post-correct=%.2f/3  reports=%d/%d  (seats=%d)\n" \
    "$cell" "$(echo "$preC/$nseat"|bc -l)" "$(echo "$postC/$nseat"|bc -l)" "$present" "$nseat" "$nseat"
done

# FORMAT-GUESSING — the sharp discovery DV: did the agent get the POST FORMAT right, or wildly guess it?
# firstMoveFormat (grader, from the transcript's first POST + its HTTP status): "guessed" = first POST
# 400'd (unparseable = wrong shape/vocabulary); "discovered" = first POST parsed (200/303/403/422 —
# format learned from the board form, /profile, or knowledge; no fetch required). formatGuesses = total
# 400s/game (the VOLUME of wild guessing — 403/422 illegal MOVES are excluded, they are not guessing).
# Which enhancements cut format-guessing is the question (e.g. Sd's /profile lowers the 400 volume).
echo "=== format-guessing per cell (first POST: correct-format vs guessed; + mean 400s/game) ==="
for cell in $(jq -r 'select(.anomaly|not).cell' "$OUT" | sort -u); do
  disc=0; guess=0; nop=0; g400=0; ng=0
  for g in "$D"/g-$cell-r*.json; do
    [ -f "$g" ] && jq -e . "$g" >/dev/null 2>&1 || continue
    case "$(jq -r '.firstMoveFormat // "?"' "$g" 2>/dev/null)" in
      discovered) disc=$((disc+1));; guessed) guess=$((guess+1));; no-post) nop=$((nop+1));; esac
  done
  for h in "$D"/http-$cell-r*.jsonl; do
    [ -f "$h" ] || continue; ng=$((ng+1))
    g400=$((g400 + $(jq -rc 'select(.method=="POST" and .status==400)' "$h" 2>/dev/null | grep -c . || true)))
  done
  posted=$((disc+guess))
  if [ "$posted" -gt 0 ]; then
    rate="n/a"; [ "$ng" -gt 0 ] && rate=$(printf '%.1f' "$(echo "$g400/$ng"|bc -l)")
    printf "  %-5s correct-format=%d/%d  guessed=%d/%d  400s/game=%s  (no-post=%d)\n" \
      "$cell" "$disc" "$posted" "$guess" "$posted" "$rate" "$nop"
  fi
done

echo ""
echo "############ DIMENSION 2 — GAMEPLAY: DURING play, does the agent follow the game correctly? ############"
echo "############   (illegalMoves = attempts at moves illegal for the CURRENT state — the A signal;   ############"
echo "############    clean-play = minimax blunder-free; completion = reached a terminal. Owned by A/C.) ############"

# illegalMoves = 403 out-of-turn + 422 position-taken: tried a move ILLEGAL FOR THE CURRENT STATE. This is
# the AFFORDANCE (A) dimension — A renders only currently-legal move-forms, revised each turn, so an
# affordance-following agent should attempt few/none. Kept DISTINCT from formatErrors (400 = wrong wire
# shape/vocab = a DISCOVERY problem, Dimension 1), which A does not own.
echo "=== illegalMoves per cell (403 out-of-turn + 422 position-taken /game — the A/affordance DV) ==="
jq -rc 'select(.anomaly|not) | [.cell,.outOfTurn,.positionTaken,.invalidMove] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; n[c]++; ot[c]+=$2; pt[c]+=$3; fmt[c]+=$4 }
END{ for(c in n) printf "  %-5s illegalMoves=%4.1f/game  (outOfTurn %.1f + taken %.1f)   [fmt-errors(400)=%.1f, Dim1]\n",
       c, (ot[c]+pt[c])/n[c], ot[c]/n[c], pt[c]/n[c], fmt[c]/n[c] }' | sort

echo "=== clean-play per cell (minimax blunder-free seats; completion = reached a terminal) ==="
jq -rc 'select(.anomaly|not) | [.cell,.outcome,.xClean,.oClean] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; n[c]++;
  if($2=="draw"||$2=="x_wins"||$2=="o_wins") comp[c]++;
  if($3=="true") xc[c]++; if($4=="true") oc[c]++ }
END{ for(c in n) printf "  %-5s completion=%3d%%  xClean=%d/%d oClean=%d/%d\n",
       c, comp[c]*100/n[c], xc[c]+0, n[c], oc[c]+0, n[c] }' | sort
