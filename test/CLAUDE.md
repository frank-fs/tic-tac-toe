# Integration Tests

## Server Start (required first)

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run --project src/TicTacToe.Web/ --urls http://localhost:5228 &>/tmp/tictactoe-web.log &
```

## Playwright Tests

```bash
TEST_BASE_URL=http://localhost:5228 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test TicTacToe.Web.Tests/
```

`dotnet test TicTacToe.sln` defaults to `localhost:5000` — always set `TEST_BASE_URL`.
