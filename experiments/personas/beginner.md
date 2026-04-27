# Persona: Beginner

**ID**: `beginner`
**Version**: 1.0.0
**Experiment Stage**: H1 definition; active in F0–F4 measurement runs

## System Prompt

### E0 (no HATEOAS layer)

None. The agent receives no system prompt. Only the bare entry-point URL is provided at runtime via the `BASE_URL` injection point.

### E1 (HATEOAS / hypermedia layer)

```
You are an HTTP client. Here is a URL: {{BASE_URL}}
```

No additional context, strategy, or game knowledge is provided.

## Goals

- Produce valid actions (any legal moves) until the game reaches a terminal state (win or draw).
- Winning is not a goal; the only requirement is that each request sent constitutes a legal, accepted move.
- The agent must discover what requests to make entirely from server responses — no out-of-band game knowledge is assumed.

## Constraints

- `temperature`: `0` — fully deterministic; no sampling variation between runs.
- `top_p`: `1.0`, `top_k`: disabled — consistent with temperature=0 reproducibility.
- The agent must NOT use any embedded knowledge of tic-tac-toe rules to decide moves. Moves are selected solely from affordances present in the server response (links, forms, hypermedia controls).
- The agent must NOT retry a rejected request with the same payload. Each 4xx response counts as one invalid request in the metric.
- Maximum turns per game: `50` (safety limit; a normal game terminates in ≤9 moves).
- The agent must follow redirects (3xx) transparently.
- No cookies, sessions, or authentication tokens are pre-injected unless the server requires them and advertises them as affordances.

## HTTP Configuration

| Parameter | Value |
|-----------|-------|
| `BASE_URL` | `http://localhost:{{PORT}}/` (injected at run time) |
| `Content-Type` (request) | `application/x-www-form-urlencoded` or as advertised by the server affordance |
| `Accept` | `text/html, application/json;q=0.9, */*;q=0.8` |
| Auth | None (anonymous) |
| Redirect policy | Follow automatically |
| Timeout per request | `10s` |

## Success Metrics

| Metric | Description | Target |
|--------|-------------|--------|
| **RPVA** (Requests Per Valid Action) | Total HTTP requests ÷ total valid (2xx) game-move responses | Primary metric; lower is better |
| **Invalid-request rate** | Count of 4xx responses ÷ total requests | Lower is better; load-bearing for Q1 |
| **Game completion rate** | Fraction of games that reach a terminal state without hitting the turn limit | Should be 1.0 under any working server variant |
| **Discovery success** | Agent reached first valid move without manual URL injection beyond `BASE_URL` | Boolean; must be `true` |

A run is considered valid for analysis only if `Game completion rate = 1.0` and `Discovery success = true`.
