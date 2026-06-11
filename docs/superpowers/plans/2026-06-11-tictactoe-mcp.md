# TicTacToe.Mcp Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a stdio MCP server that wraps `TicTacToe.Engine` and exposes four tools (`new_game`, `get_board`, `make_move`, `get_state`) so the E_RPC orchestrator cell can reach game logic via the same MCP client as other cells.

**Architecture:** Single .NET console app using `ModelContextProtocol` SDK's `StdioServerTransport`. A `GameSupervisor` singleton manages all in-memory game state. The server enforces turn order (engine-level: X then O alternating) but has no per-user player assignment — this is the broken-server baseline by design.

**Tech Stack:** F# .NET 10, ModelContextProtocol NuGet (prerelease), Microsoft.Extensions.Hosting, TicTacToe.Engine project reference. NUnit for tests.

---

## File Structure

| Action | Path | Responsibility |
|--------|------|---------------|
| Create | `src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj` | Project file, exe |
| Create | `src/TicTacToe.Mcp/Tools.fs` | `[McpServerToolType]` class with four tools |
| Create | `src/TicTacToe.Mcp/Program.fs` | Host setup, DI wiring |
| Create | `test/TicTacToe.Mcp.Tests/TicTacToe.Mcp.Tests.fsproj` | Test project |
| Create | `test/TicTacToe.Mcp.Tests/ToolTests.fs` | NUnit tests for tool methods |

---

### Task 1: Scaffold project files

**Files:**
- Create: `src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj`
- Create: `test/TicTacToe.Mcp.Tests/TicTacToe.Mcp.Tests.fsproj`

- [ ] **Step 1: Create `src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>TicTacToe.Mcp</AssemblyName>
    <RootNamespace>TicTacToe.Mcp</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Tools.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\TicTacToe.Engine\TicTacToe.Engine.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
    <PackageReference Include="ModelContextProtocol" Version="0.3.*-*" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add the ModelContextProtocol prerelease package**

```bash
dotnet add src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj package ModelContextProtocol --prerelease
dotnet add src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj package Microsoft.Extensions.Hosting
```

- [ ] **Step 3: Create `test/TicTacToe.Mcp.Tests/TicTacToe.Mcp.Tests.fsproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="ToolTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="NUnit" Version="4.*" />
    <PackageReference Include="NUnit3TestAdapter" Version="4.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add both projects to the solution**

```bash
dotnet sln add src/TicTacToe.Mcp/TicTacToe.Mcp.fsproj
dotnet sln add test/TicTacToe.Mcp.Tests/TicTacToe.Mcp.Tests.fsproj
```

---

### Task 2: Write failing tests

**Files:**
- Create: `test/TicTacToe.Mcp.Tests/ToolTests.fs`

- [ ] **Step 1: Create placeholder `src/TicTacToe.Mcp/Tools.fs` so the test project compiles**

```fsharp
module TicTacToe.Mcp.Tools
// placeholder — filled in Task 3
```

- [ ] **Step 2: Create placeholder `src/TicTacToe.Mcp/Program.fs`**

```fsharp
module TicTacToe.Mcp.Program

[<EntryPoint>]
let main _args = 0
```

- [ ] **Step 3: Write `test/TicTacToe.Mcp.Tests/ToolTests.fs`**

```fsharp
module TicTacToe.Mcp.Tests.ToolTests

open System.Text.Json.Nodes
open NUnit.Framework
open TicTacToe.Engine
open TicTacToe.Mcp.Tools

let private makeTools () =
    GameTools(createGameSupervisor())

let private parseObj (json: string) = JsonNode.Parse(json) :?> JsonObject

[<TestFixture>]
type NewGameTests() =

    [<Test>]
    member _.``returns gameId, 9-cell board, X turn, in_progress``() =
        let tools = makeTools()
        let obj = parseObj (tools.``new_game``())
        Assert.That(obj.ContainsKey("gameId"), Is.True)
        Assert.That((obj["board"] :?> JsonArray).Count, Is.EqualTo(9))
        Assert.That(obj["whoseTurn"].GetValue<string>(), Is.EqualTo("X"))
        Assert.That(obj["status"].GetValue<string>(), Is.EqualTo("in_progress"))
        Assert.That((obj["validMoves"] :?> JsonArray).Count, Is.EqualTo(9))

    [<Test>]
    member _.``each call produces a distinct gameId``() =
        let tools = makeTools()
        let id1 = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        let id2 = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        Assert.That(id1, Is.Not.EqualTo(id2))

[<TestFixture>]
type GetBoardTests() =

    [<Test>]
    member _.``returns board for valid game``() =
        let tools = makeTools()
        let gameId = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        let obj = parseObj(tools.``get_board``(gameId))
        Assert.That(obj.ContainsKey("board"), Is.True)
        Assert.That(obj.ContainsKey("error"), Is.False)

    [<Test>]
    member _.``returns GameNotFound for unknown id``() =
        let tools = makeTools()
        let obj = parseObj(tools.``get_board``("no-such-game"))
        Assert.That(obj["error"].GetValue<string>(), Is.EqualTo("GameNotFound"))

[<TestFixture>]
type MakeMoveTests() =

    [<Test>]
    member _.``valid move updates board and alternates turn``() =
        let tools = makeTools()
        let gameId = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        let obj = parseObj(tools.``make_move``(gameId, "TopLeft"))
        Assert.That(obj.ContainsKey("error"), Is.False)
        Assert.That(obj["whoseTurn"].GetValue<string>(), Is.EqualTo("O"))

    [<Test>]
    member _.``position already taken returns PositionTaken``() =
        let tools = makeTools()
        let gameId = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        tools.``make_move``(gameId, "TopLeft") |> ignore   // X plays TopLeft
        tools.``make_move``(gameId, "TopCenter") |> ignore // O plays TopCenter
        let obj = parseObj(tools.``make_move``(gameId, "TopLeft")) // X tries TopLeft again
        Assert.That(obj["error"].GetValue<string>(), Is.EqualTo("PositionTaken"))

    [<Test>]
    member _.``invalid position name returns InvalidPosition``() =
        let tools = makeTools()
        let gameId = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        let obj = parseObj(tools.``make_move``(gameId, "NotASquare"))
        Assert.That(obj["error"].GetValue<string>(), Is.EqualTo("InvalidPosition"))

    [<Test>]
    member _.``move on finished game returns GameOver``() =
        let tools = makeTools()
        let gameId = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        // X wins: TopLeft, TopCenter, TopRight; O blocks nowhere useful
        tools.``make_move``(gameId, "TopLeft")    |> ignore // X
        tools.``make_move``(gameId, "MiddleLeft") |> ignore // O
        tools.``make_move``(gameId, "TopCenter")  |> ignore // X
        tools.``make_move``(gameId, "MiddleCenter") |> ignore // O
        tools.``make_move``(gameId, "TopRight")   |> ignore // X wins
        let obj = parseObj(tools.``make_move``(gameId, "BottomLeft"))
        Assert.That(obj["error"].GetValue<string>(), Is.EqualTo("GameOver"))

    [<Test>]
    member _.``unknown game returns GameNotFound``() =
        let tools = makeTools()
        let obj = parseObj(tools.``make_move``("no-game", "TopLeft"))
        Assert.That(obj["error"].GetValue<string>(), Is.EqualTo("GameNotFound"))

[<TestFixture>]
type GetStateTests() =

    [<Test>]
    member _.``includes gameId in response``() =
        let tools = makeTools()
        let gameId = parseObj(tools.``new_game``())["gameId"].GetValue<string>()
        let obj = parseObj(tools.``get_state``(gameId))
        Assert.That(obj["gameId"].GetValue<string>(), Is.EqualTo(gameId))
        Assert.That(obj.ContainsKey("validMoves"), Is.True)

    [<Test>]
    member _.``unknown game returns GameNotFound``() =
        let tools = makeTools()
        let obj = parseObj(tools.``get_state``("no-game"))
        Assert.That(obj["error"].GetValue<string>(), Is.EqualTo("GameNotFound"))
```

- [ ] **Step 4: Run tests — expect all to fail (Tools.fs is a placeholder)**

```bash
dotnet test test/TicTacToe.Mcp.Tests/
```

Expected: compile errors or assertion failures since Tools.fs has no implementation.

---

### Task 3: Implement Tools.fs

**Files:**
- Modify: `src/TicTacToe.Mcp/Tools.fs`

- [ ] **Step 1: Write `src/TicTacToe.Mcp/Tools.fs`**

```fsharp
module TicTacToe.Mcp.Tools

open System.ComponentModel
open System.Text.Json
open System.Text.Json.Nodes
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Model

type RejectionReason =
    | GameNotFound
    | InvalidPosition
    | OutOfTurn
    | PositionTaken
    | GameOver

let private allPositions =
    [| TopLeft; TopCenter; TopRight
       MiddleLeft; MiddleCenter; MiddleRight
       BottomLeft; BottomCenter; BottomRight |]

let private renderBoard (gs: GameState) =
    allPositions
    |> Array.map (fun pos ->
        match gs.TryGetValue(pos) with
        | true, Taken X -> "X"
        | true, Taken O -> "O"
        | _ -> "")

let private getGs = function
    | XTurn(gs, _) | OTurn(gs, _) | Won(gs, _) | Draw gs | Error(gs, _) -> gs

let private statusStr = function
    | XTurn _ | OTurn _ -> "in_progress"
    | Won _ -> "won"
    | Draw _ -> "draw"
    | Error _ -> "error"

let private whoseTurnStr = function
    | XTurn _ -> "X"
    | OTurn _ -> "O"
    | Won _ | Draw _ | Error _ -> "game_over"

let private validMovesArr = function
    | XTurn(_, moves) -> moves |> Array.map (fun (XPos p) -> p.ToString())
    | OTurn(_, moves) -> moves |> Array.map (fun (OPos p) -> p.ToString())
    | _ -> [||]

let private stateJson (result: MoveResult) =
    let gs = getGs result
    let obj = JsonObject()
    obj["board"] <- JsonNode.Parse(JsonSerializer.Serialize(renderBoard gs))
    obj["whoseTurn"] <- JsonValue.Create(whoseTurnStr result)
    obj["status"] <- JsonValue.Create(statusStr result)
    obj["validMoves"] <- JsonNode.Parse(JsonSerializer.Serialize(validMovesArr result))
    obj

let private errorJson (reason: RejectionReason) =
    let obj = JsonObject()
    obj["error"] <- JsonValue.Create(reason.ToString())
    obj.ToJsonString()

[<McpServerToolType>]
type GameTools(supervisor: GameSupervisor) =

    [<McpServerTool>]
    [<Description("Create a new tic-tac-toe game. Returns gameId, board (9 cells), whoseTurn, status, and validMoves. X always moves first.")>]
    member _.``new_game``() : string =
        let (gameId, _) = supervisor.CreateGame()
        match supervisor.GetGame(gameId) with
        | None -> errorJson GameNotFound
        | Some game ->
            let obj = stateJson (game.GetState())
            obj["gameId"] <- JsonValue.Create(gameId)
            obj.ToJsonString()

    [<McpServerTool>]
    [<Description("Get board state for a game. Returns board, whoseTurn, status, validMoves.")>]
    member _.``get_board``(
        [<Description("Game ID returned by new_game")>] gameId: string) : string =
        match supervisor.GetGame(gameId) with
        | None -> errorJson GameNotFound
        | Some game -> (stateJson (game.GetState())).ToJsonString()

    [<McpServerTool>]
    [<Description("Make a move in the current player's turn. position is one of: TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")>]
    member _.``make_move``(
        [<Description("Game ID")>] gameId: string,
        [<Description("Square to claim, e.g. TopLeft")>] position: string) : string =
        match supervisor.GetGame(gameId) with
        | None -> errorJson GameNotFound
        | Some game ->
            let currentResult = game.GetState()
            match currentResult with
            | Won _ | Draw _ -> errorJson GameOver
            | _ ->
                match SquarePosition.TryParse(position) with
                | None -> errorJson InvalidPosition
                | Some pos ->
                    let gs = getGs currentResult
                    match gs.TryGetValue(pos) with
                    | true, Taken _ -> errorJson PositionTaken
                    | _ ->
                        let move =
                            match currentResult with
                            | XTurn _ -> XMove pos
                            | OTurn _ -> OMove pos
                            | _ -> XMove pos  // unreachable: Won/Draw handled above
                        game.MakeMove(move)
                        (stateJson (game.GetState())).ToJsonString()

    [<McpServerTool>]
    [<Description("Get full game state including gameId, board, whoseTurn, status, and validMoves.")>]
    member _.``get_state``(
        [<Description("Game ID")>] gameId: string) : string =
        match supervisor.GetGame(gameId) with
        | None -> errorJson GameNotFound
        | Some game ->
            let obj = stateJson (game.GetState())
            obj["gameId"] <- JsonValue.Create(gameId)
            obj.ToJsonString()
```

---

### Task 4: Implement Program.fs

**Files:**
- Modify: `src/TicTacToe.Mcp/Program.fs`

- [ ] **Step 1: Write `src/TicTacToe.Mcp/Program.fs`**

```fsharp
module TicTacToe.Mcp.Program

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ModelContextProtocol.Server
open TicTacToe.Engine
open TicTacToe.Mcp.Tools

[<EntryPoint>]
let main _args =
    let builder = Host.CreateApplicationBuilder()
    builder.Services.AddSingleton<GameSupervisor>(fun _ -> createGameSupervisor()) |> ignore
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof<GameTools>.Assembly)
    |> ignore
    builder.Build().Run()
    0
```

---

### Task 5: Verify

- [ ] **Step 1: Run tests — expect all pass**

```bash
dotnet test test/TicTacToe.Mcp.Tests/
```

Expected: all 10 tests green.

- [ ] **Step 2: Build the server**

```bash
dotnet build src/TicTacToe.Mcp/
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Smoke test — verify tools list over stdio**

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}' | dotnet run --project src/TicTacToe.Mcp/ 2>/dev/null | head -5
```

Expected: JSON response containing `"result"` with server capabilities (not an error).

- [ ] **Step 4: Commit**

```bash
git add src/TicTacToe.Mcp/ test/TicTacToe.Mcp.Tests/ TicTacToe.sln
git commit -m "feat(mcp): add TicTacToe.Mcp stdio server for E_RPC cells"
```
