# Proto Phase B: per-resource SSE streams — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Proto a per-game SSE stream (`/games/{id}/sse`) alongside the kept dashboard stream (`/sse`), with `SseBroadcast` routing each game's events only to the dashboard and that game's subscribers, and the dashboard connect path morphing boards by id instead of clearing+re-appending.

**Architecture:** Separate stream endpoints (Frank.Datastar has no native conneg). `SseBroadcast` subscribers carry an optional game filter (`None` = dashboard/all, `Some gameId` = one game). A game's move broadcast targets dashboard ∪ that-game subscribers; a new-game append targets dashboard subscribers only. The per-game page opens its own stream via a parametrized layout `data-init`.

**Tech Stack:** F# / .NET 10, Frank, Frank.Datastar (SSE), Oxpecker.ViewEngine, NUnit + Playwright + raw HttpClient tests.

**Spec:** `docs/superpowers/specs/2026-06-18-proto-phase-b-per-resource-streams-design.md`

**Test policy note:** Per project guidance (lean against reflexive tests; E2E SSE is flaky), TDD centers on the **deterministic `SseBroadcast` unit test** (Task 1). E2E is targeted: one raw-HttpClient connect-resync test and one Playwright append/morph test. Phase A no-JS tests are the AC5 regression — run, don't rewrite.

---

## File Structure

- **Modify** `src/TicTacToe.Web/SseBroadcast.fs` — subscriber gains a game filter; add `broadcastForGame`, `broadcastPerRoleForGame`, `broadcastToDashboard`; rename `broadcastPerRole` → `broadcastPerRoleForGame`.
- **Modify** `src/TicTacToe.Web/Handlers.fs` — `subscribe userId None` in dashboard `sse`; drop connect clear + morph-by-id; `broadcastPerRoleForGame`; new-game append → `broadcastToDashboard`; new `gameSse` handler.
- **Modify** `src/TicTacToe.Web/Program.fs` — register `resource "/games/{id}/sse"`.
- **Modify** `src/TicTacToe.Web/templates/shared/layout.fs` — add `htmlWithStream` taking the stream URL; `html` delegates with `/sse`.
- **Test** `test/TicTacToe.Web.Tests/SseBroadcastTests.fs` (create) — per-game filtering unit test.
- **Test** `test/TicTacToe.Web.Tests/PerGameStreamTests.fs` (create) — connect-resync (HttpClient) + append/morph (Playwright).
- **Modify** `test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj` — add the two test files (before `Main.fs`).

---

## Task 1: SseBroadcast per-game filtering

**Files:**
- Modify: `src/TicTacToe.Web/SseBroadcast.fs`
- Create: `test/TicTacToe.Web.Tests/SseBroadcastTests.fs`
- Modify: `test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj`

- [ ] **Step 1: Add the test file to the fsproj**

In `test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj`, add this line to the `<ItemGroup>` of `<Compile>` entries, immediately before the `Main.fs` entry:

```xml
    <Compile Include="SseBroadcastTests.fs" />
```

- [ ] **Step 2: Write the failing test**

Create `test/TicTacToe.Web.Tests/SseBroadcastTests.fs`. This is a pure unit test — no server, no browser. It drives the module's in-memory subscriber registry directly.

```fsharp
namespace TicTacToe.Web.Tests

open System.IO
open System.Threading.Tasks
open NUnit.Framework
open TicTacToe.Web.SseBroadcast

/// Pure unit tests for per-game SSE routing. No server/browser: subscribe channels
/// directly, broadcast, then assert which channels received an event.
[<TestFixture>]
type SseBroadcastTests() =

    // A render that ignores the writer; we only care about delivery, not payload.
    let noopRender : TextWriter -> Task = fun _ -> Task.CompletedTask

    /// True if the channel has at least one queued event (non-blocking).
    let received (ch: System.Threading.Channels.Channel<SseEvent>) =
        ch.Reader.Count > 0

    [<Test>]
    member _.``per-game broadcast reaches dashboard and that game, not other games``() =
        let dash, dashSub = subscribe "u-dash" None
        let g1, g1Sub = subscribe "u-g1" (Some "game-1")
        let g2, g2Sub = subscribe "u-g2" (Some "game-2")
        try
            broadcastPerRoleForGame "game-1" (fun _ -> PatchElements noopRender)
            Assert.That(received dash, Is.True, "dashboard (None filter) must receive every game's events")
            Assert.That(received g1, Is.True, "the game-1 subscriber must receive game-1 events")
            Assert.That(received g2, Is.False, "the game-2 subscriber must NOT receive game-1 events")
        finally
            dashSub.Dispose(); g1Sub.Dispose(); g2Sub.Dispose()

    [<Test>]
    member _.``dashboard broadcast reaches only dashboard subscribers``() =
        let dash, dashSub = subscribe "u-dash" None
        let g1, g1Sub = subscribe "u-g1" (Some "game-1")
        try
            broadcastToDashboard (PatchElementsAppend("#games-container", noopRender))
            Assert.That(received dash, Is.True, "dashboard must receive a new-game append")
            Assert.That(received g1, Is.False, "a per-game subscriber must NOT receive another game's append")
        finally
            dashSub.Dispose(); g1Sub.Dispose()
```

- [ ] **Step 3: Run the test to verify it fails to compile**

Run: `dotnet build test/TicTacToe.Web.Tests/`
Expected: FAIL — `subscribe` takes one arg (not two), `broadcastPerRoleForGame` / `broadcastToDashboard` undefined.

- [ ] **Step 4: Rewrite SseBroadcast.fs with the filter**

Replace the bodies of `subscribe`, `broadcast`, `sendToUser`, and `broadcastPerRole` in `src/TicTacToe.Web/SseBroadcast.fs`. The subscriber tuple becomes `(userId, gameFilter, channel)`. Full new content of the section from the `subscribers` value through `broadcastPerRole`:

```fsharp
/// Thread-safe collection of subscriber channels: (userId, gameFilter, channel).
/// gameFilter = None  -> dashboard subscriber (receives every game's events).
/// gameFilter = Some gameId -> per-game subscriber (receives only that game's events).
let private subscribers = ConcurrentDictionary<Guid, string * string option * Channel<SseEvent>>()

/// Create a new subscriber channel for an SSE connection.
/// gameFilter None = dashboard (all games); Some gameId = only that game.
/// Returns (Channel, IDisposable); disposing unsubscribes and completes the channel.
let subscribe (userId: string) (gameFilter: string option) : Channel<SseEvent> * IDisposable =
    let channel = Channel.CreateUnbounded<SseEvent>()
    let id = Guid.NewGuid()
    subscribers.TryAdd(id, (userId, gameFilter, channel)) |> ignore

    let disposable =
        { new IDisposable with
            member __.Dispose() =
                match subscribers.TryRemove(id) with
                | true, (_, _, ch) -> ch.Writer.Complete()
                | false, _ -> () }

    (channel, disposable)

/// A dashboard (None) subscriber receives every game; a Some-filtered subscriber
/// receives only its own game.
let private receivesGame (gameFilter: string option) (gameId: string) =
    match gameFilter with
    | None -> true
    | Some gid -> gid = gameId

/// Broadcast an event to ALL active SSE connections (used for global signals/removals).
let broadcast (event: SseEvent) =
    for KeyValue(_, (_, _, ch)) in subscribers do
        ch.Writer.TryWrite(event) |> ignore

/// Send an event to a specific user's SSE connections.
let sendToUser (userId: string) (event: SseEvent) =
    for KeyValue(_, (uid, _, ch)) in subscribers do
        if uid = userId then
            ch.Writer.TryWrite(event) |> ignore

/// Broadcast a per-role event for a specific game: reaches the dashboard subscribers
/// and that game's per-game subscribers, each rendered for their own userId.
let broadcastPerRoleForGame (gameId: string) (renderForRole: string -> SseEvent) =
    for KeyValue(_, (userId, gameFilter, ch)) in subscribers do
        if receivesGame gameFilter gameId then
            ch.Writer.TryWrite(renderForRole userId) |> ignore

/// Broadcast an event to dashboard (None-filter) subscribers only. Used for the
/// new-game append: a per-game subscriber must not receive another game's board.
let broadcastToDashboard (event: SseEvent) =
    for KeyValue(_, (_, gameFilter, ch)) in subscribers do
        if Option.isNone gameFilter then
            ch.Writer.TryWrite(event) |> ignore
```

- [ ] **Step 5: Run the unit test to verify it passes**

Run: `dotnet test test/TicTacToe.Web.Tests/ --filter "TestFixture=SseBroadcastTests"`
Expected: 2 tests PASS. (The rest of the project will not yet compile until Task 1 Step 6 updates callers — run the build in Step 6 first if the filter cannot resolve.)

- [ ] **Step 6: Update the two callers so the project compiles**

In `src/TicTacToe.Web/Handlers.fs`:
- In `subscribeToGame` (≈ line 71), change `broadcastPerRole renderForRole` to:
```fsharp
                        broadcastPerRoleForGame gameId renderForRole
```
- In the dashboard `sse` handler (≈ line 186), change `let (myChannel, subscription) = subscribe userId` to:
```fsharp
        let (myChannel, subscription) = subscribe userId None
```

Run: `dotnet build src/TicTacToe.Web/`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/TicTacToe.Web/SseBroadcast.fs src/TicTacToe.Web/Handlers.fs test/TicTacToe.Web.Tests/SseBroadcastTests.fs test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj
git commit -m "feat(web): per-game SSE broadcast filtering (#62)"
```

---

## Task 2: Dashboard connect = morph-by-id; new-game append → dashboard only

**Files:**
- Modify: `src/TicTacToe.Web/Handlers.fs` (dashboard `sse` ≈ 183-215; `createGame` ≈ 254; `resetGame` ≈ 475)

- [ ] **Step 1: Drop the connect clear and morph each board by id**

In the dashboard `sse` handler, **delete** the clear line (≈ 192):

```fsharp
            // Clear loading state when client connects
            do! Datastar.streamPatchElements (fun tw -> tw.WriteAsync("""<div id="games-container" class="games-container"></div>""")) ctx
```

Then change the connect snapshot loop (≈ 199-203) from Append to a morph-by-id patch (the board carries `id="game-{gameId}"`, so the default patch morphs it in place):

```fsharp
            let snapshot = supervisor.SnapshotActiveGames()
            let gameCount = List.length snapshot
            for (gameId, state) in snapshot do
                let assignment = assignmentManager.GetAssignment(gameId)
                let element = renderGameBoard gameId state userId assignment gameCount
                do! Datastar.streamPatchElements (fun tw -> Render.toTextWriterAsync tw element) ctx
```

- [ ] **Step 2: Scope the new-game append to dashboard subscribers**

In `createGame` (≈ line 254), change `broadcast (PatchElementsAppend(...))` to `broadcastToDashboard`:

```fsharp
                            broadcastToDashboard (PatchElementsAppend("#games-container", fun tw -> Render.toTextWriterAsync tw element))
```

In `resetGame` (≈ line 475), make the identical change (the reset path recreates a game and re-appends it):

```fsharp
                                    broadcastToDashboard (PatchElementsAppend("#games-container", fun tw -> Render.toTextWriterAsync tw element))
```

(Leave the `broadcast (RemoveElement ...)` calls in `deleteGame`/`resetGame` and the `broadcast (PatchSignals ...)` rejection in `makeMove` as `broadcast` — removing a non-present element is a client-side no-op and the rejection signal is out of scope for this issue.)

- [ ] **Step 3: Build**

Run: `dotnet build src/TicTacToe.Web/`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/TicTacToe.Web/Handlers.fs
git commit -m "feat(web): dashboard SSE morphs boards by id on connect; new-game append targets dashboard (#62)"
```

---

## Task 3: Per-game stream endpoint `/games/{id}/sse`

**Files:**
- Modify: `src/TicTacToe.Web/Handlers.fs` (add `gameSse` after the dashboard `sse`, ≈ after line 215)
- Modify: `src/TicTacToe.Web/Program.fs` (register the resource near `sse`, ≈ line 99-103)

- [ ] **Step 1: Add the `gameSse` handler**

In `src/TicTacToe.Web/Handlers.fs`, immediately after the dashboard `sse` handler (after its closing `}` ≈ line 215), add:

```fsharp
/// Per-game SSE endpoint — streams ONLY this game's events. On connect it sends a
/// morph-by-id snapshot of the current board (connect-resync, no container clear), then
/// forwards that game's broadcasts. Mirrors the dashboard `sse` but filtered to one game.
let gameSse (ctx: HttpContext) =
    task {
        let gameId = ctx.Request.RouteValues.["id"] |> string
        let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
        let (myChannel, subscription) = subscribe userId (Some gameId)
        let supervisor = ctx.RequestServices.GetRequiredService<GameSupervisor>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()

        try
            // Connect-resync: send the current board so a connect-after-move shows current state.
            match supervisor.TryGetState(gameId) with
            | Some result ->
                let assignment = assignmentManager.GetAssignment(gameId)
                let gameCount = supervisor.GetActiveGameCount()
                let element = renderGameBoard gameId result userId assignment gameCount
                do! Datastar.streamPatchElements (fun tw -> Render.toTextWriterAsync tw element) ctx
            | None -> ()

            while not ctx.RequestAborted.IsCancellationRequested do
                let! event = myChannel.Reader.ReadAsync(ctx.RequestAborted).AsTask()
                do! writeSseEvent ctx event
        with
        | :? OperationCanceledException -> ()
        | :? ChannelClosedException -> ()
        | _ -> ()

        subscription.Dispose()
    }
```

- [ ] **Step 2: Register the route**

In `src/TicTacToe.Web/Program.fs`, after the `sse` resource (≈ line 103), add:

```fsharp
let gameSse =
    resource "/games/{id}/sse" {
        name "GameSse"
        datastar Handlers.gameSse
    }
```

Then register it in the `webHost { ... }` resource list (Program.fs ≈ 183-191), adding the line after `resource gameDelete`:

```fsharp
        resource gameById
        resource gameReset
        resource gameDelete
        resource gameSse
```

(ASP.NET routing matches by template specificity, so `/games/{id}/sse` resolves over `/games/{id}` regardless of list order; grouping it with the other game sub-resources is just for readability.)

- [ ] **Step 3: Build**

Run: `dotnet build src/TicTacToe.Web/`
Expected: 0 errors.

- [ ] **Step 4: Smoke the route manually**

Run the server (`dotnet run --project src/TicTacToe.Web/` — port 5228), then in another shell:
```bash
curl -sN -H "datastar-request: true" http://localhost:5228/games/$(curl -s -X POST http://localhost:5228/games -c /tmp/c -b /tmp/c -o /dev/null -w '%{redirect_url}' ; echo)/sse --max-time 2 | head -5
```
(Or simpler: create a game in the browser, note its id, then `curl -sN -H "datastar-request: true" http://localhost:5228/games/<id>/sse --max-time 2`.)
Expected: an SSE `event:` frame containing `game-<id>` board markup, then the stream stays open.

- [ ] **Step 5: Commit**

```bash
git add src/TicTacToe.Web/Handlers.fs src/TicTacToe.Web/Program.fs
git commit -m "feat(web): per-game SSE stream /games/{id}/sse with connect-resync (#62)"
```

---

## Task 4: Per-game page opens its own stream

**Files:**
- Modify: `src/TicTacToe.Web/templates/shared/layout.fs` (`html` ≈ line 37-52)
- Modify: `src/TicTacToe.Web/Handlers.fs` (`getGame` ≈ line 288)

- [ ] **Step 1: Parametrize the stream URL in layout**

In `src/TicTacToe.Web/templates/shared/layout.fs`, replace the `html` function (the one with `body().attr("data-init", "@get('/sse')")`) with a parametrized version plus a default-delegating `html`:

```fsharp
    let htmlWithStream (ctx: HttpContext) (streamUrl: string) (content: HtmlElement) =
        html (lang = "en") {
            head () {
                title () {
                    match ctx.Items.TryGetValue "Title" with
                    | true, title -> string title
                    | false, _ -> "Tic Tac Toe"
                }

                meta (charset = "utf-8")
                meta (name = "viewport", content = "width=device-width, initial-scale=1.0")
                base' (href = "/")
                link (rel = "icon", type' = "image/png", href = "/favicon.png")

                script (
                    type' = "module",
                    src = "https://cdn.jsdelivr.net/gh/starfederation/datastar@v1.0.0-RC.7/bundles/datastar.js",
                    crossorigin = "anonymous"
                )
            }

            body().attr("data-init", sprintf "@get('%s')" streamUrl) { mainLayout ctx content }
        }

    /// Default page: subscribes to the global dashboard stream.
    let html (ctx: HttpContext) (content: HtmlElement) =
        htmlWithStream ctx "/sse" content
```

(This preserves every existing `layout.html` caller — `home`, the `getGame` 404 branch — which keep the `/sse` dashboard stream.)

- [ ] **Step 2: Point the game page at its own stream**

In `src/TicTacToe.Web/Handlers.fs` `getGame`, change the success-branch render (≈ line 288) from `layout.html ctx` to `layout.htmlWithStream` with the per-game URL:

```fsharp
            let body = renderGameBoard gameId result userId assignment gameCount |> withBanner ctx
            let element = layout.htmlWithStream ctx (sprintf "/games/%s/sse" gameId) body
```

(Leave the 404 branch at ≈ line 296 using `layout.html` — the dashboard stream is correct for a not-found page.)

- [ ] **Step 3: Build**

Run: `dotnet build src/TicTacToe.Web/`
Expected: 0 errors.

- [ ] **Step 4: Verify the game page wires its own stream**

Run: `dotnet build src/TicTacToe.Web/ && grep -n "games/%s/sse\|htmlWithStream" src/TicTacToe.Web/Handlers.fs src/TicTacToe.Web/templates/shared/layout.fs`
Expected: `getGame` uses `htmlWithStream` with `/games/%s/sse`; layout defines `htmlWithStream` + `html`.

- [ ] **Step 5: Commit**

```bash
git add src/TicTacToe.Web/templates/shared/layout.fs src/TicTacToe.Web/Handlers.fs
git commit -m "feat(web): game page opens its own /games/{id}/sse stream via parametrized layout (#62)"
```

---

## Task 5: E2E — connect-resync (HttpClient) + append/morph (Playwright)

**Files:**
- Create: `test/TicTacToe.Web.Tests/PerGameStreamTests.fs`
- Modify: `test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj`

These need the server running on the test base URL. See `test/CLAUDE.md`: start the server, set `TEST_BASE_URL` (server is on :5228).

- [ ] **Step 1: Add the test file to the fsproj**

In `test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj`, add immediately before `Main.fs`:

```xml
    <Compile Include="PerGameStreamTests.fs" />
```

- [ ] **Step 2: Write the connect-resync test (raw HttpClient)**

Create `test/TicTacToe.Web.Tests/PerGameStreamTests.fs`:

```fsharp
namespace TicTacToe.Web.Tests

open System
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open NUnit.Framework

/// E2E for #62 per-game streams. Connect-resync uses raw HttpClient (read the first SSE
/// frame); append/morph uses Playwright (the JS path).
[<TestFixture>]
type PerGameStreamTests() =
    let mutable client: HttpClient = null
    let mutable handler: HttpClientHandler = null

    let baseUrl =
        Environment.GetEnvironmentVariable("TEST_BASE_URL")
        |> Option.ofObj
        |> Option.filter (fun s -> not (String.IsNullOrEmpty(s)))
        |> Option.defaultValue "http://localhost:5000"

    [<OneTimeSetUp>]
    member _.Setup() : Task =
        task {
            handler <- new HttpClientHandler(CookieContainer = Net.CookieContainer(), AllowAutoRedirect = false)
            client <- new HttpClient(handler, BaseAddress = Uri(baseUrl))
            let! _ = client.GetAsync("/login")
            ()
        }

    [<OneTimeTearDown>]
    member _.Teardown() =
        if not (isNull client) then client.Dispose(); client <- null
        if not (isNull handler) then handler.Dispose(); handler <- null

    member private _.CreateGame() : Task<string> =
        task {
            let! resp = client.PostAsync("/games", null)
            let loc = resp.Headers.Location.ToString()
            return loc.Substring("/games/".Length)
        }

    /// AC3: opening /games/{id}/sse after a move yields that board's current state first.
    [<Test>]
    member this.``per-game stream first payload contains the current board``() : Task =
        task {
            let! gameId = this.CreateGame()
            // Make a move so the board is non-empty (so resync content is observable).
            use form = new FormUrlEncodedContent([ Collections.Generic.KeyValuePair("player","X"); Collections.Generic.KeyValuePair("position","TopLeft") ])
            let! _ = client.PostAsync($"/games/{gameId}", form)

            use req = new HttpRequestMessage(HttpMethod.Get, $"/games/{gameId}/sse")
            req.Headers.TryAddWithoutValidation("datastar-request", "true") |> ignore
            use cts = new CancellationTokenSource(TimeSpan.FromSeconds 5.0)
            use! resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            use! stream = resp.Content.ReadAsStreamAsync(cts.Token)
            use reader = new StreamReader(stream)
            // Read the first ~2KB of the stream (the connect-resync frame).
            let buf = Array.zeroCreate<char> 2048
            let! n = reader.ReadAsync(buf.AsMemory(0, buf.Length)).AsTask()
            let payload = String(buf, 0, n)
            Assert.That(payload, Does.Contain $"game-{gameId}", "first SSE frame must carry this game's board")
        }
```

- [ ] **Step 3: Run the connect-resync test (server must be running)**

Start the server, set `TEST_BASE_URL=http://localhost:5228`, then:
Run: `dotnet test test/TicTacToe.Web.Tests/ --filter "Name~per-game stream first payload"`
Expected: PASS.

- [ ] **Step 4: Add the Playwright append/morph test (AC2 + AC4)**

Append this member to `PerGameStreamTests` (it uses Playwright via a fresh page; mirror the existing Playwright tests' setup — open the dashboard, create a game from the HttpClient, assert the board appears live and there is exactly one of it):

```fsharp
    /// AC2 + AC4: a game created while a dashboard stream is open is appended live, and a
    /// move morphs the board in place (exactly one board element, no duplicate).
    [<Test>]
    member this.``dashboard appends a new game live and keeps one board element``() : Task =
        task {
            let! playwright = Microsoft.Playwright.Playwright.CreateAsync()
            let! browser = playwright.Chromium.LaunchAsync()
            let! page = browser.NewPageAsync()
            let! _ = page.GotoAsync($"{baseUrl}/login")
            let! _ = page.GotoAsync($"{baseUrl}/")
            // Create a game via the API on the shared cookie client; the open dashboard must gain it.
            let! gameId = this.CreateGame()
            do! TestHelpers.waitForCount page $"#game-{gameId}" 1 5000
            // A move morphs in place — still exactly one board element for this game.
            use form = new FormUrlEncodedContent([ Collections.Generic.KeyValuePair("player","X"); Collections.Generic.KeyValuePair("position","TopLeft") ])
            let! _ = client.PostAsync($"/games/{gameId}", form)
            do! TestHelpers.waitForCount page $"#game-{gameId}" 1 5000
            do! browser.CloseAsync()
        }
```

- [ ] **Step 5: Run the Playwright test**

Run: `dotnet test test/TicTacToe.Web.Tests/ --filter "Name~dashboard appends a new game live"`
Expected: PASS. (If flaky on timing, the `waitForCount` 5s window is the knob; do not add sleeps.)

- [ ] **Step 6: AC5 regression — Phase A no-JS still green**

Run: `dotnet test test/TicTacToe.Web.Tests/ --filter "TestFixture=ProtoPeServerRenderTests"`
Expected: all PASS (no-JS discovery/move/refresh unbroken by the stream changes).

- [ ] **Step 7: Commit**

```bash
git add test/TicTacToe.Web.Tests/PerGameStreamTests.fs test/TicTacToe.Web.Tests/TicTacToe.Web.Tests.fsproj
git commit -m "test(web): per-game stream connect-resync + dashboard live-append E2E (#62)"
```

---

## Task 5 — as implemented (revised 2026-06-19)

Task 5 was redesigned during execution (see the spec's "Implementation note"). Instead of a
single external-server append test (flaky; and the append path never runs under the experiment
cap), the suite uses a **configurable-server harness**:

- **Create** `test/TicTacToe.Web.Tests/ConfiguredServer.fs` — launches the built `TicTacToe.Web`
  dll on a free port with `TICTACTOE_INITIAL_GAMES` / `TICTACTOE_MAX_GAMES`, polls readiness,
  kills on dispose.
- **`PerGameStreamTests.fs`** holds `ConfiguredServerFixture` (base: server + Playwright +
  authed HttpClient) and two fixtures:
  - `ExperimentConfigStreamTests(1, 1)`: move-morphs-live, second-create-rejected (409),
    per-game connect-resync.
  - `MultiGameConfigStreamTests(6, 50)`: new-game appended live as a single board.
- fsproj `<Compile>` order: `SseBroadcastTests.fs`, `ConfiguredServer.fs`, `PerGameStreamTests.fs`.

Verified: new fixtures green 3/3 in isolation; full `dotnet test test/TicTacToe.Web.Tests/`
118/118. Commits: `5079b2b` (harness + tests), `b12b320` (FS0760 fix).

## Final verification

- [ ] `dotnet build` (whole game solution) — 0 errors.
- [ ] `dotnet test test/TicTacToe.Engine.Tests/` — green (untouched, sanity).
- [ ] Server running + `TEST_BASE_URL=http://localhost:5228`, then `dotnet test test/TicTacToe.Web.Tests/` — green.
- [ ] Manual: open `/` in a browser, open `/games/{id}` in a second tab, make a move in one — both update live; refresh with JS disabled still shows current state.
- [ ] `/verification-before-completion` (large change).
