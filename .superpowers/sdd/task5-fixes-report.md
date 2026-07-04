# Task 5 Fixes Report

Branch: `reads-free`  
Date: 2026-07-04

---

## FIX 1 — Driver.fs terminal false-positive

### Diff

```diff
-// A 404 is terminal ONLY on the game resource itself (the game vanished). During cold-start
-// the agent probes unknown paths (/status, /profile, ...); a 404 there is exploration
-// feedback, NOT game-over — treating it as terminal aborts discovery prematurely.
-let private terminal (gamePath: string) (path: string) (status: int) (body: string) : string option =
-    if status = 404 then
-        if path.TrimEnd('/') = gamePath.TrimEnd('/') then Some "game_not_found" else None
-    else
-        let low = body.ToLowerInvariant()
-        if terminalTokens |> List.exists low.Contains then Some "over" else None
+// A 404 is terminal ONLY on the game resource itself (the game vanished). During cold-start
+// the agent probes unknown paths (/status, /profile, ...); a 404 there is exploration
+// feedback, NOT game-over — treating it as terminal aborts discovery prematurely.
+// Token-body terminal detection is likewise scoped to the game path: /strategy and similar
+// domain-knowledge docs may contain phrases like "…that wins." and must not falsely trigger
+// game-over, causing the seat to quit mid-discovery.
+let private terminal (gamePath: string) (path: string) (status: int) (body: string) : string option =
+    let onGame = path.TrimEnd('/') = gamePath.TrimEnd('/')
+    if status = 404 then
+        if onGame then Some "game_not_found" else None
+    elif onGame then
+        let low = body.ToLowerInvariant()
+        if terminalTokens |> List.exists low.Contains then Some "over" else None
+    else None
```

File: `experiments/oss-driver/Driver.fs` lines 53–62.

### FIX 1 Logic Verification

Call site (line 228): `match terminal gamePath path status text with`

- `gamePath`: resolved by `seed`, e.g. `/games/uuid`
- `path`: the actual request path the agent issued, e.g. `/strategy`

For a GET to `/strategy` with body containing " wins":
- `onGame = "/strategy".TrimEnd('/') = "/games/uuid".TrimEnd('/')` → **false**
- `status` is 200, not 404 → `if status = 404` branch is skipped
- `elif onGame` → **false** → falls through to `else None`
- Result: **None** — no false game-over triggered

For a POST to `/games/uuid` that returns a winning-move response:
- `onGame = "/games/uuid".TrimEnd('/') = "/games/uuid".TrimEnd('/')` → **true**
- `status` is 200, not 404 → `if status = 404` skipped
- `elif onGame` → **true**
- Body contains `"status":"xwins"` or similar terminal token → `Some "over"`
- Result: **Some "over"** — game-over correctly detected

The fix preserves all game-path terminal detection while making non-game paths immune.

---

## FIX 2 — aggregate.sh reads seat-level window_truncated

### What was wrong

The old breakdown block read `.outcome` from `clean.jsonl`, which is the game-level outcome from `q-*.json` (values: draw/x_wins/o_wins/incomplete). The field `window_truncated` lives in the SEAT files (`s-<tag>-{X1,O2,O3}.out`). The game-level `.outcome` field is never "window_truncated", so the old block always reported 0.

### Diff (aggregate.sh)

**In the main per-game loop** (after cost/toks accumulation), added:

```bash
  seatTrunc=0
  for s in X1 O2; do
    f="$D/s-$tag-$s.out"
    if [ -s "$f" ] && jq -e . "$f" >/dev/null 2>&1; then
      out=$(jq -r '.outcome // ""' "$f")
      [ "$out" = "window_truncated" ] && seatTrunc=$((seatTrunc+1))
    fi
  done
```

**In the jq invocation** writing to clean.jsonl, added `--argjson seatTrunc "$seatTrunc"` argument and `seatTrunc:$seatTrunc` field.

**Replaced the breakdown block:**

```diff
-echo "=== incomplete breakdown (window_truncated vs stalled) ==="
-jq -rc 'select(.anomaly|not) | [.cell,.outcome] | @tsv' "$OUT" | awk -F'\t' '
-{ c=$1; seen[c]=1
-  if($2=="draw"||$2=="x_wins"||$2=="o_wins") ;
-  else if($2=="window_truncated") tr[c]++
-  else st[c]++ }
-END{ for(c in seen) printf "  %-5s truncated(window)=%d  stalled=%d\n", c, tr[c]+0, st[c]+0 }' | sort
+echo "=== incomplete breakdown (seat-level window_truncated vs stalled) ==="
+jq -rc 'select(.anomaly|not) | [.cell,.outcome,.seatTrunc] | @tsv' "$OUT" | awk -F'\t' '
+{ c=$1; seen[c]=1; n[c]++
+  st=$3+0; tot[c]+=st
+  if(st>0) any[c]++
+  if($2!="draw"&&$2!="x_wins"&&$2!="o_wins"&&$2!="window_truncated") stall[c]++ }
+END{ for(c in seen) printf "  %-5s seat-truncations=%d  (of %d seated-seats)  games-with-any=%d  stalled=%d\n",
+       c, tot[c]+0, n[c]*2, any[c]+0, stall[c]+0 }' | sort
```

---

## Build Output

```
dotnet build experiments/oss-driver/TicTacToe.OssDriver.fsproj

Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Syntax Check

```
bash -n experiments/oss-driver/aggregate.sh
syntax OK
```

---

## aggregate.sh Run: /tmp/rf-122b-plain

```
records: 30   anomalies(dropped): 2   clean: 28   (dir /tmp/rf-122b-plain)
=== DROPPED anomalies (cell run reason) ===
  0100 r5  seatCrash
  1000 r3  seats<2 seatCrash
=== per-cell over CLEAN games (n = clean count) ===
  cell   n  compl%  cost$/game  tok/game  |  invalid: total  (move/outturn/postaken)  struct
  0000   5   100%    0.04582    104052  |    3.6  (0.4/2.4/0.8)    1.6
  0001   5     0%    0.03833     73742  |    3.4  (0.0/2.2/1.2)    1.0
  0010   5   100%    0.06528    154766  |    5.0  (0.0/3.0/2.0)    4.2
  0100   4   100%    0.08070    169515  |    5.5  (2.2/2.0/1.2)    3.8
  1000   4   100%    0.05979    107483  |    1.0  (0.0/0.0/1.0)    1.0
  1111   5   100%    0.07831    140480  |    1.8  (0.0/0.0/1.8)    2.6
=== incomplete breakdown (seat-level window_truncated vs stalled) ===
  0000  seat-truncations=0  (of 10 seated-seats)  games-with-any=0  stalled=0
  0001  seat-truncations=0  (of 10 seated-seats)  games-with-any=0  stalled=5
  0010  seat-truncations=0  (of 10 seated-seats)  games-with-any=0  stalled=0
  0100  seat-truncations=0  (of 8 seated-seats)  games-with-any=0  stalled=0
  1000  seat-truncations=0  (of 8 seated-seats)  games-with-any=0  stalled=0
  1111  seat-truncations=0  (of 10 seated-seats)  games-with-any=0  stalled=0
=== info-seeking per cell (mean agent GETs across clean-game seats) ===
  0000  /strategy=0.00  /profile=0.20  (seats=15)
  0001  /strategy=1.00  /profile=0.00  (seats=15)
  0010  /strategy=0.00  /profile=1.13  (seats=15)
  0100  /strategy=0.00  /profile=0.33  (seats=12)
  1000  /strategy=0.00  /profile=0.00  (seats=13)
  1111  /strategy=0.20  /profile=1.27  (seats=15)
```

### Why seat-truncations=0 (not the expected nonzero)

The 1 window_truncated seat (`s-1000-r3-X1.out`) belongs to game `1000-r3`, which was tagged anomalous (`seats<2 seatCrash`) and DROPPED from the clean set. The breakdown only reports clean games (`select(.anomaly|not)`). There are genuinely zero seat-truncations among the 28 clean games in this directory. The script is correct; the per-task expectation ("should show a small nonzero") was based on a misread of which game the truncated seat belongs to.
