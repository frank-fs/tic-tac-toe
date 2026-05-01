# TicTacToe Orchestrator

A CLI tool that drives a Claude-compatible language model against the tic-tac-toe server, captures full interaction transcripts, and outputs per-game metrics as structured JSON. Used in the H2 harness of the agent-hypothesis experiment to measure how different model/setup combinations navigate HTTP affordances versus direct RPC tools.

---

## Design

### What it measures

The orchestrator quantifies three things per run:

| Metric | Description |
|--------|-------------|
| **RPVA** | Requests Per Valid Action — total API calls ÷ successful game moves. Lower is better; measures discovery overhead. |
| **Invalid rate** | Fraction of requests that returned an error (4xx / tool error). Signals misuse of affordances. |
| **Abandon rate** | Fraction of games where the agent made zero valid moves (hit the 50-turn limit without playing). |
| **Tokens** | Average tokens consumed per game (input + output). |

### Agent setups

| Setup | System prompt | Interface | Use |
|-------|--------------|-----------|-----|
| **E0** | None | `http_request` tool | Baseline: can the model discover the API cold? |
| **E1** | Persona file (`experiments/personas/`) | `http_request` tool | Measures persona effect on HTTP discovery. |
| **E_RPC** | Minimal fixed prompt | 4 game tools backed by `TicTacToe.Engine` | Null hypothesis: removes HTTP entirely, measures pure game-play capability. |

### Architecture

```
Program.fs          CLI arg parsing → RunConfig
Runner.fs           N-game loop, persona prompt loading, server lifecycle
├── HttpAgent.fs    E0/E1: http_request tool loop, transcript recording
│   ├── Classifier.fs   outcome (ValidAction/InvalidAction/Discovery/Retry)
│   └── Classifier.fs   strategy (HtmlFollow/SpecFollow/BlindPost)
└── RpcAgent.fs     E_RPC: new_game/get_board/make_move/get_state backed by engine
Metrics.fs          RPVA, invalid_rate, abandon_rate, token aggregation
AnthropicClient.fs  Direct HTTP to /v1/messages (no SDK); tool-use cycle
ServerProcess.fs    dotnet publish + subprocess on free port; git worktree per commit
```

Every tool call or HTTP request becomes a `TranscriptEntry` in the output JSON, tagged with its outcome and (for HTTP) the strategy the agent used to produce the URL.

### Output shape

```json
{
  "cell": { "commit": "HEAD", "variant": "proto", "model": "haiku", "persona": "beginner", "setup": "E1" },
  "games": [
    {
      "transcript": [
        { "turn": 1, "method": "GET", "url": "http://localhost:5228/", "status": 200, "outcome": "Discovery", "strategy": "BlindPost", ... },
        { "turn": 2, "method": "POST", "url": "http://localhost:5228/arenas", "status": 201, "outcome": "ValidAction", "strategy": "HtmlFollow", ... }
      ],
      "metrics": { "rpva": 2.5, "invalid_rate": 0.1, "abandoned": false, "tokens": 4210 }
    }
  ],
  "aggregate": { "rpva": 2.5, "invalid_rate": 0.1, "abandon_rate": 0.0, "tokens": 4210.0 }
}
```

---

## Prerequisites

- .NET 10 SDK
- An `ANTHROPIC_API_KEY` **or** a local LM Studio server (see below)

---

## Usage with Anthropic

```bash
export ANTHROPIC_API_KEY=sk-ant-...

# E_RPC run — no server needed, calls the engine directly
dotnet run --project experiments/orchestrator/ -- run \
  --setup E_RPC \
  --model haiku \
  --persona beginner \
  --games 5 \
  --output results/erpc-haiku.json

# E1 HTTP run — server must be running first
dotnet run --project src/TicTacToe.Web/ &   # or TicTacToe.Web.Simple on port 5328

dotnet run --project experiments/orchestrator/ -- run \
  --commit HEAD \
  --variant proto \
  --model sonnet \
  --persona expert \
  --setup E1 \
  --games 5 \
  --temperature 0.0 \
  --output results/e1-sonnet-expert.json
```

### All flags

| Flag | Default | Values |
|------|---------|--------|
| `--commit` | `HEAD` | any git SHA or `HEAD` |
| `--variant` | `proto` | `proto`, `simple` |
| `--model` | `haiku` | `haiku`, `sonnet`, `opus`, or any model name string |
| `--persona` | `beginner` | `beginner`, `expert`, `chaos` |
| `--setup` | `E1` | `E0`, `E1`, `E_RPC` |
| `--games` | `3` | positive integer |
| `--output` | `run.json` | file path |
| `--temperature` | `0.0` | float 0.0–1.0 |

---

## Usage with LM Studio

LM Studio (v0.3.6+) exposes an Anthropic-compatible endpoint. The orchestrator speaks the Anthropic Messages API natively, so no adapter is needed.

**Setup:**

1. Download [LM Studio](https://lmstudio.ai) and load a model.
2. In the **Local Server** tab, enable the server and select **Anthropic** as the API format.
3. Note the port (default `1234`).

**Run:**

```bash
export ANTHROPIC_BASE_URL=http://localhost:1234
export ANTHROPIC_API_KEY=lm-studio   # any non-empty string; LM Studio ignores it

dotnet run --project experiments/orchestrator/ -- run \
  --setup E_RPC \
  --model "qwen2.5-7b-instruct" \   # must match the model name shown in LM Studio
  --games 3 \
  --output results/erpc-qwen.json
```

The `--model` value passes through verbatim to the API, so use whatever identifier LM Studio displays for the loaded model. It also appears as `cell.model` in the output JSON, keeping runs labelled correctly.

---

## Recommended open-source models

Tested and worth trying, roughly in order of tool-use quality:

### Strong tool use

| Model | Size | Notes |
|-------|------|-------|
| **Qwen2.5-7B-Instruct** | ~4 GB (Q4) | Best small model for structured tool-use loops; explicitly trained on function calling. Good first choice. |
| **Qwen2.5-14B-Instruct** | ~8 GB (Q4) | Noticeably stronger than 7B on multi-turn tool sequences; worth the extra VRAM. |
| **Qwen2.5-32B-Instruct** | ~18 GB (Q4) | Approaches frontier quality on tool use; requires a GPU with 20+ GB VRAM or CPU offload. |

### Good instruction following, moderate tool use

| Model | Size | Notes |
|-------|------|-------|
| **Gemma 3 12B** | ~7 GB (Q4) | Google's March 2025 release; strong reasoning, decent function calling. |
| **Gemma 3 4B** | ~2.5 GB (Q4) | Very fast; tool use is weaker but useful for quick sanity checks. |
| **Llama 3.1 8B Instruct** | ~5 GB (Q4) | Meta's solid community baseline; more consistent than earlier Llama generations. |
| **Phi-4 14B** | ~8 GB (Q4) | Microsoft; punches above its weight on structured tasks; good for E_RPC runs. |

### Worth exploring

| Model | Size | Notes |
|-------|------|-------|
| **Mistral 7B Instruct v0.3** | ~4 GB (Q4) | Fast; weaker on multi-step tool-use but interesting as a low-RPVA baseline. |
| **DeepSeek-R1 Distill (Qwen 7B)** | ~4 GB (Q4) | Reasoning model; verbose thinking traces inflate token counts but may reduce RPVA. |

### What to expect vs Claude

Local models will generally show higher RPVA and abandon rates on E0/E1 (HTTP discovery) compared to Claude, since discovery requires chaining affordances across multiple requests without explicit guidance. E_RPC is the fairest comparison — it removes HTTP navigation entirely and tests raw game-play capability. Start there to establish a baseline before running HTTP modes.

---

## Inspecting results

The output JSON is self-contained. Quick checks with Python:

```bash
# RPVA and game count
python -c "
import json, sys
d = json.load(open('results/erpc-qwen.json'))
print('RPVA:', d['aggregate']['rpva'])
print('games:', len(d['games']))
"

# Verify E_RPC has no HTTP entries
python -c "
import json
d = json.load(open('results/erpc-qwen.json'))
t = d['games'][0]['transcript']
print('http entries:', sum(1 for e in t if 'status' in e))   # should be 0
print('tool entries:', sum(1 for e in t if 'tool_name' in e))
"

# Outcome breakdown across all games
python -c "
import json
from collections import Counter
d = json.load(open('results/erpc-qwen.json'))
counts = Counter(e['outcome'] for g in d['games'] for e in g['transcript'])
for k, v in counts.most_common(): print(f'{k}: {v}')
"
```
