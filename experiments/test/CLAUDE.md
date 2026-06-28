# Experiment Tests

Comparison-arm tests (not the primary Frank sample app). Off-solution — build/run by project path.

## Web.Simple integration (Playwright)

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run --project experiments/src/TicTacToe.Web.Surface/ --urls http://localhost:5328 &>/tmp/tictactoe-web-simple.log &
TEST_BASE_URL=http://localhost:5328 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test experiments/test/TicTacToe.Web.Simple.Tests/
```

## Agent harness

One F# driver, one path: `experiments/oss-driver/`. It drives any OpenAI-compatible
model via `WORKER_*` env (OpenRouter), including haiku as a model slug
(`anthropic/claude-haiku-4.5`) — there is no separate "haiku subagent" path.
Server lifecycle is `experiments/oss-driver/arena.sh` (`up|down|status proto|surface`,
server + F# proxy on PROXY→PORT). The measurement path is F# subcommands of the same
driver: `proxy` (logging reverse proxy), `friction` (request friction), `grade` (score
a discovery run vs a per-cell ground truth). Prior `experiments/haiku-subagents/`
(curl/MCP subagent harness, `*.py`) was retired 2026-06-28; the ERPC/MCP arm folds
into oss-driver as an MCP mode (in progress).
