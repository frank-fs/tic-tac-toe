# ERPC Identity Handshake Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the self-asserted `player` argument in the ERPC arm's `make_move` with a claim-based identity handshake, so the server derives each caller's seat from an authenticated token.

**Architecture:** A new `authenticate()` tool mints a token and stores it in a per-connection `SessionIdentity`. An incoming MCP message filter presents that token as a `ClaimsPrincipal` on every request. `make_move(gameId, position)` reads identity from the injected claim and resolves the seat via a `PlayerAssignmentStore` whose decision table mirrors the Web arm. All seat/identity logic lives in a new `Identity.fs` so it is unit-testable independent of MCP plumbing.

**Tech Stack:** F# / .NET 10, ModelContextProtocol 1.2.0 (stdio), Expecto tests, MailboxProcessor for the assignment store.

---

## File Structure

- `experiments/mcp-rpc/Identity.fs` — **new.** `RejectionReason`, `MoveValidationResult`, `MoveOutcome` DUs; `SessionIdentity` (per-connection token holder); `PlayerAssignmentStore` (token→seat decision table); `resolveMove` (game + assignment resolution). No MCP dependency.
- `experiments/mcp-rpc/Tools.fs` — **modify.** Static type → instance `TicTacToeTools(supervisor, session, assignments)`; add `authenticate`; rewrite `make_move` to `(ClaimsPrincipal, gameId, position)`; reads unchanged. Remove module-level `supervisor`.
- `experiments/mcp-rpc/Program.fs` — **modify.** Register `GameSupervisor`, `SessionIdentity`, `PlayerAssignmentStore` singletons; add message filter that sets `context.User` from `SessionIdentity`.
- `experiments/mcp-rpc/TicTacToe.McpRpc.fsproj` — **modify.** Add `Identity.fs` to compile order (before `Tools.fs`).
- `test/TicTacToe.McpRpc.Tests/` — **new.** Expecto project referencing `TicTacToe.McpRpc`. `IdentityTests.fs`, `ResolveMoveTests.fs`, `Main.fs`.

---

## Task 1: Test project scaffold + SessionIdentity

**Files:**
- Create: `test/TicTacToe.McpRpc.Tests/TicTacToe.McpRpc.Tests.fsproj`
- Create: `test/TicTacToe.McpRpc.Tests/Main.fs`
- Create: `test/TicTacToe.McpRpc.Tests/IdentityTests.fs`
- Create: `experiments/mcp-rpc/Identity.fs`
- Modify: `experiments/mcp-rpc/TicTacToe.McpRpc.fsproj`

- [ ] **Step 1: Create the test project file**

Create `test/TicTacToe.McpRpc.Tests/TicTacToe.McpRpc.Tests.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateProgramFile>false</GenerateProgramFile>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="IdentityTests.fs" />
    <Compile Include="ResolveMoveTests.fs" />
    <Compile Include="Main.fs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
    <PackageReference Include="Expecto" Version="10.*" />
    <PackageReference Include="YoloDev.Expecto.TestSdk" Version="0.15.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\experiments\mcp-rpc\TicTacToe.McpRpc.fsproj" />
  </ItemGroup>
</Project>
```

Note: `ResolveMoveTests.fs` is listed now (created in Task 4) so the compile order is final. Create a placeholder so the project compiles in this task — see Step 2.

- [ ] **Step 2: Create placeholder ResolveMoveTests.fs and Main.fs**

Create `test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs`:

```fsharp
module TicTacToe.McpRpc.Tests.ResolveMoveTests
// Tests added in Task 4.
```

Create `test/TicTacToe.McpRpc.Tests/Main.fs`:

```fsharp
module TicTacToe.McpRpc.Tests.Main

open Expecto

[<EntryPoint>]
let main argv =
    Tests.runTestsInAssemblyWithCLIArgs [] argv
```

- [ ] **Step 3: Write the failing test for SessionIdentity**

Create `test/TicTacToe.McpRpc.Tests/IdentityTests.fs`:

```fsharp
module TicTacToe.McpRpc.Tests.IdentityTests

open Expecto
open TicTacToe.McpRpc.Identity

[<Tests>]
let sessionIdentityTests =
    testList
        "SessionIdentity"
        [ testCase "starts unauthenticated"
          <| fun _ ->
              let s = SessionIdentity()
              Expect.isNone s.Current "no token before authenticate"

          testCase "Authenticate sets a token and returns it"
          <| fun _ ->
              let s = SessionIdentity()
              let t = s.Authenticate()
              Expect.isNotNull t "token is returned"
              Expect.equal s.Current (Some t) "Current reflects the minted token"

          testCase "Authenticate mints distinct tokens"
          <| fun _ ->
              let a = SessionIdentity().Authenticate()
              let b = SessionIdentity().Authenticate()
              Expect.notEqual a b "tokens are unique" ]
```

- [ ] **Step 4: Add Identity.fs to the mcp-rpc compile order**

In `experiments/mcp-rpc/TicTacToe.McpRpc.fsproj`, change the `<Compile>` block so `Identity.fs` precedes `Tools.fs`:

```xml
  <ItemGroup>
    <Compile Include="Identity.fs" />
    <Compile Include="Tools.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
```

- [ ] **Step 5: Create Identity.fs with SessionIdentity only**

Create `experiments/mcp-rpc/Identity.fs`:

```fsharp
module TicTacToe.McpRpc.Identity

open System
open TicTacToe.Model

/// Per-connection authenticated identity. On stdio one process == one
/// connection == one agent, so a single instance is the connection's session.
type SessionIdentity() =
    let mutable token: string option = None

    /// Mint a new identity token for this connection and return it.
    member _.Authenticate() : string =
        let t = Guid.NewGuid().ToString("N")
        token <- Some t
        t

    /// The currently authenticated token, if any.
    member _.Current: string option = token
```

- [ ] **Step 6: Register the test project in the solution**

Run: `dotnet sln TicTacToe.sln add test/TicTacToe.McpRpc.Tests/TicTacToe.McpRpc.Tests.fsproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: PASS — 3 SessionIdentity tests green.

- [ ] **Step 8: Commit**

```bash
git add experiments/mcp-rpc/Identity.fs experiments/mcp-rpc/TicTacToe.McpRpc.fsproj test/TicTacToe.McpRpc.Tests/ TicTacToe.sln
git commit -m "test(erpc): SessionIdentity token handshake (#67)"
```

---

## Task 2: PlayerAssignmentStore decision table

Mirrors `src/TicTacToe.Web/Model.fs:58-103`, with `userId` = token and `Allowed` carrying the assigned side.

**Files:**
- Modify: `experiments/mcp-rpc/Identity.fs`
- Modify: `test/TicTacToe.McpRpc.Tests/IdentityTests.fs`

- [ ] **Step 1: Write failing tests for the assignment store**

Append to `test/TicTacToe.McpRpc.Tests/IdentityTests.fs`:

```fsharp
open TicTacToe.Model

[<Tests>]
let assignmentStoreTests =
    testList
        "PlayerAssignmentStore"
        [ testCase "first mover on X's turn is assigned X"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              let r = store.TryAssignAndValidate("g1", "tokA", true)
              Expect.equal r (Allowed X) "tokA binds to X"

          testCase "second distinct token on O's turn is assigned O"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              store.TryAssignAndValidate("g1", "tokA", true) |> ignore
              let r = store.TryAssignAndValidate("g1", "tokB", false)
              Expect.equal r (Allowed O) "tokB binds to O"

          testCase "X player moving on O's turn is rejected NotYourTurn"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              store.TryAssignAndValidate("g1", "tokA", true) |> ignore
              store.TryAssignAndValidate("g1", "tokB", false) |> ignore
              let r = store.TryAssignAndValidate("g1", "tokA", false)
              Expect.equal r (Rejected NotYourTurn) "X cannot move on O's turn"

          testCase "third token in a full game is rejected NotAPlayer"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              store.TryAssignAndValidate("g1", "tokA", true) |> ignore
              store.TryAssignAndValidate("g1", "tokB", false) |> ignore
              let r = store.TryAssignAndValidate("g1", "tokC", true)
              Expect.equal r (Rejected NotAPlayer) "spectator rejected"

          testCase "one token holds independent seats across two games"
          <| fun _ ->
              let store = PlayerAssignmentStore()
              let r1 = store.TryAssignAndValidate("g1", "tokA", true)
              let r2 = store.TryAssignAndValidate("g2", "tokA", true)
              Expect.equal r1 (Allowed X) "X in g1"
              Expect.equal r2 (Allowed X) "X in g2 — independent binding" ]
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: FAIL — `PlayerAssignmentStore`, `Allowed`, `Rejected`, `NotYourTurn`, `NotAPlayer` not defined.

- [ ] **Step 3: Add the DUs and store to Identity.fs**

Insert into `experiments/mcp-rpc/Identity.fs` after the `open` lines and before `SessionIdentity`:

```fsharp
/// Why a move was rejected (subset relevant to the ERPC arm).
type RejectionReason =
    | NotYourTurn
    | NotAPlayer

/// Result of assigning + validating a caller's move.
type MoveValidationResult =
    | Allowed of Player
    | Rejected of RejectionReason

/// Per-game seat binding: token assigned to X and/or O.
type private Assignment =
    { PlayerXId: string option
      PlayerOId: string option }

let private emptyAssignment = { PlayerXId = None; PlayerOId = None }

type private StoreMessage =
    | TryAssign of
        gameId: string *
        token: string *
        isXTurn: bool *
        AsyncReplyChannel<MoveValidationResult>

/// Thread-safe (token, gameId) -> seat binding. Decision table mirrors
/// src/TicTacToe.Web/Model.fs:58-103, emitting the assigned side on success.
type PlayerAssignmentStore() =
    let decide (a: Assignment) (token: string) (isXTurn: bool) : MoveValidationResult * Assignment =
        match a.PlayerXId, a.PlayerOId, isXTurn with
        | None, _, true -> Allowed X, { a with PlayerXId = Some token }
        | Some xId, None, false when xId <> token -> Allowed O, { a with PlayerOId = Some token }
        | Some xId, _, true when xId = token -> Allowed X, a
        | _, Some oId, false when oId = token -> Allowed O, a
        | Some xId, Some _, false when xId = token -> Rejected NotYourTurn, a
        | Some _, Some oId, true when oId = token -> Rejected NotYourTurn, a
        | Some xId, Some oId, _ when xId <> token && oId <> token -> Rejected NotAPlayer, a
        | Some xId, None, false when xId = token -> Rejected NotYourTurn, a
        | None, _, false -> Rejected NotAPlayer, a
        | _ -> Rejected NotAPlayer, a

    let agent =
        MailboxProcessor<StoreMessage>.Start(fun inbox ->
            let rec loop (state: Map<string, Assignment>) =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | TryAssign(gameId, token, isXTurn, reply) ->
                        let current = state |> Map.tryFind gameId |> Option.defaultValue emptyAssignment
                        let result, updated = decide current token isXTurn
                        reply.Reply result
                        return! loop (state |> Map.add gameId updated)
                }

            loop Map.empty)

    /// Assign the token to an open seat (lazily, on first move) and validate the turn.
    member _.TryAssignAndValidate(gameId: string, token: string, isXTurn: bool) : MoveValidationResult =
        agent.PostAndReply(fun reply -> TryAssign(gameId, token, isXTurn, reply))
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: PASS — all SessionIdentity + PlayerAssignmentStore tests green.

- [ ] **Step 5: Commit**

```bash
git add experiments/mcp-rpc/Identity.fs test/TicTacToe.McpRpc.Tests/IdentityTests.fs
git commit -m "feat(erpc): token-keyed seat assignment store mirroring Web decision table (#67)"
```

---

## Task 3: resolveMove — game + assignment resolution

`resolveMove` ties the supervisor, assignment store, and claim token together and returns a typed `MoveOutcome`. The MCP tool (Task 4) only maps a `ClaimsPrincipal` to a token and boxes this outcome.

**Files:**
- Modify: `experiments/mcp-rpc/Identity.fs`
- Modify: `test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs`

- [ ] **Step 1: Write failing tests for resolveMove**

Replace `test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs` with:

```fsharp
module TicTacToe.McpRpc.Tests.ResolveMoveTests

open Expecto
open TicTacToe.Engine
open TicTacToe.McpRpc.Identity

let private freshGame () =
    let sup = createGameSupervisor ()
    let gameId, _ = sup.CreateGame()
    sup, gameId

[<Tests>]
let resolveMoveTests =
    testList
        "resolveMove"
        [ testCase "no token -> unauthenticated"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              let r = resolveMove sup store None gameId "TopLeft"
              Expect.equal r (Rejected "unauthenticated") "missing claim is rejected"

          testCase "unknown game -> game_not_found"
          <| fun _ ->
              let sup, _ = freshGame ()
              let store = PlayerAssignmentStore()
              let r = resolveMove sup store (Some "tokA") "no-such-game" "TopLeft"
              Expect.equal r (Rejected "game_not_found") "unknown gameId rejected"

          testCase "authenticated first move succeeds and binds X"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()

              match resolveMove sup store (Some "tokA") gameId "TopLeft" with
              | Moved(board, whoseTurn, status) ->
                  Expect.equal board.[0] "X" "X placed at TopLeft"
                  Expect.equal whoseTurn "O" "now O's turn"
                  Expect.equal status "in_progress" "game continues"
              | other -> failtestf "expected Moved, got %A" other

          testCase "second token moving out of turn -> not_your_turn"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              resolveMove sup store (Some "tokA") gameId "TopLeft" |> ignore
              // tokB has not bound a seat; it is O's turn, so tokA (X) moving again:
              let r = resolveMove sup store (Some "tokA") gameId "TopCenter"
              Expect.equal r (Rejected "not_your_turn") "X cannot move on O's turn"

          testCase "occupied square -> position_taken"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              resolveMove sup store (Some "tokA") gameId "TopLeft" |> ignore
              let r = resolveMove sup store (Some "tokB") gameId "TopLeft"
              Expect.equal r (Rejected "position_taken") "cannot replay a taken square"

          testCase "invalid position string -> invalid_input"
          <| fun _ ->
              let sup, gameId = freshGame ()
              let store = PlayerAssignmentStore()
              let r = resolveMove sup store (Some "tokA") gameId "Nope"
              Expect.equal r (Rejected "invalid_input") "unparseable position rejected" ]
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: FAIL — `resolveMove`, `Moved` not defined.

- [ ] **Step 3: Add MoveOutcome and resolveMove to Identity.fs**

Append to `experiments/mcp-rpc/Identity.fs`:

```fsharp
open TicTacToe.Engine

/// Outcome of a move attempt, ready to be boxed into the MCP JSON response.
type MoveOutcome =
    | Moved of board: string[] * whoseTurn: string * status: string
    | Rejected of code: string

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) : string[] =
    allPositions
    |> Array.map (fun pos ->
        match gs.TryGetValue pos with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private stateOf (result: MoveResult) : GameState =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

let private whoseTurnStr (result: MoveResult) =
    match result with
    | XTurn _ -> "X"
    | OTurn _ -> "O"
    | Won(_, p) -> sprintf "%O won" p
    | Draw _ -> "draw"
    | Error _ -> "error"

let private statusStr (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error(_, msg) -> sprintf "error: %s" msg

/// Resolve a move attempt: authenticate, locate game, validate turn via the
/// assignment store, derive the side from the claim, apply, and shape the result.
let resolveMove
    (supervisor: GameSupervisor)
    (assignments: PlayerAssignmentStore)
    (token: string option)
    (gameId: string)
    (position: string)
    : MoveOutcome =
    match token with
    | None -> Rejected "unauthenticated"
    | Some token ->
        match supervisor.GetGame gameId with
        | None -> Rejected "game_not_found"
        | Some game ->
            match game.GetState() with
            | Won _
            | Draw _ -> Rejected "game_over"
            | Error _ -> Rejected "invalid_move"
            | (XTurn _ | OTurn _) as before ->
                let isXTurn = (match before with | XTurn _ -> true | _ -> false)

                match assignments.TryAssignAndValidate(gameId, token, isXTurn) with
                | MoveValidationResult.Rejected NotYourTurn -> Rejected "not_your_turn"
                | MoveValidationResult.Rejected NotAPlayer -> Rejected "game_full"
                | Allowed side ->
                    match SquarePosition.TryParse position with
                    | None -> Rejected "invalid_input"
                    | Some pos ->
                        let move = match side with | X -> XMove pos | O -> OMove pos
                        game.MakeMove move

                        match game.GetState() with
                        | Error _ -> Rejected "position_taken"
                        | after -> Moved(renderBoard (stateOf after), whoseTurnStr after, statusStr after)
```

Note: `MoveValidationResult.Rejected` / `MoveOutcome.Rejected` are disambiguated by qualifying the validation-result cases. Both DUs use `Rejected`; F# resolves by last-declared, so qualify the `MoveValidationResult` matches as shown.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: PASS — all resolveMove tests green.

- [ ] **Step 5: Commit**

```bash
git add experiments/mcp-rpc/Identity.fs test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs
git commit -m "feat(erpc): resolveMove ties claim token to seat-derived engine move (#67)"
```

---

## Task 4: Rewrite Tools.fs — instance tools, authenticate, claim-based make_move

**Files:**
- Modify: `experiments/mcp-rpc/Tools.fs`
- Modify: `test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs`

- [ ] **Step 1: Write a failing test for the tool surface (claim-driven)**

Append to `test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs`:

```fsharp
open System.Security.Claims
open TicTacToe.McpRpc

let private principal (token: string) =
    ClaimsPrincipal(ClaimsIdentity([ Claim(ClaimTypes.Name, token) ], "Test", ClaimTypes.Name, ClaimTypes.Role))

[<Tests>]
let toolsTests =
    testList
        "TicTacToeTools"
        [ testCase "authenticate returns a token and binds the session"
          <| fun _ ->
              let session = SessionIdentity()
              let tools = TicTacToeTools(createGameSupervisor (), session, PlayerAssignmentStore())
              let resp = tools.authenticate ()
              Expect.isNotNull resp.token "token returned"
              Expect.equal session.Current (Some resp.token) "session bound to minted token"

          testCase "make_move with no claim is unauthenticated"
          <| fun _ ->
              let sup = createGameSupervisor ()
              let gameId, _ = sup.CreateGame()
              let tools = TicTacToeTools(sup, SessionIdentity(), PlayerAssignmentStore())
              let resp = tools.make_move (null, gameId, "TopLeft") :?> {| error: string |}
              Expect.equal resp.error "unauthenticated" "no claim rejected"

          testCase "make_move with a claim places the mark"
          <| fun _ ->
              let sup = createGameSupervisor ()
              let gameId, _ = sup.CreateGame()
              let tools = TicTacToeTools(sup, SessionIdentity(), PlayerAssignmentStore())
              let resp = tools.make_move (principal "tokA", gameId, "TopLeft")
              // success shape carries a board with X at index 0
              let board = (resp.GetType().GetProperty("board").GetValue(resp)) :?> string[]
              Expect.equal board.[0] "X" "X placed via claim identity" ]
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: FAIL — `TicTacToeTools` constructor signature and `authenticate` not matching.

- [ ] **Step 3: Rewrite Tools.fs**

Replace the entire contents of `experiments/mcp-rpc/Tools.fs` with:

```fsharp
module TicTacToe.McpRpc.Tools

open System.ComponentModel
open System.Security.Claims
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Model
open TicTacToe.McpRpc.Identity

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gameState: GameState) : string[] =
    allPositions
    |> Array.map (fun pos ->
        match gameState.TryGetValue(pos) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private whoseTurn (result: MoveResult) =
    match result with
    | XTurn _ -> "X"
    | OTurn _ -> "O"
    | Won(_, player) -> sprintf "%O won" player
    | Draw _ -> "draw"
    | Error _ -> "error"

let private statusStr (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error(_, msg) -> sprintf "error: %s" msg

let private validMoves (result: MoveResult) : string[] =
    match result with
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

let private stateOf (result: MoveResult) : GameState =
    match result with
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

type AuthResponse = { token: string }
type NewGameResponse = { gameId: string }

[<McpServerToolType>]
type TicTacToeTools
    (
        supervisor: GameSupervisor,
        session: SessionIdentity,
        assignments: PlayerAssignmentStore
    ) =

    [<McpServerTool>]
    [<Description("Authenticate as a player. Returns a token bound to this connection. Call this once before make_move; subsequent calls carry your identity automatically.")>]
    member _.authenticate() : AuthResponse =
        { token = session.Authenticate() }

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns a gameId to use in subsequent calls. X always moves first.")>]
    member _.new_game() : NewGameResponse =
        let gameId, _ = supervisor.CreateGame()
        { gameId = gameId }

    [<McpServerTool>]
    [<Description("Make a move. You must authenticate first; the server derives your side (X or O) from your identity. position must be one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.make_move
        (
            user: ClaimsPrincipal,
            [<Description("The game ID returned by new_game")>] gameId: string,
            [<Description("Board position: TopLeft | TopCenter | TopRight | MiddleLeft | MiddleCenter | MiddleRight | BottomLeft | BottomCenter | BottomRight")>] position: string
        ) : obj =
        let token =
            match user with
            | null -> None
            | u -> u.Identity |> Option.ofObj |> Option.bind (fun i -> Option.ofObj i.Name)

        match resolveMove supervisor assignments token gameId position with
        | Moved(board, turn, status) -> box {| board = board; whoseTurn = turn; status = status |}
        | MoveOutcome.Rejected code -> box {| error = code; position = position |}

    [<McpServerTool>]
    [<Description("Get the current board state for a game: 9 cells (index 0=TopLeft to 8=BottomRight), whose turn it is, status, and valid moves.")>]
    member _.get_board([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame(gameId) with
        | None -> box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            let result = game.GetState()
            box
                {| board = renderBoard (stateOf result)
                   whoseTurn = whoseTurn result
                   status = statusStr result
                   validMoves = validMoves result |}

    [<McpServerTool>]
    [<Description("Get full game state including board, turn, status, and valid moves for the game.")>]
    member _.get_state([<Description("The game ID returned by new_game")>] gameId: string) : obj =
        match supervisor.GetGame(gameId) with
        | None -> box {| error = "game_not_found"; gameId = gameId |}
        | Some game ->
            let result = game.GetState()
            box
                {| gameId = gameId
                   board = renderBoard (stateOf result)
                   whoseTurn = whoseTurn result
                   status = statusStr result
                   validMoves = validMoves result |}
```

Note: `make_move` returns an anonymous-record success shape `{| board; whoseTurn; status |}` (matching the prior `MoveResponse` fields). The test reads `board` via reflection because anonymous records are distinct types per shape.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: PASS — TicTacToeTools tests green plus all prior tests.

- [ ] **Step 5: Commit**

```bash
git add experiments/mcp-rpc/Tools.fs test/TicTacToe.McpRpc.Tests/ResolveMoveTests.fs
git commit -m "feat(erpc): instance tools, authenticate handshake, claim-based make_move; drop self-asserted player (#67)"
```

---

## Task 5: Wire Program.fs — DI singletons + stdio identity message filter

This wiring is verified by build + a manual stdio smoke (no unit test — it is MCP-transport plumbing).

**Files:**
- Modify: `experiments/mcp-rpc/Program.fs`

- [ ] **Step 1: Rewrite Program.fs**

Replace the entire contents of `experiments/mcp-rpc/Program.fs` with:

```fsharp
module TicTacToe.McpRpc.Program

open System.Security.Claims
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.McpRpc.Identity

let private configureLogging (builder: HostApplicationBuilder) =
    // All logs go to stderr; stdout is reserved for MCP JSON-RPC communication
    builder.Logging.AddConsole(fun opts -> opts.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

[<EntryPoint>]
let main _ =
    let builder = Host.CreateApplicationBuilder()
    configureLogging builder

    builder.Services.AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor ()) |> ignore
    builder.Services.AddSingleton<SessionIdentity>() |> ignore
    builder.Services.AddSingleton<PlayerAssignmentStore>() |> ignore

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithMessageFilters(fun filters ->
            filters.AddIncomingFilter(fun next ->
                fun (context: JsonRpcMessageContext) (ct: CancellationToken) ->
                    let session = context.Services.GetService<SessionIdentity>()

                    match (if isNull (box session) then None else session.Current) with
                    | Some token ->
                        let identity =
                            ClaimsIdentity(
                                [ Claim(ClaimTypes.Name, token) ],
                                "StdioAuth",
                                ClaimTypes.Name,
                                ClaimTypes.Role
                            )

                        context.User <- ClaimsPrincipal(identity)
                    | None -> ()

                    next.Invoke(context, ct))
            |> ignore)
        .WithTools<Tools.TicTacToeTools>()
    |> ignore

    builder.Build().Run()
    0
```

Note on the filter delegate: the SDK's `AddIncomingFilter` takes `Func<Handler, Handler>` where `Handler` is `Func<JsonRpcMessageContext, CancellationToken, Task>` (or `ValueTask`). If the build reports a delegate-type mismatch, adjust the inner lambda to return the exact task type the SDK's handler delegate expects (wrap with `Task.CompletedTask`/`:> Task` as needed) — the logic (read `SessionIdentity.Current`, set `context.User`, call `next`) stays identical. Confirm the exact `JsonRpcMessageContext` member names (`Services`, `User`) against the installed `ModelContextProtocol` 1.2.0 assembly.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build experiments/mcp-rpc/TicTacToe.McpRpc.fsproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full test project**

Run: `dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: PASS — all tests green.

- [ ] **Step 4: Manual stdio smoke — authenticate then move**

Run (from `experiments/mcp-rpc`):

```bash
dotnet build -c Release
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"new_game","arguments":{}}}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"authenticate","arguments":{}}}' \
  | dotnet bin/Release/net10.0/TicTacToe.McpRpc.dll 2>/dev/null
```

Expected: JSON-RPC responses on stdout — id 2 returns a `gameId`; id 3 returns a `token`. (A full move requires threading the gameId from id 2 into a follow-up call; the smoke confirms the server starts, tools list, and the handshake responds. The behavioral guarantees are covered by the unit tests in Tasks 3–4.)

- [ ] **Step 5: Verify make_move no longer exposes a `player` argument**

Run (from `experiments/mcp-rpc`):

```bash
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | dotnet bin/Release/net10.0/TicTacToe.McpRpc.dll 2>/dev/null
```

Expected: the `make_move` tool's `inputSchema` lists `gameId` and `position` only — **no `player`, no `token`** (the `ClaimsPrincipal` is excluded from the schema by the SDK).

- [ ] **Step 6: Commit**

```bash
git add experiments/mcp-rpc/Program.fs
git commit -m "feat(erpc): DI singletons + stdio message filter presenting token as ClaimsPrincipal (#67)"
```

---

## Task 6: Close-out — full build/test + issue link

**Files:** none (verification + bookkeeping)

- [ ] **Step 1: Full solution build**

Run: `dotnet build TicTacToe.sln`
Expected: Build succeeded, 0 errors (warnings acceptable if pre-existing).

- [ ] **Step 2: Run engine + new tests**

Run: `dotnet test test/TicTacToe.Engine.Tests/ && dotnet test test/TicTacToe.McpRpc.Tests/`
Expected: all PASS.

- [ ] **Step 3: Self-review against the spec**

Confirm each acceptance item from `docs/superpowers/specs/2026-06-16-erpc-identity-handshake-design.md`:
- `make_move` takes `(gameId, position)` only — no `player`, no `token` in schema (Task 5 Step 5).
- `authenticate()` mints a token; identity rides the claim (Tasks 1, 4, 5).
- Lazy per-game seat binding mirrors the Web decision table (Task 2).
- Error codes: `unauthenticated`, `not_your_turn`, `game_full`, plus existing (Task 3).
- Module-level `supervisor` removed; tools are instance + DI (Tasks 4, 5).

- [ ] **Step 4: Final commit if any cleanup was needed; otherwise done.**

The branch `erpc-identity-handshake` is ready for `git merge --ff-only` into the main worktree, or a PR.
```

