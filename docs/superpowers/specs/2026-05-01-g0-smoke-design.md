# G0 Smoke Test Design

**Issue:** [#41](https://github.com/frank-fs/tic-tac-toe/issues/41)
**Date:** 2026-05-01
**Status:** Design approved

---

## Purpose

G0 is a one-time gate check between the H-wave harness work (H0–H6) and the F-axis measurement matrix (F0–F8). It verifies that the orchestrator pipeline is sound end-to-end before launching the ~217-cell matrix.

G0 does **not** measure whether RPVA is good or bad — that is the F-curve's job. It only checks that every component of the harness produces valid, correctly-shaped output without errors.

The model substitution (Qwen2.5-14B-Instruct instead of the originally specified Haiku) is intentional:
- Validates the LM Studio path (`ANTHROPIC_BASE_URL`) simultaneously
- Avoids Anthropic API cost for a smoke run
- 14B is strong enough on multi-turn tool use to give the HTTP discovery cells a real chance of completing

---

## Cell Matrix

Four canary cells, one game each, `--temperature 0.0`, model `qwen2.5-14b-instruct`:

| Output file | Variant | Setup | Notes |
|-------------|---------|-------|-------|
| `smoke-proto-e1.json` | proto | E1 | beginner persona + HTTP discovery |
| `smoke-proto-e0.json` | proto | E0 | no system prompt, cold discovery |
| `smoke-simple-e1.json` | simple | E1 | V_simple — same paths, no HATEOAS |
| `smoke-erpc.json` | — | E_RPC | no server; engine-backed tools |

The orchestrator manages server lifecycle for the three HTTP cells automatically via `--commit HEAD` (`ServerProcess.fs` runs `dotnet publish` and starts the server on a dynamic port). No manual server startup required.

---

## Environment

```powershell
$env:ANTHROPIC_BASE_URL = "http://127.0.0.1:1234"
$env:ANTHROPIC_API_KEY  = "lm-studio"   # any non-empty string; LM Studio ignores it
```

LM Studio configuration:
- Model: `qwen2.5-14b-instruct`
- Context length: **32768** (not the default 4096)
- API format: Anthropic `/v1/messages`

---

## Script

`experiments/scripts/smoke.ps1` (pwsh, cross-platform):

1. **Environment check** — verifies/defaults `ANTHROPIC_BASE_URL` and `ANTHROPIC_API_KEY`
2. **Output directory** — creates `experiments/results/smoke/` if absent
3. **Four orchestrator invocations** — sequential, exit code captured per cell
4. **Validation pass** — inline Python (stdlib only) checks each output file
5. **Summary table** — cell / exit-code / RPVA / invalid_rate / abandon_rate / PASS·FAIL, exits 1 if any failure

The `$Model` variable is defined at the top of the script for easy substitution.

---

## Validation Criteria

Per the G0 pass criteria from issue #41:

| Criterion | How checked |
|-----------|-------------|
| All cells complete without error | Exit code 0 per `dotnet run` invocation |
| Expected JSON shape | `cell`, `games`, `aggregate` keys present in each file |
| RPVA is finite | `math.isfinite(d['aggregate']['rpva'])` |
| Strategy populated for every request | Every HTTP transcript entry has a non-null `strategy` field |
| E_RPC: tool entries only | No transcript entry has a `status` key; all have `tool_name` |
| V_simple path parity | Path sets from `smoke-simple-e1.json` and `smoke-proto-e1.json` extracted and printed for inspection |
| Blind-POST rate | Computed from transcript (`strategy == "BlindPost"` ÷ total HTTP entries); printed per cell |

**Known gap:** `blind_post_rate` is not a field in `Aggregate` (the orchestrator computes invalid_rate and abandon_rate but not strategy breakdown). The validation script computes it from the transcript. Adding `blind_post_rate` to `Aggregate` is filed as a follow-up item.

---

## Key Observations (for spec context)

### Hypermedia is stateless per turn

HATEOAS responses re-express full game state through affordances — the model can act correctly from the current response alone without retaining prior turns. Prior context is strategy/planning headroom, not a correctness requirement. This inverts the intuition about context length:

- **E0/E1**: accumulates bytes (full HTTP bodies), but each decision needs only the latest response
- **E_RPC**: produces compact tool outputs, but the model must track game state across turns from accumulated history

### Context length as a future experimental axis

Context window size is a meaningful variable for this experiment — not just a capacity limit:

- **E_RPC** needs history for correctness (state tracking without affordances)
- **E0/E1** needs it for strategy (remembering prior failures, adapting — vs reacting turn-by-turn with no memory)

A context-starved E0/E1 agent may follow affordances reactively (lower RPVA, higher invalid rate from no learning); a context-rich agent can plan and adapt (potentially lower invalid rate). This makes 4096 vs 32768 a controlled variable worth adding to the F-axis if results are thin.

G0 uses 32768 throughout and does not vary context length. This observation is noted for future measurement, not acted on here.

---

## Model Inventory

Models available locally for this experiment:

| Model | Size | Role |
|-------|------|------|
| Qwen2.5-14B-Instruct (Q3_K_L) | 14B | G0 primary; F-axis measurement; fits fully on RTX 3090 (24GB); 1M context ceiling |
| Gemma 4 E4B | ~4B | F-axis cross-family comparison |

Notes:
- Q3_K_L quantization is a quality trade-off vs Q4; on a 14B model the difference is small and full-GPU inference is worth it over partial offload
- The 1M context ceiling is available but not used for G0 — tic-tac-toe games fit well within 32768. Reserve for the context-length axis experiment
- Coder vs Instruct variants are a meaningful experimental axis (Coder favors E_RPC structured tool use; Instruct favors E0/E1 hypermedia navigation) — add when a larger machine is available
- Qwen2.5-Coder-1.5B-Instruct (128K context) is a candidate for the context-length axis experiment
- Qwen2.5-32B-Instruct requires partial GPU offload on the current RTX 3090 (24GB VRAM)

---

## Follow-Up Handling

Any criterion failure or code gap surfaces during the smoke run gets a GitHub issue filed before closing G0. The script prints a `FOLLOW-UP ITEMS` section at the end. Known pre-existing item:

- [ ] Add `blind_post_rate` to `Aggregate` in `Types.fs` and `Metrics.fs`

---

## Acceptance Criteria

- [ ] All four canary cells complete without orchestrator errors
- [ ] Output files committed to `experiments/results/smoke/`
- [ ] Validation pass prints all PASS (or failures are filed as issues)
- [ ] Decision recorded as comment on issue #41: "pipeline is sound → proceed to F0" or "blocked on issue #X"
- [ ] Issue #41 closed
