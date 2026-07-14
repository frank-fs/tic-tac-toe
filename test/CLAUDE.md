# Integration Tests

## Playwright Tests

Self-hosting: the suite boots its own `TicTacToe.Web` on a free port (`SharedServer` fixture) and
tears it down after the run. No server to start by hand.

```bash
dotnet test test/TicTacToe.Web.Tests/
```

Set `TEST_BASE_URL` to drive an already-running server instead:

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run --project src/TicTacToe.Web/ --urls http://localhost:5228 &>/tmp/tictactoe-web.log &
TEST_BASE_URL=http://localhost:5228 dotnet test test/TicTacToe.Web.Tests/
```

Browsers must be installed once: `pwsh test/TicTacToe.Web.Tests/bin/Debug/net10.0/playwright.ps1 install chromium`.

## Surface cells

The app is the factorial surface: `TICTACTOE_CELL=<ACSdSo>` (e.g. `1010`) takes factors away.
Unset = the full surface (`1111`) — the real app, which is what these tests exercise.
