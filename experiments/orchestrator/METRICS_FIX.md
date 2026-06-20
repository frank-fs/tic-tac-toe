# Metrics capture fix (fix/metrics-capture)

## Problem (from smoke @ fee8f86)
`metrics.json` had empty `per_agent` + `role_assignments` for all 6 cells. Root causes:
1. `Orchestrator.fs` hardcoded `let sessionMap = Map.empty` → roles + per-agent collapse.
2. Event source missing per arm:
   - Simple: writes `server-requests.jsonl` (request + event records), but `move_accepted`
     events carry `role`, not `session_id` — `ServerLogTail` parsed `session_id` → dropped them.
   - Proto (`src/TicTacToe.Web`): no request/event logging at all.
   - ERPC (`experiments/mcp-rpc`, stdio): no HTTP server; stdout reserved for JSON-RPC.

## Decision
File-based event logging, **all three** servers, **one consistent schema**. No Serilog
(reuse the existing JSON-line pattern; no new prod dependency). Metrics resolved **per role**
from the log (agent↔session linkage is unavailable for browser arms and intentionally dropped).

### Event schema (one JSON object per line)
```
{"event_type":"player_assigned","game_id":G,"role":R,"timestamp":T}
{"event_type":"move_accepted","game_id":G,"role":R,"move":M,"timestamp":T}
{"event_type":"move_rejected","game_id":G,"role":R,"reason":Reason,"timestamp":T}
{"event_type":"game_over","game_id":G,"outcome":O,"move_count":N,"timestamp":T}
```
`role ∈ {X, O, Observer, unassigned}`. All three servers write to the path in env
`TICTACTOE_REQUEST_LOG_PATH` = `experiments/results/<cell>/server-requests.jsonl`.

## Per-role metrics
accepted, rejected, out_of_turn (reason=OutOfTurn), invalid_rate=rejected/(accepted+rejected),
rpva=total/accepted. Tokens stay per-agent in transcripts; cell-level total added.
