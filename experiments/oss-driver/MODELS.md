# Model tier ladder (oss-driver, OpenRouter-swappable)

Descent from a proven anchor; **model is the only variable** (same driver, same
cold-start prompt, same surface cell). Dispatch = `oss-driver` with `WORKER_*` env
for OpenRouter, or the Anthropic backend for the haiku reference. Source ladder:
`docs/superpowers/specs/2026-06-23-cold-start-discovery-reset-design.md` §2.2 — which
left the **mid tier unpinned** ("settle at Phase-2 start"). This file pins it.

| Tier | Model | OpenRouter id | Notes |
|---|---|---|---|
| anchor/top | Haiku-4.5 | (Anthropic native, or `anthropic/claude-3.5-haiku`) | proven proficient; not re-run via paid OpenRouter — already covered by subscription. Bump back up only if a lower rung is ambiguous. |
| **mid** | **Llama-3.1-70B-Instruct** | `meta-llama/llama-3.1-70b-instruct` | the previously-missing in-between rung. Same family as the small-end 8b → within-family descent, cleanest comparison. **Start the OpenRouter descent here.** |
| small | Llama-3.1-8B-Instruct | `meta-llama/llama-3.1-8b-instruct` | validated weak-end; showed the thesis signal (Simple-excludes / Proto-rescues). The `WORKER_MODEL` env default. |
| (small, alt) | gemma-4-e4b · Qwen2.5-7B · gemma-2-9b | per OpenRouter id | other weak-end probes from the design / prior sweep. |

## Run a rung

```
WORKER_BASE_URL=https://openrouter.ai/api/v1  WORKER_API_KEY=…  \
WORKER_MODEL=meta-llama/llama-3.1-70b-instruct \
dotnet run --project experiments/oss-driver -- \
  --coldstart --role X --base http://localhost:6328 --route arenas --game <id>
```

`--model` overrides `WORKER_MODEL`. Drop a rung when a surface still succeeds; bump
up when it breaks ambiguously. Record the floor model per surface (Phase-2 titration).

## Open design note (persona / mastery layer)

Personas (`Personas.fs`) are **dropped in `--coldstart`** because the expert guidance
("take the center, then corners, create forks") leaks the game identity and strategy,
defeating discovery. If a mastery layer returns for cold-start, its guidance must be
**game-agnostic** (strategies spanning multiple games, not tic-tac-toe-specific) so it
does not reveal what the model is supposed to discover.
