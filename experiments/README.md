# Agent Persona Experiments

Infrastructure for testing the tic-tac-toe API using AI agent personas with distinct behavioral profiles.

## Overview

This framework defines three agent personas that interact with the tic-tac-toe HTTP API in different ways:

- **Beginner** — No domain knowledge. Discovers the API purely through HTTP affordances (OPTIONS, Link headers, HTML content).
- **Expert** — Full tic-tac-toe knowledge and optimal strategy. Plays to win or draw, never loses.
- **Chaos** — Adversarial tester. Probes error handling with invalid moves, malformed requests, identity switching, and concurrency.

## Directory Structure

```
experiments/
├── personas/
│   ├── beginner.md      # Beginner persona definition and system prompt
│   ├── expert.md        # Expert persona definition and strategy
│   └── chaos.md         # Chaos/adversarial persona and probe categories
├── orchestrator/
│   ├── run.fsx          # F# script orchestrator (HttpClient-based)
│   └── config.json      # Default configuration
├── results/             # JSON transcripts from experiment runs
│   └── .gitkeep
└── README.md            # This file
```

## Configuration

Edit `orchestrator/config.json`:

```json
{
    "serverUrl": "http://localhost:5228",
    "defaultGameCount": 3,
    "timeoutSeconds": 60,
    "maxRequestsPerGame": 50
}
```

| Field | Description |
|---|---|
| `serverUrl` | Base URL of the running tic-tac-toe server |
| `defaultGameCount` | Number of games to play per run |
| `timeoutSeconds` | HTTP request timeout |
| `maxRequestsPerGame` | Safety limit to prevent runaway loops |

## Usage

### Prerequisites

1. Start the tic-tac-toe server:
   ```bash
   dotnet run --project src/TicTacToe.Web/
   ```

2. Verify it's running:
   ```bash
   curl -s -o /dev/null -w "%{http_code}" http://localhost:5228/
   ```

### Running the Orchestrator

```bash
cd experiments/orchestrator

# Run with a persona (auto-generates instance ID)
dotnet fsi run.fsx beginner

# Run with a specific instance ID
dotnet fsi run.fsx expert test-001

# Run with custom config and results directory
dotnet fsi run.fsx chaos chaos-1 ./config.json ../results
```

### Results

Each run produces a JSON transcript in `experiments/results/`:

```
results/
├── beginner-a1b2c3d4-20260328-143000.json
├── expert-test-001-20260328-143500.json
└── chaos-chaos-1-20260328-144000.json
```

Each transcript includes:
- Persona and instance identifiers
- Configuration used
- Per-game request/response records with timing
- Summary metrics

## Persona Details

### Beginner

Starts from zero knowledge. Uses HTTP discovery (OPTIONS, Link headers) to understand the API before attempting interaction. Measures how quickly and accurately the persona can learn the API through affordances alone.

### Expert

Knows tic-tac-toe strategy and REST conventions. Plays optimally (center first, block wins, create forks). Measures win/draw rate and API usage efficiency.

### Chaos

Systematically tests error handling across categories: move validation, request integrity, identity/authorization, and concurrency. Measures server robustness (5xx should be zero).

## Roadmap

This framework is used by subsequent issues:
- **#22**: Wire personas to Claude API for autonomous play
- **#23**: Run full experiment suites and analyze results
