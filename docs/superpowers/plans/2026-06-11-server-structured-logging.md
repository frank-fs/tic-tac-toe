# Server Structured Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add env-var configuration, typed rejection reasons, game-creation lockdown (MaxGames), and JSONL structured request logging to `TicTacToe.Web.Simple`. The orchestrator tails these logs to detect game completion and correlate agent sessions.

**Architecture:** A new `Logger` module writes JSONL to `TICTACTOE_REQUEST_LOG_PATH` (no-op if unset). `GameStore` gains a `MaxGames` limit. `RejectionReason` DU gets new cases. All changes are additive — existing behavior unchanged when env vars are absent.

**Tech Stack:** F# .NET 10, Frank 7.2.0, existing Playwright/NUnit test suite. No new packages.

> **Scope note:** `TicTacToe.Web` (Frank+Datastar) needs the same changes but its statechart architecture is more complex. Mirror this plan there in a follow-on issue.

---

## File Structure

| Action | Path | Responsibility |
|--------|------|---------------|
| Modify | `src/TicTacToe.Web.Simple/Model.fs` | Updated `RejectionReason` DU |
| Modify | `src/TicTacToe.Web.Simple/GameStore.fs` | MaxGames enforcement |
| Create | `src/TicTacToe.Web.Simple/Logger.fs` | JSONL event writer |
| Modify | `src/TicTacToe.Web.Simple/TicTacToe.Web.Simple.fsproj` | Add Logger.fs to compile list |
| Modify | `src/TicTacToe.Web.Simple/Program.fs` | Env-var config, DI for Logger |
| Modify | `src/TicTacToe.Web.Simple/Handlers.fs` | Log events, fix engine-error handling, return typed rejection |
| Modify | `test/TicTacToe.Web.Simple.Tests/GameTests.fs` | New tests for 409, typed rejection, env-var config |

---

### Task 1: Update RejectionReason and fix engine-error handling

**Files:**
- Modify: `src/TicTacToe.Web.Simple/Model.fs`
- Modify: `src/TicTacToe.Web.Simple/Handlers.fs`

**Background:** The existing `RejectionReason` cases (`NotYourTurn | NotAPlayer | WrongPlayer | GameOver`) are too coarse for the structured log. The `makeMove` handler also has a silent bug: when the engine returns `Error(_, "Invalid move")` (position already taken), the handler responds 200 with the error state instead of 422.

- [ ] **Step 1: Update `RejectionReason` in `src/TicTacToe.Web.Simple/Model.fs`**

Replace the existing DU:
```fsharp
type RejectionReason =
    | NotYourTurn   // Correct player, wrong turn
    | NotAPlayer    // User is spectator
    | WrongPlayer   // User is X but O's turn (or vice versa)
    | GameOver      // Game already finished
```

With:
```fsharp
type RejectionReason =
    | OutOfTurn      // Right player, wrong turn order
    | NotAPlayer     // Third user — no slot available
    | PositionTaken  // Square already occupied
    | GameOver       // Game already finished
    | InvalidMove    // Malformed input (position string not recognised)
```

- [ ] **Step 2: Update `PlayerAssignmentManager` match arms in `Model.fs`**

The `TryAssignAndValidate` handler uses `NotYourTurn` and `WrongPlayer`. Replace both with `OutOfTurn`:

In `Model.fs`, find the `TryAssignAndValidate` match and replace every `Rejected NotYourTurn` and `Rejected WrongPlayer` with `Rejected OutOfTurn`. The full updated match arms:

```fsharp
| None, _, true ->
    let updated = { assignment with PlayerXId = Some userId }
    Allowed, updated

| Some xId, None, false when xId <> userId ->
    let updated = { assignment with PlayerOId = Some userId }
    Allowed, updated

| Some xId, _, true when xId = userId -> Allowed, assignment
| _, Some oId, false when oId = userId -> Allowed, assignment

| Some xId, Some _, false when xId = userId -> Rejected OutOfTurn, assignment
| Some _, Some oId, true when oId = userId -> Rejected OutOfTurn, assignment

| Some xId, Some oId, _ when xId <> userId && oId <> userId ->
    Rejected NotAPlayer, assignment

| Some xId, None, false when xId = userId -> Rejected OutOfTurn, assignment
| None, _, false -> Rejected NotAPlayer, assignment
| _ -> Rejected NotAPlayer, assignment
```

- [ ] **Step 3: Fix engine-error handling in `makeMove` in `Handlers.fs`**

Find the block after `store.Update(arenaId, move)` and add a check for `Error` result:

```fsharp
| Allowed ->
    match store.Update(arenaId, move) with
    | None ->
        ctx.Response.StatusCode <- 404
    | Some (Error(_, _)) ->
        // Engine rejected the move (position already taken)
        let reason = Rejected PositionTaken
        if acceptsJson ctx then
            ctx.Response.StatusCode <- 422
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync({| error = "PositionTaken" |})
        else
            do! renderArenaHtml ctx arenaId currentResult (Some "That square is already taken.")
    | Some nextResult ->
        if acceptsJson ctx then
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync(toArenaJson arenaId nextResult)
        else
            do! renderArenaHtml ctx arenaId nextResult None
```

- [ ] **Step 4: Update the `Rejected reason` branch in `makeMove` in `Handlers.fs`**

Replace the `WrongPlayer` case to use the new names:

```fsharp
| Rejected reason ->
    let msg =
        match reason with
        | OutOfTurn -> "It's not your turn."
        | NotAPlayer -> "You are not a player in this arena."
        | PositionTaken -> "That square is already taken."
        | GameOver -> "This game is already over."
        | InvalidMove -> "Invalid move."

    if acceptsJson ctx then
        ctx.Response.StatusCode <- 403
        ctx.Response.ContentType <- "application/json"
        do! ctx.Response.WriteAsJsonAsync({| error = reason.ToString() |})
    else
        do! renderArenaHtml ctx arenaId currentResult (Some msg)
```

- [ ] **Step 5: Build and run existing tests — expect all to still pass**

```bash
dotnet build src/TicTacToe.Web.Simple/
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

Then (server must be running on port 5328):
```bash
TEST_BASE_URL=http://localhost:5328 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test test/TicTacToe.Web.Simple.Tests/
```

Expected: all existing tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/TicTacToe.Web.Simple/Model.fs src/TicTacToe.Web.Simple/Handlers.fs
git commit -m "fix(simple): typed RejectionReason, fix silent engine-error 200 response"
```

---

### Task 2: Add MaxGames to GameStore

**Files:**
- Modify: `src/TicTacToe.Web.Simple/GameStore.fs`

- [ ] **Step 1: Add `maxGames: int option` constructor parameter to `GameStore`**

Replace the class definition:
```fsharp
type GameStore() =
    let agent =
        MailboxProcessor<GameStoreMsg>.Start(fun inbox ->
            let state = Dictionary<string, MoveResult>()
```

With:
```fsharp
type GameStore(?maxGames: int) =
    let agent =
        MailboxProcessor<GameStoreMsg>.Start(fun inbox ->
            let state = Dictionary<string, MoveResult>()
```

- [ ] **Step 2: Update the `Create` handler to enforce the limit**

Replace the `Create reply` match arm:

```fsharp
| Create reply ->
    match maxGames with
    | Some limit when state.Count >= limit ->
        reply.Reply(None)  // at capacity
    | _ ->
        let id = Guid.NewGuid().ToString()
        let result = startGame ()
        state.[id] <- result
        reply.Reply(Some(id, result))
    return! loop ()
```

- [ ] **Step 3: Update `Create` reply channel type in the message union**

In `GameStore.fs`, update the `Create` message and `GameStore.Create()` member:

```fsharp
// Message type — change reply from (string * MoveResult) to option
type GameStoreMsg =
    | Create of AsyncReplyChannel<(string * MoveResult) option>
    | Get    of string * AsyncReplyChannel<MoveResult option>
    | Update of string * Move * AsyncReplyChannel<MoveResult option>
    | Delete of string
    | Reset  of string * AsyncReplyChannel<MoveResult option>
    | List   of AsyncReplyChannel<(string * MoveResult) list>
```

Update the member:
```fsharp
member _.Create() =
    agent.PostAndReply(fun ch -> Create ch)
```

- [ ] **Step 4: Update all callers of `store.Create()` to handle the `option`**

In `Handlers.fs`, update `createArena`:
```fsharp
let createArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        match store.Create() with
        | None ->
            ctx.Response.StatusCode <- 409
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync({| error = "MaxGamesReached" |})
        | Some (arenaId, _) ->
            ctx.Response.Redirect($"/arenas/{arenaId}")
    }
```

In `Program.fs`, update `createInitialArenas`:
```fsharp
lifetime.ApplicationStarted.Register(fun () ->
    for _ in 1..initialGames do
        store.Create() |> ignore)
|> ignore
```

(The `|> ignore` on `store.Create()` is fine — startup ignores the `option`.)

- [ ] **Step 5: Build and run existing tests**

```bash
dotnet build src/TicTacToe.Web.Simple/
TEST_BASE_URL=http://localhost:5328 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test test/TicTacToe.Web.Simple.Tests/
```

Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/TicTacToe.Web.Simple/GameStore.fs src/TicTacToe.Web.Simple/Handlers.fs
git commit -m "feat(simple): MaxGames limit returns 409 when capacity reached"
```

---

### Task 3: Create Logger module

**Files:**
- Create: `src/TicTacToe.Web.Simple/Logger.fs`
- Modify: `src/TicTacToe.Web.Simple/TicTacToe.Web.Simple.fsproj`

- [ ] **Step 1: Create `src/TicTacToe.Web.Simple/Logger.fs`**

```fsharp
module TicTacToe.Web.Simple.Logger

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open TicTacToe.Model

/// Structured JSONL logger. No-op when logPath is None.
/// All writes are fire-and-forget via MailboxProcessor to avoid blocking request path.
type RequestLogger(?logPath: string) =
    let writer =
        logPath |> Option.map (fun path ->
            MailboxProcessor<string>.Start(fun inbox ->
                let rec loop () =
                    async {
                        let! line = inbox.Receive()
                        File.AppendAllText(path, line + "\n")
                        return! loop ()
                    }
                loop ()))

    let writeJson (obj: JsonObject) =
        match writer with
        | None -> ()
        | Some mp -> mp.Post(obj.ToJsonString())

    let boardArray (result: MoveResult) =
        let allPositions =
            [| TopLeft; TopCenter; TopRight
               MiddleLeft; MiddleCenter; MiddleRight
               BottomLeft; BottomCenter; BottomRight |]
        let gs =
            match result with
            | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs
        allPositions |> Array.map (fun pos ->
            match gs.TryGetValue(pos) with
            | true, Taken X -> "X"
            | true, Taken O -> "O"
            | _ -> "")

    member _.LogRequest(requestId: string, sessionId: string, gameId: string option, playerRole: string,
                        method: string, path: string, statusCode: int,
                        rejectionReason: string option,
                        boardBefore: MoveResult option, boardAfter: MoveResult option) =
        let obj = JsonObject()
        obj["request_id"] <- JsonValue.Create(requestId)
        obj["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("o"))
        obj["session_id"] <- JsonValue.Create(sessionId)
        obj["game_id"] <- gameId |> Option.map JsonValue.Create<string> |> Option.defaultValue (JsonValue.Create(null: string)) :> JsonNode
        obj["player_role"] <- JsonValue.Create(playerRole)
        obj["method"] <- JsonValue.Create(method)
        obj["path"] <- JsonValue.Create(path)
        obj["status_code"] <- JsonValue.Create(statusCode)
        obj["rejection_reason"] <- rejectionReason |> Option.map JsonValue.Create<string> |> Option.defaultValue (JsonValue.Create(null: string)) :> JsonNode
        obj["board_state_before"] <- boardBefore |> Option.map (fun r -> JsonNode.Parse(JsonSerializer.Serialize(boardArray r))) |> Option.defaultValue (JsonValue.Create(null: string) :> JsonNode)
        obj["board_state_after"] <- boardAfter |> Option.map (fun r -> JsonNode.Parse(JsonSerializer.Serialize(boardArray r))) |> Option.defaultValue (JsonValue.Create(null: string) :> JsonNode)
        writeJson obj

    member _.LogEvent(eventType: string, gameId: string, ?role: string, ?outcome: string, ?moveCount: int, ?move: string) =
        let obj = JsonObject()
        obj["event_type"] <- JsonValue.Create(eventType)
        obj["timestamp"] <- JsonValue.Create(DateTimeOffset.UtcNow.ToString("o"))
        obj["game_id"] <- JsonValue.Create(gameId)
        role |> Option.iter (fun r -> obj["role"] <- JsonValue.Create(r))
        outcome |> Option.iter (fun o -> obj["outcome"] <- JsonValue.Create(o))
        moveCount |> Option.iter (fun n -> obj["move_count"] <- JsonValue.Create(n))
        move |> Option.iter (fun m -> obj["move"] <- JsonValue.Create(m))
        writeJson obj
```

- [ ] **Step 2: Add `Logger.fs` to the compile list in `TicTacToe.Web.Simple.fsproj`**

Add before `Extensions.fs`:
```xml
<Compile Include="Logger.fs" />
```

The full `<ItemGroup>` compile list should be:
```xml
<ItemGroup>
    <Compile Include="GameStore.fs" />
    <Compile Include="Model.fs" />
    <Compile Include="Logger.fs" />
    <Compile Include="Extensions.fs" />
    <Compile Include="Auth.fs" />
    <Compile Include="templates/shared/layout.fs" />
    <Compile Include="templates/game.fs" />
    <Compile Include="templates/home.fs" />
    <Compile Include="Handlers.fs" />
    <Compile Include="Program.fs" />
</ItemGroup>
```

- [ ] **Step 3: Build to verify no compile errors**

```bash
dotnet build src/TicTacToe.Web.Simple/
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 4: Commit**

```bash
git add src/TicTacToe.Web.Simple/Logger.fs src/TicTacToe.Web.Simple/TicTacToe.Web.Simple.fsproj
git commit -m "feat(simple): RequestLogger JSONL writer, no-op when path unset"
```

---

### Task 4: Wire env-var config in Program.fs

**Files:**
- Modify: `src/TicTacToe.Web.Simple/Program.fs`

- [ ] **Step 1: Add env-var reads and DI wiring to `Program.fs`**

Add this before `configureServices`:

```fsharp
let private initialGames () =
    match System.Environment.GetEnvironmentVariable("TICTACTOE_INITIAL_GAMES") with
    | null | "" -> 6
    | s ->
        match System.Int32.TryParse(s) with
        | true, n when n > 0 -> n
        | _ -> 6

let private maxGames () =
    match System.Environment.GetEnvironmentVariable("TICTACTOE_MAX_GAMES") with
    | null | "" -> None
    | s ->
        match System.Int32.TryParse(s) with
        | true, n when n > 0 -> Some n
        | _ -> None

let private requestLogPath () =
    match System.Environment.GetEnvironmentVariable("TICTACTOE_REQUEST_LOG_PATH") with
    | null | "" -> None
    | s -> Some s
```

- [ ] **Step 2: Update `configureServices` to pass config to `GameStore` and `RequestLogger`**

Replace the `GameStore` and add `RequestLogger` registrations:

```fsharp
let configureServices (services: IServiceCollection) =
    services.AddRouting().AddHttpContextAccessor() |> ignore
    services.AddAntiforgery() |> ignore

    services
        .AddSingleton<GameStore>(fun _ -> GameStore(?maxGames = maxGames()))
        .AddSingleton<RequestLogger>(fun _ -> RequestLogger(?logPath = requestLogPath()))
        .AddSingleton<PlayerAssignmentManager>(fun _ -> PlayerAssignmentManager())
        .AddSingleton<IClaimsTransformation, GameUserClaimsTransformation>()
    |> ignore

    services
```

- [ ] **Step 3: Update `createInitialArenas` to use `initialGames()`**

```fsharp
let createInitialArenas (app: IApplicationBuilder) =
    let lifetime =
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>()
    let store = app.ApplicationServices.GetRequiredService<GameStore>()

    lifetime.ApplicationStarted.Register(fun () ->
        for _ in 1..initialGames() do
            store.Create() |> ignore)
    |> ignore

    app
```

- [ ] **Step 4: Build**

```bash
dotnet build src/TicTacToe.Web.Simple/
```

Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/TicTacToe.Web.Simple/Program.fs
git commit -m "feat(simple): TICTACTOE_INITIAL_GAMES, TICTACTOE_MAX_GAMES, TICTACTOE_REQUEST_LOG_PATH env vars"
```

---

### Task 5: Log events in Handlers.fs

**Files:**
- Modify: `src/TicTacToe.Web.Simple/Handlers.fs`

**Background:** Add `RequestLogger` calls to `createArena`, `getArena`, `makeMove`, `deleteArena`, and `restartArena`. Log game-lifecycle events from `makeMove` (player assigned, move accepted, game over).

- [ ] **Step 1: Add `RequestLogger` import and helper to `Handlers.fs`**

At the top of `Handlers.fs`, add to the `open` list:
```fsharp
open TicTacToe.Web.Simple.Logger
```

Add a private helper for session ID (derived from the cookie value):
```fsharp
let private sessionId (ctx: HttpContext) =
    ctx.Request.Cookies.TryGetValue("TicTacToe.SimpleUser")
    |> function true, v -> v | _ -> "anonymous"

let private requestId () = Guid.NewGuid().ToString()
```

- [ ] **Step 2: Add logging to `createArena`**

```fsharp
let createArena (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let logger = ctx.RequestServices.GetRequiredService<RequestLogger>()
        let rid = requestId()
        let sid = sessionId ctx
        match store.Create() with
        | None ->
            logger.LogRequest(rid, sid, None, "unassigned", "POST", "/arenas", 409, None, None, None)
            ctx.Response.StatusCode <- 409
            ctx.Response.ContentType <- "application/json"
            do! ctx.Response.WriteAsJsonAsync({| error = "MaxGamesReached" |})
        | Some (arenaId, _) ->
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", "/arenas", 302, None, None, None)
            logger.LogEvent("game_created", arenaId)
            ctx.Response.Redirect($"/arenas/{arenaId}")
    }
```

- [ ] **Step 3: Add logging to `makeMove`**

After resolving `currentResult` and before parsing the move, save the before-state. Then log the appropriate event after the result is known.

The key log points in `makeMove`:
- On `None` (game not found): log 404
- On parse failure: log 400, reason = `InvalidMove`
- On `Rejected reason`: log 403, reason = reason string
- On `Some(Error _)`: log 422, reason = `PositionTaken`
- On `Some nextResult` (success): log 202, then log `move_accepted` event, and if game over log `game_over`

Full updated `makeMove`:

```fsharp
let makeMove (ctx: HttpContext) =
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
        let logger = ctx.RequestServices.GetRequiredService<RequestLogger>()
        let arenaId = ctx.Request.RouteValues.["id"] |> string
        let userId = ctx.User.TryGetUserId()
        let rid = requestId()
        let sid = sessionId ctx
        let path = $"/arenas/{arenaId}"

        match store.Get(arenaId), userId with
        | None, _ ->
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 404, None, None, None)
            ctx.Response.StatusCode <- 404
        | _, None ->
            logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 401, None, None, None)
            ctx.Response.StatusCode <- 401
        | Some currentResult, Some uid ->
            let playerRaw =
                match ctx.Request.Form.TryGetValue("player") with
                | true, v -> v.ToString()
                | _ -> ""
            let positionRaw =
                match ctx.Request.Form.TryGetValue("position") with
                | true, v -> v.ToString()
                | _ -> ""

            match Move.TryParse(playerRaw, positionRaw) with
            | None ->
                logger.LogRequest(rid, sid, Some arenaId, "unassigned", "POST", path, 400,
                                  Some "InvalidMove", Some currentResult, None)
                if acceptsJson ctx then
                    ctx.Response.StatusCode <- 400
                else
                    do! renderArenaHtml ctx arenaId currentResult (Some "Invalid move format.")
            | Some move ->
                let xTurn = isXTurn currentResult
                let (validationResult, assignment) = assignmentManager.TryAssignAndValidate(arenaId, uid, xTurn)
                let playerRole =
                    match assignment.PlayerXId, assignment.PlayerOId with
                    | Some xId, _ when xId = uid -> "X"
                    | _, Some oId when oId = uid -> "O"
                    | _ -> "unassigned"

                match validationResult with
                | Allowed ->
                    match store.Update(arenaId, move) with
                    | None ->
                        logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 404, None, Some currentResult, None)
                        ctx.Response.StatusCode <- 404
                    | Some (Error(_, _)) ->
                        logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 422,
                                          Some "PositionTaken", Some currentResult, None)
                        if acceptsJson ctx then
                            ctx.Response.StatusCode <- 422
                            ctx.Response.ContentType <- "application/json"
                            do! ctx.Response.WriteAsJsonAsync({| error = "PositionTaken" |})
                        else
                            do! renderArenaHtml ctx arenaId currentResult (Some "That square is already taken.")
                    | Some nextResult ->
                        let statusCode = 200
                        logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, statusCode,
                                          None, Some currentResult, Some nextResult)
                        // Log move as the position string
                        logger.LogEvent("move_accepted", arenaId, role = playerRole,
                                        move = positionRaw)
                        // Log game lifecycle if terminal
                        match nextResult with
                        | Won(_, winner) ->
                            let moveCount =
                                let gs, _ = match nextResult with Won(gs, _) -> gs, () | _ -> match currentResult with XTurn(gs,_)|OTurn(gs,_)|Won(gs,_)|Draw gs|Error(gs,_) -> gs, ()
                                gs |> Seq.filter (fun kv -> kv.Value <> Empty) |> Seq.length
                            logger.LogEvent("game_over", arenaId, outcome = $"{winner}_wins", moveCount = moveCount)
                        | Draw _ ->
                            logger.LogEvent("game_over", arenaId, outcome = "draw", moveCount = 9)
                        | _ -> ()

                        if acceptsJson ctx then
                            ctx.Response.ContentType <- "application/json"
                            do! ctx.Response.WriteAsJsonAsync(toArenaJson arenaId nextResult)
                        else
                            do! renderArenaHtml ctx arenaId nextResult None

                | Rejected reason ->
                    let reasonStr = reason.ToString()
                    logger.LogRequest(rid, sid, Some arenaId, playerRole, "POST", path, 403,
                                      Some reasonStr, Some currentResult, None)
                    let msg =
                        match reason with
                        | OutOfTurn -> "It's not your turn."
                        | NotAPlayer -> "You are not a player in this arena."
                        | PositionTaken -> "That square is already taken."
                        | GameOver -> "This game is already over."
                        | InvalidMove -> "Invalid move."

                    if acceptsJson ctx then
                        ctx.Response.StatusCode <- 403
                        ctx.Response.ContentType <- "application/json"
                        do! ctx.Response.WriteAsJsonAsync({| error = reasonStr |})
                    else
                        do! renderArenaHtml ctx arenaId currentResult (Some msg)
    }
```

- [ ] **Step 4: Build**

```bash
dotnet build src/TicTacToe.Web.Simple/
```

Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add src/TicTacToe.Web.Simple/Handlers.fs
git commit -m "feat(simple): JSONL request + lifecycle event logging in handlers"
```

---

### Task 6: Add tests

**Files:**
- Modify: `test/TicTacToe.Web.Simple.Tests/GameTests.fs`

> These are Playwright integration tests. Start the server first:
> ```bash
> DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet run --project src/TicTacToe.Web.Simple/ --urls http://localhost:5328 &
> ```

- [ ] **Step 1: Add typed-rejection tests to `GameTests.fs`**

Add a new `[<TestFixture>]` at the end of the file:

```fsharp
/// Tests for typed rejection reasons and MaxGames enforcement.
/// Uses HttpClient (not browser) to inspect JSON responses directly.
[<TestFixture>]
type RejectionTests() =
    inherit TestBase()

    let httpClient = new System.Net.Http.HttpClient()

    let makeJsonMove (arenaUrl: string) (cookies: string) (player: string) (position: string) =
        task {
            use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, arenaUrl)
            req.Headers.Add("Accept", "application/json")
            req.Headers.Add("Cookie", cookies)
            let body = $"player={player}&position={position}"
            req.Content <- new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            let! resp = httpClient.SendAsync(req)
            let! json = resp.Content.ReadAsStringAsync()
            return (int resp.StatusCode, json)
        }

    [<Test>]
    member this.``out-of-turn move returns 403 OutOfTurn``() : Task =
        task {
            // Navigate to a fresh arena as player 1
            let! arenaUrl = createArena this.Page this.TimeoutMs
            let cookies = this.Page.Context.CookiesAsync([| arenaUrl |]).Result
                          |> Seq.map (fun c -> $"{c.Name}={c.Value}")
                          |> String.concat "; "
            // First move as X (this player claims X)
            let! (_, _) = makeJsonMove arenaUrl cookies "X" "TopLeft"
            // Try to move again as X (it's O's turn now)
            let! (status, body) = makeJsonMove arenaUrl cookies "X" "TopCenter"
            Assert.That(status, Is.EqualTo(403))
            let doc = System.Text.Json.JsonNode.Parse(body) :?> System.Text.Json.Nodes.JsonObject
            Assert.That(doc["error"].GetValue<string>(), Is.EqualTo("OutOfTurn"))
        }

    [<Test>]
    member this.``third player returns 403 NotAPlayer``() : Task =
        task {
            let! arenaUrl = createArena this.Page this.TimeoutMs
            // Player 1 (X): claim X slot
            let! p1Cookies =
                task {
                    let cookies = this.Page.Context.CookiesAsync([| arenaUrl |]).Result
                                  |> Seq.map (fun c -> $"{c.Name}={c.Value}")
                                  |> String.concat "; "
                    return cookies
                }
            let! _ = makeJsonMove arenaUrl p1Cookies "X" "TopLeft"

            // Player 2 (O): claim O slot via second browser context
            let! player2Page = this.CreateSecondPlayer(arenaUrl)
            let p2Cookies =
                player2Page.Context.CookiesAsync([| arenaUrl |]).Result
                |> Seq.map (fun c -> $"{c.Name}={c.Value}")
                |> String.concat "; "
            let! _ = makeJsonMove arenaUrl p2Cookies "O" "TopCenter"

            // Player 3: fresh session, tries to play
            let! player3Page = this.CreateSecondPlayer(arenaUrl)
            let p3Cookies =
                player3Page.Context.CookiesAsync([| arenaUrl |]).Result
                |> Seq.map (fun c -> $"{c.Name}={c.Value}")
                |> String.concat "; "
            let! (status, body) = makeJsonMove arenaUrl p3Cookies "X" "TopRight"
            Assert.That(status, Is.EqualTo(403))
            let doc = System.Text.Json.JsonNode.Parse(body) :?> System.Text.Json.Nodes.JsonObject
            Assert.That(doc["error"].GetValue<string>(), Is.EqualTo("NotAPlayer"))
        }
```

- [ ] **Step 2: Add MaxGames test**

Add to the `HomePageTests` fixture:

```fsharp
[<Test>]
member this.``POST /arenas returns 409 when TICTACTOE_MAX_GAMES reached``() : Task =
    // This test requires the server to be started with TICTACTOE_MAX_GAMES=1.
    // Skip if TICTACTOE_MAX_GAMES is not set (default server has no limit).
    // To run: TICTACTOE_MAX_GAMES=1 TICTACTOE_INITIAL_GAMES=1 dotnet run ... &
    task {
        let maxGamesEnv = System.Environment.GetEnvironmentVariable("TICTACTOE_TEST_MAX_GAMES_ENABLED")
        if maxGamesEnv = "1" then
            use client = new System.Net.Http.HttpClient()
            // login to get a cookie
            let! loginResp = client.GetAsync($"{this.BaseUrl}/login")
            let cookieHeader =
                if loginResp.Headers.Contains("Set-Cookie") then
                    loginResp.Headers.GetValues("Set-Cookie") |> String.concat "; "
                else ""
            use req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, $"{this.BaseUrl}/arenas")
            req.Headers.Add("Accept", "application/json")
            if cookieHeader <> "" then
                req.Headers.Add("Cookie", cookieHeader)
            req.Content <- new System.Net.Http.StringContent("", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")
            let! resp = client.SendAsync(req)
            Assert.That(int resp.StatusCode, Is.EqualTo(409))
        else
            Assert.Pass("Skipped: set TICTACTOE_TEST_MAX_GAMES_ENABLED=1 to run")
    }
```

- [ ] **Step 3: Run all Simple tests**

```bash
TEST_BASE_URL=http://localhost:5328 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet test test/TicTacToe.Web.Simple.Tests/
```

Expected: all tests pass (the MaxGames test self-skips unless the env var is set).

- [ ] **Step 4: Commit**

```bash
git add test/TicTacToe.Web.Simple.Tests/GameTests.fs
git commit -m "test(simple): typed rejection + MaxGames Playwright tests"
```

---

### Task 7: Full build and test gate

- [ ] **Step 1: Full build**

```bash
dotnet build
```

Expected: `Build succeeded.`

- [ ] **Step 2: Engine + orchestrator unit tests**

```bash
dotnet test test/TicTacToe.Engine.Tests/
dotnet test test/TicTacToe.Orchestrator.Tests/
```

Expected: all pass.

- [ ] **Step 3: Verify JSONL output manually**

```bash
TICTACTOE_REQUEST_LOG_PATH=/tmp/ttt-test.jsonl \
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
  dotnet run --project src/TicTacToe.Web.Simple/ --urls http://localhost:5329 &>/tmp/ttt-server.log &
sleep 2
# Make a request via curl (get a cookie first)
curl -c /tmp/ttt-cookies.txt http://localhost:5329/login -L -s -o /dev/null
curl -b /tmp/ttt-cookies.txt -X POST http://localhost:5329/arenas -H "Accept: application/json" -s
sleep 1
cat /tmp/ttt-test.jsonl | head -5
kill %1
```

Expected: at least one JSONL line with `request_id`, `timestamp`, `session_id`, `game_id`, `method`, `status_code`.

- [ ] **Step 4: Final commit**

```bash
git add -p  # review any remaining unstaged changes
git commit -m "feat(simple): server structured logging complete"
```
