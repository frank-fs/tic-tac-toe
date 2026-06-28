# Model tier ladder (oss-driver, OpenRouter)

Descent from a proven anchor; **model is the only variable** (same driver, same
cold-start prompt, same surface cell). Dispatch = `oss-driver` with `WORKER_*` env.
Order rungs by **capability**, NOT raw param count — the top tier is MoE (total ≠
active), which is fine: a MoE reasons ~like its *active* size but knows ~like its
*total* size, and it is *cheaper* to run. Dense rungs give a clean low end.

**Family held constant = Qwen 3.5** (spans haiku-class → small in one lineage).
Vary family only as a separate probe at ONE fixed size, never woven through the
descent (else size and architecture confound). Slugs + pricing confirmed live on
OpenRouter 2026-06-28 (`/api/v1/models`).

| Rung | Slug | Type | params | $/M prompt · completion |
|---|---|---|---|---|
| ceiling | `qwen/qwen3.5-397b-a17b` | MoE | 397B / 17B act | 0.385 · 2.45 |
| anchor (haiku-class) | `qwen/qwen3.5-122b-a10b` | MoE | 122B / 10B act | 0.26 · 2.08 |
| mid (MoE) | `qwen/qwen3.5-35b-a3b` | MoE | 35B / 3B act | 0.14 · 1.00 |
| mid (dense) | `qwen/qwen3.5-27b` | dense | 27B | 0.195 · 1.56 |
| low (dense) | `qwen/qwen3.5-9b` | dense | 9B | 0.10 · 0.15 |
| floor | `qwen/qwen3.5-flash-02-23` | small | (flash) | 0.065 · 0.26 |

No 3.5 4B/2B hosted on OpenRouter — `9b` / `flash` are the floor. (Anthropic haiku
stays the off-ladder reference; not re-paid via OpenRouter — already proven + on
subscription. Bump there only to disambiguate a rung.)

**MoE-vs-dense comparison** (a deliberate interest): the two mid rungs `35b-a3b`
(MoE, 3B active) and `27b` (dense) sit at the same tier/generation — running both
isolates architecture from size.

## Run a rung

```
WORKER_BASE_URL=https://openrouter.ai/api/v1  WORKER_API_KEY=…  \
WORKER_MODEL=qwen/qwen3.5-27b \
dotnet run --project experiments/oss-driver -- \
  --coldstart --role X --base http://localhost:6328 --route arenas --game <id>
```

`--model` overrides `WORKER_MODEL`. Descend until a surface starts producing errors;
record the floor model per surface (Phase-2 titration). Expectation under the
corrected thesis: richer surface (Sd, then C+So) → **fewer move-format errors** for
the same model.

## Cross-family probe (optional, separate cell — NOT in the descent)

Same fixed size, different families, to read architecture effects:
`qwen/qwen3.5-27b` vs `google/gemma-3-27b-it` vs (a ~30B other). Also available
small: `google/gemma-3-12b-it`, `google/gemma-3-4b-it`, `mistralai/mistral-nemo`
(12B), `meta-llama/llama-3.3-70b-instruct`, `moonshotai/kimi-k2.6`.

## Persona note

Personas (`Personas.fs`) are dropped in `--coldstart` — expert guidance ("center,
corners, forks") leaks the game. A cold-start mastery layer, if reintroduced, must
be game-agnostic.
