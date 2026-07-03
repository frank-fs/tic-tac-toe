# Reads-Free Observation Windows + So Discovery Channel — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the harness caps agent-blind observation windows (reads free, mutations bounded), move So's ontology onto the Link-header channel agents actually follow, restore the browser arm, and re-run the 35b tier to observe the effect.

**Architecture:** F# ReAct driver (`Driver.fs`) splits its single turn-budget into a POST-attempt bound + a loose total-turn backstop; the Surface web app advertises `/strategy` via a So-gated `Link` header instead of a per-turn inline JSON-LD blob; bash glue (`sweep.sh`/`aggregate.sh`) carries the new bounds + a plain/browser arm switch and reports retrieval/truncation observations.

**Tech Stack:** F# / .NET 10, Oxpecker.ViewEngine, xUnit (existing `TicTacToe.Web.Surface.Tests`), bash + jq + awk, OpenRouter via `WORKER_*`.

## Global Constraints

- One language: F# for driver + measurement subcommands; `sweep.sh`/`aggregate.sh` are existing bash glue, extended in-kind — no new language.
- Frozen prompts stay hash-locked; any restored prompt gets a committed `.sha256`.
- Factor orthogonality: A, C, Sd, So each own an independent channel; So's Link header must NOT depend on Sd's `/profile`.
- Holzmann: nesting ≤2, every loop bounded (R10), functions ≤60 lines / one job, no module-level mutable.
- Experiment code stays under `experiments/`. Prefer run-based validation; add a test only where it guards real logic.
- The agent is never told any budget — add no budget text to prompts or driver observations.
- Work on branch `reads-free` in worktree `.claude/worktrees/reads-free`. Build: `dotnet build experiments/oss-driver/TicTacToe.OssDriver.fsproj` and `dotnet build experiments/src/TicTacToe.Web.Surface`.

---

### Task 1: Driver — agent-blind reads-free budget split

**Files:**
- Modify: `experiments/oss-driver/Driver.fs:16-28` (Config), `:176-231` (run loop), `:245-257` (result JSON)
- Modify: `experiments/oss-driver/Program.fs:37`

**Interfaces:**
- Produces: `Driver.Config` fields `MaxAttempts: int`, `MaxTurns: int` (replacing `MaxActions: int`); result JSON gains `attempts` (POST count) and `turns` (loop iterations); `outcome` gains value `"window_truncated"` for a non-terminal bound exit.
- Consumes: nothing new.

- [ ] **Step 1: Replace the Config budget field**

In `Driver.fs`, change the record field (was `MaxActions: int`):

```fsharp
      MaxAttempts: int      // mutation-attempt budget: POSTs (accepted+rejected). Bounds thrash.
      MaxTurns: int         // total-iteration backstop incl. GETs (R10). Agent never sees either.
```

- [ ] **Step 2: Split the loop bound and count only POSTs**

In `Driver.fs run`, add an attempts counter beside `moves` (after `let mutable moves = 0`):

```fsharp
    let mutable attempts = 0
```

Change the loop header (was `while not stop && step < cfg.MaxActions do`):

```fsharp
    while not stop && attempts < cfg.MaxAttempts && step < cfg.MaxTurns do
```

In the `Some(m, path, body)` branch, increment `attempts` for every POST (accepted or rejected) — place it right after the accepted-move count line (`if m = "POST" && status < 400 then moves <- moves + 1`):

```fsharp
            if m = "POST" then attempts <- attempts + 1
```

- [ ] **Step 3: Label a non-terminal bound exit as window_truncated**

In `Driver.fs`, immediately after the `while` loop closes (before the transcript-persist block), add:

```fsharp
    // Both bounds are agent-invisible observation windows; hitting one without a terminal
    // outcome is a truncated observation, NOT a play failure (R10 backstop tripped).
    if not stop && outcome = "incomplete" then outcome <- "window_truncated"
```

- [ ] **Step 4: Emit attempts + turns in the result JSON**

In `Driver.fs`, alongside `res["moves_submitted"]` add:

```fsharp
    res["attempts"] <- JsonValue.Create attempts
    res["turns"] <- JsonValue.Create step
```

(Keep `res["actions"] <- JsonValue.Create step` for backward-compatible reading.)

- [ ] **Step 5: Wire the two args in Program.fs**

In `Program.fs`, replace the `MaxActions = ...` line (`:37`) with:

```fsharp
          MaxAttempts = argVal argv "--max-attempts" "30" |> int
          MaxTurns = argVal argv "--max-turns" "80" |> int
```

- [ ] **Step 6: Build**

Run: `dotnet build experiments/oss-driver/TicTacToe.OssDriver.fsproj`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 7: Mechanical smoke — GETs don't spend the mutation budget**

This verifies the counter, not experiment data (a single seat is fine for a mechanical check). Start a surface arena, run one seat with a tiny turn backstop and generous attempts, and read the result JSON.

Run:
```bash
GID=$(CELL=0010 bash experiments/oss-driver/arena.sh up surface | grep '^GAME_ID=' | cut -d= -f2)
dotnet run --project experiments/oss-driver --no-build -- --role X --arm http \
  --base http://127.0.0.1:6328 --route arenas --game "$GID" --coldstart \
  --model qwen/qwen3.5-35b-a3b --max-attempts 30 --max-turns 12 2>/dev/null | \
  jq '{attempts,turns,actions,outcome,moves_submitted}'
bash experiments/oss-driver/arena.sh down surface
```
Expected: JSON where `turns` ≥ `attempts` (GETs counted turns but not attempts), and `outcome` is `"window_truncated"` if it stopped on the `--max-turns 12` backstop without a terminal. Confirms reads are free against the mutation budget.

- [ ] **Step 8: Commit**

```bash
git add experiments/oss-driver/Driver.fs experiments/oss-driver/Program.fs
git commit -m "feat(oss-driver): split turn budget into agent-blind MaxAttempts + MaxTurns; reads free"
```

---

### Task 2: So surfaces its ontology via a Link header (drop per-turn JSON-LD)

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Surface/Handlers.fs:39-43` (header helper), `:178` (arena GET), `:341-351` (profile cross-link)
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/game.fs:236` (drop inline ontology call)
- Test: `experiments/test/TicTacToe.Web.Surface.Tests/RenderFactorTests.fs:64-79`

**Interfaces:**
- Produces: response header `Link: </strategy>; rel="subjectOf"` on the arena resource when `surface.So`; So render no longer contains `application/ld+json`; `/strategy` route unchanged (200 when So, 404 else).
- Consumes: `Surface.So` flag; existing `setDiscoveryHeaders` pattern.

- [ ] **Step 1: Update the render tests to the new So behavior**

In `RenderFactorTests.fs`, replace the `So1 emits a schema.org Game JSON-LD block ...` test (`:69-79`) with — So render must now carry NO inline JSON-LD (discoverability moved to the Link header):

```fsharp
[<Fact>]
let ``So1 render carries no inline JSON-LD (discoverability moved to the Link header)`` () =
    let out = renderArenaPage (cell false false false true) "g" (freshXTurn()) "userX" seated None |> html
    Assert.DoesNotContain("application/ld+json", out)
    Assert.DoesNotContain("subjectOf", out)
```

(Leave `So0 emits no JSON-LD` as-is — still true.)

- [ ] **Step 2: Run the tests to verify the new one fails**

Run: `dotnet test experiments/test/TicTacToe.Web.Surface.Tests/ --filter "FullyQualifiedName~RenderFactorTests"`
Expected: FAIL — the So render still emits `application/ld+json` (old `renderOntology`).

- [ ] **Step 3: Drop the per-turn inline ontology**

In `templates/game.fs`, remove the ontology injection (was `:236`):

```fsharp
        if surface.So then renderOntology () else Fragment() { }
```

Delete that line. Then delete the now-unused `renderOntology` function (`:167-183`). (If the F# compiler flags `renderOntology` as still referenced elsewhere, grep first: `grep -rn renderOntology experiments/src` — it should have exactly one call site.)

- [ ] **Step 4: Run the render tests to verify they pass**

Run: `dotnet test experiments/test/TicTacToe.Web.Surface.Tests/ --filter "FullyQualifiedName~RenderFactorTests"`
Expected: PASS (both So tests + all A/C tests).

- [ ] **Step 5: Add the So Link-header helper and emit it on the arena resource**

In `Handlers.fs`, after `setDiscoveryHeaders` (`:43`), add:

```fsharp
/// So: advertise the domain-knowledge article as a followable link (the channel agents
/// actually use — inline JSON-LD was observed ignored). Independent of Sd's /profile.
let private setStrategyHeader (ctx: HttpContext) =
    ctx.Response.Headers.Append("Link", "</strategy>; rel=\"subjectOf\"")
```

At the arena GET (`:178`), add the So branch right after the Sd branch:

```fsharp
            if surface.Sd then setDiscoveryHeaders ctx $"/arenas/{arenaId}" (Some (arenaAllow result))
            if surface.So then setStrategyHeader ctx
```

- [ ] **Step 6: Cross-link /strategy from /profile only when Sd∩So**

In `Handlers.fs` `profile` (`:348`, inside the `else` where Sd is on), add after `setDiscoveryHeaders`:

```fsharp
            if surface.So then setStrategyHeader ctx
```

(This fires only in the Sd∩So cell — `/profile` is Sd-gated — so So-alone stays header-only on the arena.)

- [ ] **Step 7: Build**

Run: `dotnet build experiments/src/TicTacToe.Web.Surface`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 8: Run-verify the header channel (So-on vs So-off vs Sd∩So)**

```bash
GID=$(CELL=0001 bash experiments/oss-driver/arena.sh up surface | grep '^GAME_ID=' | cut -d= -f2)   # So only
echo "So=1 arena Link:"; curl -sI "http://localhost:5328/arenas/$GID" | grep -i '^link:'
echo "So=1 /strategy status:"; curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5328/strategy
bash experiments/oss-driver/arena.sh down surface

GID=$(CELL=0000 bash experiments/oss-driver/arena.sh up surface | grep '^GAME_ID=' | cut -d= -f2)   # So off
echo "So=0 arena Link (expect none):"; curl -sI "http://localhost:5328/arenas/$GID" | grep -i '^link:' || echo "  (none)"
echo "So=0 /strategy status (expect 404):"; curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5328/strategy
bash experiments/oss-driver/arena.sh down surface
```
Expected: So=1 arena carries `Link: </strategy>; rel="subjectOf"` and `/strategy` → 200; So=0 has no strategy Link and `/strategy` → 404.

- [ ] **Step 9: Commit**

```bash
git add experiments/src/TicTacToe.Web.Surface/Handlers.fs experiments/src/TicTacToe.Web.Surface/templates/game.fs experiments/test/TicTacToe.Web.Surface.Tests/RenderFactorTests.fs
git commit -m "feat(surface): So advertises /strategy via Link header; drop per-turn inline JSON-LD"
```

---

### Task 3: Restore the browser prompt as a selectable arm

**Files:**
- Restore: `experiments/oss-driver/coldstart-browser-prompt.md`, `experiments/oss-driver/directed-browser-prompt.md` (from `e3ffeba^`)
- Create: matching `.sha256` locks
- Modify: `experiments/oss-driver/sweep.sh` (PROMPT switch)

**Interfaces:**
- Produces: `sweep.sh` env `PROMPT=plain|browser`; when `browser`, exports `COLDSTART_PROMPT_PATH`/`DIRECTED_PROMPT_PATH` (env the driver already reads, `Driver.fs:104-114`) to the browser files.
- Consumes: nothing new in F#.

- [ ] **Step 1: Recover the two prompt files from history**

```bash
git show e3ffeba^:experiments/oss-driver/coldstart-browser-prompt.md > experiments/oss-driver/coldstart-browser-prompt.md
git show e3ffeba^:experiments/oss-driver/directed-browser-prompt.md > experiments/oss-driver/directed-browser-prompt.md
```
Confirm non-empty: `wc -l experiments/oss-driver/*-browser-prompt.md` (both > 0).

- [ ] **Step 2: Verify the restored prompts carry no budget/limit language**

Run: `grep -in "action\|budget\|limit\|turn\b\|attempt\|steps\|tries" experiments/oss-driver/coldstart-browser-prompt.md experiments/oss-driver/directed-browser-prompt.md || echo "clean"`
Expected: no budget wording (per Global Constraints — agent stays budget-blind). If any is found, STOP and surface it — do not edit a prompt silently.

- [ ] **Step 3: Generate the hash locks**

```bash
for f in coldstart-browser-prompt directed-browser-prompt; do
  shasum -a 256 experiments/oss-driver/$f.md | awk '{print $1}' > experiments/oss-driver/$f.md.sha256
done
```

- [ ] **Step 4: Add the PROMPT switch to sweep.sh**

In `sweep.sh`, near the env defaults (after the `MAX_ACTIONS`/bounds block), add:

```bash
PROMPT="${PROMPT:-plain}"                # plain (cold-start instrument) | browser (re-test arm)
if [ "$PROMPT" = "browser" ]; then
  export COLDSTART_PROMPT_PATH="$REPO/experiments/oss-driver/coldstart-browser-prompt.md"
  export DIRECTED_PROMPT_PATH="$REPO/experiments/oss-driver/directed-browser-prompt.md"
fi
echo "$PROMPT" > "$D/.prompt"
```

- [ ] **Step 5: Verify each restored prompt matches its lock**

The driver's `verifyPromptLock` fails a run if the bytes drift from the `.sha256`. Confirm the locks match the restored bytes:
```bash
for f in coldstart-browser-prompt directed-browser-prompt; do
  exp=$(cat experiments/oss-driver/$f.md.sha256)
  act=$(shasum -a 256 experiments/oss-driver/$f.md | awk '{print $1}')
  [ "$exp" = "$act" ] && echo "$f: lock OK" || echo "$f: LOCK MISMATCH"
done
```
Expected: both `lock OK`.

- [ ] **Step 6: Commit**

```bash
git add experiments/oss-driver/coldstart-browser-prompt.md experiments/oss-driver/directed-browser-prompt.md experiments/oss-driver/coldstart-browser-prompt.md.sha256 experiments/oss-driver/directed-browser-prompt.md.sha256 experiments/oss-driver/sweep.sh
git commit -m "feat(oss-driver): restore browser prompt as PROMPT=browser arm (locked) for re-test"
```

---

### Task 4: Harness — new bounds in sweep.sh + observation columns in aggregate.sh

**Files:**
- Modify: `experiments/oss-driver/sweep.sh` (pass `--max-attempts`/`--max-turns`, record meta)
- Modify: `experiments/oss-driver/aggregate.sh` (window_truncated label; strategy/profile fetch columns)

**Interfaces:**
- Consumes: driver result `outcome` may be `"window_truncated"`; per-seat transcript `t-*.jsonl` for fetch observation.
- Produces: `aggregate.sh` reports a `window_truncated` count in the incomplete breakdown and per-cell `/strategy` + `/profile` fetch rates.

- [ ] **Step 1: sweep.sh — replace the single cap with the two bounds**

In `sweep.sh`, replace the `MAX_ACTIONS` block with:

```bash
MAX_ATTEMPTS="${MAX_ATTEMPTS:-30}"      # POST-attempt budget (mutations). Generous: play never nears it.
MAX_TURNS="${MAX_TURNS:-80}"            # total-turn backstop incl. free GETs (R10). Agent-invisible.
```
And in the driver invocation, replace `--max-actions "$MAX_ACTIONS"` with:

```bash
        --coldstart --model "$MODEL" --max-attempts "$MAX_ATTEMPTS" --max-turns "$MAX_TURNS" \
```
And replace the `.maxactions` record line with:

```bash
printf 'attempts=%s turns=%s\n' "$MAX_ATTEMPTS" "$MAX_TURNS" > "$D/.bounds"
```

- [ ] **Step 2: aggregate.sh — count window_truncated in the incomplete breakdown**

In `aggregate.sh`, the incomplete-breakdown awk currently splits capped vs stalled by `capHit`. Replace its body so it reads the driver `outcome` directly (now authoritative):

```bash
jq -rc 'select(.anomaly|not) | [.cell,.outcome] | @tsv' "$OUT" | awk -F'\t' '
{ c=$1; seen[c]=1
  if($2=="draw"||$2=="x_wins"||$2=="o_wins") ; 
  else if($2=="window_truncated") tr[c]++
  else st[c]++ }
END{ for(c in seen) printf "  %-5s truncated(window)=%d  stalled=%d\n", c, tr[c]+0, st[c]+0 }' | sort
```
(Keep the existing per-cell primary table unchanged.)

- [ ] **Step 3: aggregate.sh — add /strategy + /profile fetch rates per cell**

After the primary per-cell table, add a block that counts agent GETs from the transcripts:

```bash
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
```

- [ ] **Step 4: Syntax-check both scripts**

Run: `bash -n experiments/oss-driver/sweep.sh && bash -n experiments/oss-driver/aggregate.sh && echo OK`
Expected: `OK`.

- [ ] **Step 5: Commit**

```bash
git add experiments/oss-driver/sweep.sh experiments/oss-driver/aggregate.sh
git commit -m "feat(oss-driver): sweep passes attempts+turns bounds; aggregate reports truncation + info-seeking"
```

---

### Task 5: Validate — re-run 35b under the new regime (plain + browser)

**Files:** none (execution + observation).

**Interfaces:** consumes everything above.

- [ ] **Step 1: Build the driver release path used by the sweep**

Run: `dotnet build experiments/oss-driver/TicTacToe.OssDriver.fsproj && dotnet build experiments/src/TicTacToe.Web.Surface`
Expected: both `Build succeeded`.

- [ ] **Step 2: Run 35b PLAIN under the new bounds**

Run: `MODEL=qwen/qwen3.5-35b-a3b PROMPT=plain SWEEP_OUT=/tmp/qwen-35b-rf bash experiments/oss-driver/sweep.sh`
Expected: `SWEEP COMPLETE`. (Background it if long; monitor the first game for auth/move activity as before.)

- [ ] **Step 3: Run 35b BROWSER under the new bounds**

Run: `MODEL=qwen/qwen3.5-35b-a3b PROMPT=browser SWEEP_OUT=/tmp/qwen-35b-rf-browser bash experiments/oss-driver/sweep.sh`
Expected: `SWEEP COMPLETE`.

- [ ] **Step 4: Aggregate both and compare to the banked 25-cap 35b**

```bash
echo "### PLAIN (reads-free)";   bash experiments/oss-driver/aggregate.sh /tmp/qwen-35b-rf
echo "### BROWSER (reads-free)"; bash experiments/oss-driver/aggregate.sh /tmp/qwen-35b-rf-browser
echo "### OLD 25-cap plain";     bash experiments/oss-driver/aggregate.sh /tmp/qwen-35b
```

- [ ] **Step 5: Check the success criteria (from the spec) and record findings**

Confirm, from the aggregate output:
1. `turns` ≥ `attempts` in seat results (reads didn't spend the mutation budget).
2. Window-truncation rate dropped sharply vs the old 22/60 cap-hits; completion reflects natural termination.
3. `/strategy` fetch rate is observable (non-zero where the model chooses to fetch, or cleanly zero — no longer masked by clutter).
4. Browser vs plain compared from fresh data — rejection confirmed or overturned.

Append a dated results section to `experiments/oss-driver/FINDINGS.md` with the plain/browser/old tables and the verdict on each criterion. Commit:

```bash
git add experiments/oss-driver/FINDINGS.md
git commit -m "docs(oss-driver): 35b reads-free results — completion vs cost/invalid/retrieval; browser re-test"
```

---

## Notes for the executor

- If Task 2 Step 3 finds `renderOntology` referenced by more than the one call site, do NOT delete it — only remove the call and leave the function; surface the extra reference.
- The 35b runs need `WORKER_API_KEY`/`WORKER_BASE_URL` in the environment (1Password-resolved in the user's shell). Never echo the key value.
- Single-seat runs appear only in Task 1 Step 7 as a mechanical counter smoke — every experiment sweep (Task 5) uses the full 3-seat harness.
