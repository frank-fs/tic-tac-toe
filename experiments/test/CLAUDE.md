# Experiment Tests

Comparison-arm tests (not the primary Frank sample app). Off-solution — build/run by project path.

## Web.Simple integration (Playwright)

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run --project experiments/src/TicTacToe.Web.Simple/ --urls http://localhost:5328 &>/tmp/tictactoe-web-simple.log &
TEST_BASE_URL=http://localhost:5328 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test experiments/test/TicTacToe.Web.Simple.Tests/
```

## Orchestrator

```bash
dotnet test experiments/test/TicTacToe.Orchestrator.Tests/
```
