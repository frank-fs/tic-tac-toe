# SP1 — Cold-Start Discovery Harness + Grading (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A cold-start harness that drives 3 discovery agents against the *existing* Simple/Proto apps — each agent given only a URL + abstract goal, discovering the app, its role, and how to act — and grades recognize / interact / pursue per party, end-to-end, for one game.

**Architecture:** New F# console project `experiments/discovery-harness/` that **links** the existing `oss-driver` LlmClient/Types/Personas source (no duplication, `oss-driver` untouched). A cold-start `Driver` plays one seat from base-URL-only with a constant discovery instruction; an `Orchestrator` staggers 3 agents so server seat-assignment lands deterministically as X / O / observer; a pure `Grader` scores the captured transcript. Friction reuses the existing `proxy.py` / `friction.py` verbatim.

**Tech Stack:** F# / .NET 10.0, `System.Text.Json.Nodes`, `System.Net.Http` (cookie-jar = identity). Run validation against Simple (port 5328, `/arenas`) and Proto-no-JS (port 5228, `/games`, `TICTACTOE_DISABLE_JS=1`).

## Global Constraints

- **Experiment isolation:** all new code lives under `experiments/` only — never `src/`. (standing feedback)
- **Testing posture:** NO reflexive TDD. Runs are the validation. Tests ONLY for the pure grader/scoring functions in Task 4 (genuinely useful — measurement validity). (standing feedback overrides writing-plans TDD default)
- **Cold-start invariant:** the discovery instruction is CONSTANT across every future cell/arm and names NOTHING app-specific — not the game, role, paths, or move format. Only: base URL, the abstract goal, and the harness I/O protocol (how to talk back). What varies later is the *served surface*, never this prompt.
- **Worktree only:** work in `.claude/worktrees/discovery-reset` (branch `experiment/discovery-reset-spec`); `git merge --ff-only` back to `main`.
- **F#:** Holzmann — nesting ≤2, bound loops, ≤60-line functions, no module-level mutable, `ILogger`/explicit errors, one indirection layer.
- **Build/run from repo root:** `dotnet build experiments/discovery-harness`.

## Ground facts (from the existing apps — do not re-derive)

- **Simple:** `experiments/src/TicTacToe.Web.Simple`, port **5328**, route **`/arenas`**, cookie `TicTacToe.SimpleUser`. `Accept: application/json` on `GET /arenas/{id}` → `{id, board[9], status, whoseTurn}`. Move: `POST /arenas/{id}` body `player=X&position=<Name>` → 200/303 ok, ≥400 reject.
- **Proto-no-JS:** `src/TicTacToe.Web`, port **5228**, route **`/games`**, cookie `TicTacToe.User`, env `TICTACTOE_DISABLE_JS=1`. Move `POST /games/{id}` → 202 ok.
- **Identity:** cookie auto-login; one `HttpClient` w/ `CookieContainer` per agent = one stable seat (no jar coaching needed — the driver owns identity).
- **Seat assignment (`PlayerAssignmentManager`, every move):** X-slot-open + X-turn → assign X; O-slot-open + O-turn + not-X → assign O; both filled + neither → `Rejected NotAPlayer`; wrong turn → `Rejected NotYourTurn`.
- **Positions (9):** `TopLeft TopCenter TopRight MiddleLeft MiddleCenter MiddleRight BottomLeft BottomCenter BottomRight`.
- **Harness scripts (reuse as-is):** `experiments/haiku-subagents/arena.sh` (`up|down|status <proto|simple>`, starts server+proxy, prints GAME_ID/URL), `proxy.py` (JSONL `{ts,method,path,status}`), `friction.py` (`proxy <log>` → read:write/rejections).

## File Structure

```
experiments/discovery-harness/
  TicTacToe.DiscoveryHarness.fsproj   # links ../oss-driver/{Types,LlmClient,Personas}.fs
  ColdStart.fs      # constant discovery instruction + cold-start system prompt builder
  Transcript.fs     # per-party transcript model (requests, responses, discovery report, board states)
  Driver.fs         # one cold-start seat: base-URL-only ReAct, emits discovery report, records transcript
  Optimal.fs        # pure tic-tac-toe minimax: board value + per-move value (blunder detection)
  Grader.fs         # pure: recognize / interact / pursue-completion / pursue-quality from a Transcript
  Orchestrator.fs   # stagger 3 agents → deterministic X/O/observer; aggregate; write results JSON
  Program.fs        # CLI entry: --arm simple|proto --base <proxy-url> --route arenas|games --out <path>
experiments/discovery-harness/test/
  TicTacToe.DiscoveryHarness.Tests.fsproj
  OptimalTests.fs   # minimax correctness (the load-bearing scorer)
  GraderTests.fs    # recognize/role-discrimination/blunder grading on hand-built transcripts
experiments/discovery-harness/run.sh   # arena up → orchestrator → friction → arena down
```

---

### Task 1: Project scaffold + cold-start instruction

**Files:**
- Create: `experiments/discovery-harness/TicTacToe.DiscoveryHarness.fsproj`
- Create: `experiments/discovery-harness/ColdStart.fs`

**Interfaces:**
- Consumes: `TicTacToe.OssDriver.Types` (`Backend`), `TicTacToe.OssDriver.LlmClient` (`chat`, `defaultModel`), `TicTacToe.OssDriver.Personas` (`Persona`, `get`) — via linked source.
- Produces: `ColdStart.discoveryInstruction : string` (the constant), `ColdStart.systemPrompt : baseUrl:string -> persona:Persona -> string`.

- [ ] **Step 1: Create the project file linking oss-driver source**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <AssemblyName>TicTacToe.DiscoveryHarness</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../oss-driver/Types.fs" />
    <Compile Include="../oss-driver/Personas.fs" />
    <Compile Include="../oss-driver/LlmClient.fs" />
    <Compile Include="ColdStart.fs" />
    <Compile Include="Transcript.fs" />
    <Compile Include="Optimal.fs" />
    <Compile Include="Grader.fs" />
    <Compile Include="Driver.fs" />
    <Compile Include="Orchestrator.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `ColdStart.fs` — the constant discovery instruction**

The instruction names nothing app-specific. It states the abstract goal + the harness I/O protocol (one action per reply; the two-moment discovery report). This text is FROZEN once approved — later cells vary only the served surface.

```fsharp
module TicTacToe.DiscoveryHarness.ColdStart

open TicTacToe.OssDriver.Types

// FROZEN cold-start contract. Names no app specifics (no game, role, path, or move
// format). Identity is owned by the driver's cookie jar — not coached here. Only the
// abstract goal + how to talk back to the harness.
let discoveryInstruction =
    "You are an autonomous agent. You are given ONLY a base URL. Nothing about what \
     lives there has been described to you. Your job: review the application at that \
     URL and interact with it to accomplish whatever goal it presents.\n\n\
     Work in two stages.\n\n\
     STAGE 1 — DISCOVER (read-only). Issue read requests to learn what this is. When \
     you can, reply with EXACTLY one line of JSON and nothing else:\n\
     DISCOVERY {\"appIs\":\"...\",\"goal\":\"...\",\"isMultiplayer\":true|false,\"howToParticipate\":\"...\"}\n\n\
     STAGE 2 — PARTICIPATE. Take part and pursue the goal. The system that assigns you \
     a part may accept or refuse your attempts; learn your part from how it responds. \
     Once you know it, reply with one line of JSON and nothing else:\n\
     ROLE {\"myRole\":\"...\",\"myAffordances\":\"...\",\"canIAct\":true|false}\n\n\
     ACTIONS. Every other reply is EXACTLY one HTTP request, one line, nothing else:\n\
     \  GET /path\n\
     or\n\
     \  POST /path key=value&key2=value2\n\n\
     I run the request and return its status + body. Pace yourself; it is not a race. \
     If the server goes quiet after activity, the task has likely ended — stop."

let systemPrompt (baseUrl: string) (persona: Persona) : string =
    sprintf "%s\n\nBase URL: %s\n\nHow well to pursue the goal: %s\n\nBegin Stage 1."
        discoveryInstruction baseUrl persona.Guidance
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build experiments/discovery-harness`
Expected: build succeeds; linked oss-driver modules resolve.

- [ ] **Step 4: Commit**

```bash
git add experiments/discovery-harness/TicTacToe.DiscoveryHarness.fsproj experiments/discovery-harness/ColdStart.fs
git commit -m "feat(discovery-harness): scaffold + frozen cold-start instruction"
```

---

### Task 2: Transcript model

**Files:**
- Create: `experiments/discovery-harness/Transcript.fs`

**Interfaces:**
- Produces:
  - `type ReqRecord = { Method: string; Path: string; Body: string option; Status: int; BodySnippet: string }`
  - `type DiscoveryReport = { AppIs: string; Goal: string; IsMultiplayer: bool option; HowToParticipate: string }`
  - `type RoleReport = { MyRole: string; MyAffordances: string; CanIAct: bool option }`
  - `type BoardSnapshot = { AfterRequestIndex: int; Cells: string[]; Status: string; WhoseTurn: string }` (parsed from Simple JSON when available; empty arrays when not)
  - `type Transcript = { Seat: string; Persona: string; Model: string; Requests: ResizeArray<ReqRecord>; mutable Discovery: DiscoveryReport option; mutable Role: RoleReport option; Boards: ResizeArray<BoardSnapshot>; mutable Outcome: string; mutable Tokens: int; mutable Actions: int; mutable MovesSubmitted: int }`
  - `Transcript.empty : seat:string -> persona:string -> model:string -> Transcript`
  - `Transcript.tryParseDiscovery : line:string -> DiscoveryReport option` (matches `DISCOVERY { ... }`)
  - `Transcript.tryParseRole : line:string -> RoleReport option` (matches `ROLE { ... }`)
  - `Transcript.tryParseBoard : afterIndex:int -> body:string -> BoardSnapshot option` (parses Simple `{board,status,whoseTurn}` JSON; None on non-JSON)

- [ ] **Step 1: Write `Transcript.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Transcript

open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

type ReqRecord = { Method: string; Path: string; Body: string option; Status: int; BodySnippet: string }
type DiscoveryReport = { AppIs: string; Goal: string; IsMultiplayer: bool option; HowToParticipate: string }
type RoleReport = { MyRole: string; MyAffordances: string; CanIAct: bool option }
type BoardSnapshot = { AfterRequestIndex: int; Cells: string[]; Status: string; WhoseTurn: string }

type Transcript =
    { Seat: string
      Persona: string
      Model: string
      Requests: ResizeArray<ReqRecord>
      mutable Discovery: DiscoveryReport option
      mutable Role: RoleReport option
      Boards: ResizeArray<BoardSnapshot>
      mutable Outcome: string
      mutable Tokens: int
      mutable Actions: int
      mutable MovesSubmitted: int }

let empty seat persona model =
    { Seat = seat; Persona = persona; Model = model
      Requests = ResizeArray(); Discovery = None; Role = None; Boards = ResizeArray()
      Outcome = "incomplete"; Tokens = 0; Actions = 0; MovesSubmitted = 0 }

let private str (o: JsonObject) (k: string) =
    match o.TryGetPropertyValue k with
    | true, v when v <> null -> v.GetValue<string>()
    | _ -> ""

let private boolOpt (o: JsonObject) (k: string) =
    match o.TryGetPropertyValue k with
    | true, v when v <> null -> (try Some(v.GetValue<bool>()) with _ -> None)
    | _ -> None

let private extractJson (prefix: string) (line: string) : JsonObject option =
    let m = Regex.Match(line, prefix + @"\s*(\{.*\})")
    if not m.Success then None
    else try Some(JsonNode.Parse(m.Groups.[1].Value) :?> JsonObject) with _ -> None

let tryParseDiscovery (line: string) : DiscoveryReport option =
    extractJson "DISCOVERY" line
    |> Option.map (fun o ->
        { AppIs = str o "appIs"; Goal = str o "goal"
          IsMultiplayer = boolOpt o "isMultiplayer"; HowToParticipate = str o "howToParticipate" })

let tryParseRole (line: string) : RoleReport option =
    extractJson "ROLE" line
    |> Option.map (fun o ->
        { MyRole = str o "myRole"; MyAffordances = str o "myAffordances"; CanIAct = boolOpt o "canIAct" })

let tryParseBoard (afterIndex: int) (body: string) : BoardSnapshot option =
    try
        let o = JsonNode.Parse body :?> JsonObject
        match o.TryGetPropertyValue "board" with
        | true, (:? JsonArray as arr) ->
            let cells = arr |> Seq.map (fun n -> if n = null then "" else n.GetValue<string>()) |> Seq.toArray
            Some { AfterRequestIndex = afterIndex; Cells = cells; Status = str o "status"; WhoseTurn = str o "whoseTurn" }
        | _ -> None
    with _ -> None
```

- [ ] **Step 2: Build**

Run: `dotnet build experiments/discovery-harness`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add experiments/discovery-harness/Transcript.fs
git commit -m "feat(discovery-harness): transcript model + report/board parsers"
```

---

### Task 3: Optimal-play scorer (pure, tested)

**Files:**
- Create: `experiments/discovery-harness/Optimal.fs`
- Create: `experiments/discovery-harness/test/TicTacToe.DiscoveryHarness.Tests.fsproj`
- Create: `experiments/discovery-harness/test/OptimalTests.fs`

**Interfaces:**
- Produces:
  - `Optimal.positions : string[]` (the 9 names, index 0..8)
  - `Optimal.winner : cells:string[] -> string` (`"X"|"O"|""`)
  - `Optimal.value : cells:string[] -> toMove:string -> int` (minimax: +1 win for `toMove`'s side perspective normalized to X=+1/O=-1/draw=0)
  - `Optimal.bestValueForMover : cells:string[] -> mover:string -> int` (value the mover can guarantee)
  - `Optimal.isBlunder : cells:string[] -> mover:string -> chosenIndex:int -> bool` (chosen move's resulting guaranteed value < best available for mover)

This is the load-bearing measurement function → it gets real tests. Tic-tac-toe state space is tiny (≤9! bounded) so exhaustive minimax is fine (Holzmann R10: recursion bounded by empty-cell count, ≤9).

- [ ] **Step 1: Write the failing tests**

```fsharp
module TicTacToe.DiscoveryHarness.OptimalTests

open Xunit
open TicTacToe.DiscoveryHarness

let private board (s: string) = s.ToCharArray() |> Array.map (fun c -> if c = '.' then "" else string c)

[<Fact>]
let ``winner detects a row`` () =
    Assert.Equal("X", Optimal.winner (board "XXX...OO."))

[<Fact>]
let ``no winner on empty board`` () =
    Assert.Equal("", Optimal.winner (board "........."))

[<Fact>]
let ``taking the immediate win is not a blunder`` () =
    // X to move: X at 0,1 ; playing 2 wins.
    let b = board "XX..O.O.."
    Assert.False(Optimal.isBlunder b "X" 2)

[<Fact>]
let ``missing an immediate win is a blunder`` () =
    let b = board "XX..O.O.."
    Assert.True(Optimal.isBlunder b "X" 3)

[<Fact>]
let ``failing to block a loss is a blunder`` () =
    // O to move: X threatens 0,1->2. Not blocking at 2 loses.
    let b = board "XX..O...."
    Assert.True(Optimal.isBlunder b "O" 5)
    Assert.False(Optimal.isBlunder b "O" 2)
```

- [ ] **Step 2: Create the test project and run to verify failure**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../Optimal.fs" />
    <Compile Include="OptimalTests.fs" />
    <Compile Include="GraderTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
  </ItemGroup>
</Project>
```

Note: `GraderTests.fs` is created in Task 4 — to run Optimal tests alone before then, temporarily comment its `<Compile>` line, or create an empty `module TicTacToe.DiscoveryHarness.GraderTests` stub now and fill it in Task 4.

Run: `dotnet test experiments/discovery-harness/test`
Expected: FAIL — `Optimal` not defined.

- [ ] **Step 3: Write `Optimal.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Optimal

let positions =
    [| "TopLeft"; "TopCenter"; "TopRight"
       "MiddleLeft"; "MiddleCenter"; "MiddleRight"
       "BottomLeft"; "BottomCenter"; "BottomRight" |]

let private lines =
    [| (0,1,2);(3,4,5);(6,7,8);(0,3,6);(1,4,7);(2,5,8);(0,4,8);(2,4,6) |]

let winner (cells: string[]) : string =
    lines
    |> Array.tryPick (fun (a,b,c) ->
        if cells.[a] <> "" && cells.[a] = cells.[b] && cells.[b] = cells.[c] then Some cells.[a] else None)
    |> Option.defaultValue ""

let private other = function "X" -> "O" | _ -> "X"
let private score = function "X" -> 1 | "O" -> -1 | _ -> 0

// Minimax to absolute value (X=+1, O=-1, draw=0). Recursion bounded by empty cells (≤9).
let rec private minimax (cells: string[]) (toMove: string) : int =
    let w = winner cells
    if w <> "" then score w
    else
        let empties = [ for i in 0..8 do if cells.[i] = "" then yield i ]
        if List.isEmpty empties then 0
        else
            let vals =
                empties |> List.map (fun i ->
                    let next = Array.copy cells
                    next.[i] <- toMove
                    minimax next (other toMove))
            if toMove = "X" then List.max vals else List.min vals

let value (cells: string[]) (toMove: string) : int = minimax cells toMove

// Value the mover can guarantee from this position (before moving).
let bestValueForMover (cells: string[]) (mover: string) : int =
    let empties = [ for i in 0..8 do if cells.[i] = "" then yield i ]
    let vals =
        empties |> List.map (fun i ->
            let next = Array.copy cells
            next.[i] <- mover
            minimax next (other mover))
    if List.isEmpty vals then 0
    elif mover = "X" then List.max vals else List.min vals

let isBlunder (cells: string[]) (mover: string) (chosenIndex: int) : bool =
    if chosenIndex < 0 || chosenIndex > 8 || cells.[chosenIndex] <> "" then false
    else
        let next = Array.copy cells
        next.[chosenIndex] <- mover
        let chosenVal = minimax next (other mover)
        let best = bestValueForMover cells mover
        // For X higher is better; for O lower is better.
        if mover = "X" then chosenVal < best else chosenVal > best
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test experiments/discovery-harness/test`
Expected: PASS (all Optimal tests green).

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Optimal.fs experiments/discovery-harness/test/
git commit -m "feat(discovery-harness): tested tic-tac-toe minimax blunder scorer"
```

---

### Task 4: Grader (pure, tested)

**Files:**
- Create: `experiments/discovery-harness/Grader.fs`
- Create: `experiments/discovery-harness/test/GraderTests.fs`

**Interfaces:**
- Consumes: `Transcript.*`, `Optimal.*`.
- Produces:
  - `type GroundTruth = { AppIs: string[]; Goal: string[]; IsMultiplayer: bool }` (keyword sets the report must hit)
  - `Grader.ticTacToeTruth : GroundTruth` (the authored truth for this app)
  - `type RecognizeScore = { AppIsHit: bool; GoalHit: bool; MultiplayerHit: bool; RoleNamed: bool; RoleDiscriminationCorrect: bool; FirstActionCoherent: bool }`
  - `type Scores = { Recognize: RecognizeScore; AcceptedMoves: int; RejectedMoves: int; RejectionCodes: string list; Outcome: string; MovesToTerminal: int; Blunders: int; MovesScored: int; Actions: int; Tokens: int }`
  - `Grader.grade : Transcript -> Scores`

Grading rules:
- **AppIsHit / GoalHit:** discovery report text contains ANY truth keyword (case-insensitive) for that field.
- **MultiplayerHit:** report `IsMultiplayer = Some truth.IsMultiplayer`.
- **RoleNamed:** role report present and `myRole` ∈ {x, o, observer/spectator/watcher} (case-insensitive substring).
- **RoleDiscriminationCorrect:** observer reports `canIAct = Some false`; X/O report `canIAct = Some true`.
- **FirstActionCoherent:** the first POST in the transcript targets the discovered participate path (a move), NOT a guessed/unrelated path; coherent if the first POST's status is not 404 and the path matches the resource the GETs already touched.
- **AcceptedMoves / RejectedMoves / RejectionCodes:** POSTs with status <400 vs ≥400; codes are the rejection reason tokens found in the response snippet (`NotYourTurn`, `NotAPlayer`, or the HTTP status).
- **Blunders / MovesScored:** replay this seat's accepted moves against the `Boards` snapshots; count `Optimal.isBlunder` for each move where a board snapshot precedes it. (If no JSON boards available — Proto — `MovesScored = 0`; quality deferred to a JSON-capable surface.)

- [ ] **Step 1: Write the failing grader tests**

```fsharp
module TicTacToe.DiscoveryHarness.GraderTests

open Xunit
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

let private withReports seat (disc: DiscoveryReport option) (role: RoleReport option) =
    let t = Transcript.empty seat "expert" "test"
    t.Discovery <- disc
    t.Role <- role
    t

[<Fact>]
let ``recognizes tic-tac-toe from keyword hit`` () =
    let d = Some { AppIs = "a tic-tac-toe game"; Goal = "get three in a row and win"
                   IsMultiplayer = Some true; HowToParticipate = "POST a move" }
    let s = Grader.grade (withReports "X" d (Some { MyRole = "X"; MyAffordances = "move"; CanIAct = Some true }))
    Assert.True(s.Recognize.AppIsHit)
    Assert.True(s.Recognize.GoalHit)
    Assert.True(s.Recognize.MultiplayerHit)

[<Fact>]
let ``observer who says it cannot act scores role-discrimination`` () =
    let r = Some { MyRole = "observer"; MyAffordances = "watch only"; CanIAct = Some false }
    let s = Grader.grade (withReports "observer" None r)
    Assert.True(s.Recognize.RoleNamed)
    Assert.True(s.Recognize.RoleDiscriminationCorrect)

[<Fact>]
let ``observer who claims it can act fails role-discrimination`` () =
    let r = Some { MyRole = "observer"; MyAffordances = "watch"; CanIAct = Some true }
    let s = Grader.grade (withReports "observer" None r)
    Assert.False(s.Recognize.RoleDiscriminationCorrect)

[<Fact>]
let ``rejected NotAPlayer move is tallied with its code`` () =
    let t = Transcript.empty "observer" "observer" "test"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g1"; Body = Some "player=X&position=TopLeft"
                     Status = 403; BodySnippet = "Rejected NotAPlayer" }
    let s = Grader.grade t
    Assert.Equal(0, s.AcceptedMoves)
    Assert.Equal(1, s.RejectedMoves)
    Assert.Contains("NotAPlayer", s.RejectionCodes)
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test experiments/discovery-harness/test`
Expected: FAIL — `Grader` not defined.

- [ ] **Step 3: Write `Grader.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Grader

open TicTacToe.DiscoveryHarness.Transcript

type GroundTruth = { AppIs: string[]; Goal: string[]; IsMultiplayer: bool }

let ticTacToeTruth =
    { AppIs = [| "tic-tac-toe"; "tic tac toe"; "noughts"; "tictactoe" |]
      Goal = [| "three in a row"; "3 in a row"; "win"; "row, column"; "line" |]
      IsMultiplayer = true }

type RecognizeScore =
    { AppIsHit: bool; GoalHit: bool; MultiplayerHit: bool
      RoleNamed: bool; RoleDiscriminationCorrect: bool; FirstActionCoherent: bool }

type Scores =
    { Recognize: RecognizeScore
      AcceptedMoves: int; RejectedMoves: int; RejectionCodes: string list
      Outcome: string; MovesToTerminal: int
      Blunders: int; MovesScored: int; Actions: int; Tokens: int }

let private hits (kws: string[]) (text: string) =
    let low = text.ToLowerInvariant()
    kws |> Array.exists (fun k -> low.Contains(k.ToLowerInvariant()))

let private roleNamed (r: RoleReport) =
    [ "x"; "o"; "observer"; "spectator"; "watcher" ]
    |> List.exists (fun k -> r.MyRole.ToLowerInvariant().Contains k)

let private isObserverSeat (seat: string) = seat.ToLowerInvariant().Contains "observ"

let private codeOf (snippet: string) (status: int) =
    [ "NotYourTurn"; "NotAPlayer" ]
    |> List.tryFind snippet.Contains
    |> Option.defaultValue (string status)

let private recognize (t: Transcript) : RecognizeScore =
    let appIsHit, goalHit, mpHit =
        match t.Discovery with
        | Some d -> hits ticTacToeTruth.AppIs d.AppIs,
                    hits ticTacToeTruth.Goal d.Goal,
                    d.IsMultiplayer = Some ticTacToeTruth.IsMultiplayer
        | None -> false, false, false
    let named, discrim =
        match t.Role with
        | Some r ->
            let expectedAct = not (isObserverSeat t.Seat)
            roleNamed r, (r.CanIAct = Some expectedAct)
        | None -> false, false
    let firstActionCoherent =
        match t.Requests |> Seq.tryFind (fun r -> r.Method = "POST") with
        | Some p -> p.Status <> 404
        | None -> false
    { AppIsHit = appIsHit; GoalHit = goalHit; MultiplayerHit = mpHit
      RoleNamed = named; RoleDiscriminationCorrect = discrim; FirstActionCoherent = firstActionCoherent }

// Replay accepted moves against the latest board snapshot preceding each, count blunders.
let private quality (t: Transcript) : int * int =
    let mutable blunders = 0
    let mutable scored = 0
    for idx in 0 .. t.Requests.Count - 1 do
        let r = t.Requests.[idx]
        if r.Method = "POST" && r.Status < 400 then
            let priorBoard =
                t.Boards |> Seq.filter (fun b -> b.AfterRequestIndex < idx)
                         |> Seq.sortByDescending (fun b -> b.AfterRequestIndex) |> Seq.tryHead
            let posName =
                r.Body |> Option.bind (fun b ->
                    b.Split('&') |> Array.tryPick (fun kv ->
                        let p = kv.Split('=') in if p.Length = 2 && p.[0] = "position" then Some p.[1] else None))
            match priorBoard, posName with
            | Some board, Some name when board.Cells.Length = 9 ->
                let mover = if t.Seat = "X" || t.Seat = "O" then t.Seat else ""
                let chosen = System.Array.IndexOf(Optimal.positions, name)
                if mover <> "" && chosen >= 0 then
                    scored <- scored + 1
                    if Optimal.isBlunder board.Cells mover chosen then blunders <- blunders + 1
            | _ -> ()
    blunders, scored

let grade (t: Transcript) : Scores =
    let posts = t.Requests |> Seq.filter (fun r -> r.Method = "POST") |> Seq.toList
    let accepted = posts |> List.filter (fun r -> r.Status < 400)
    let rejected = posts |> List.filter (fun r -> r.Status >= 400)
    let codes = rejected |> List.map (fun r -> codeOf r.BodySnippet r.Status) |> List.distinct
    let blunders, scored = quality t
    { Recognize = recognize t
      AcceptedMoves = List.length accepted
      RejectedMoves = List.length rejected
      RejectionCodes = codes
      Outcome = t.Outcome
      MovesToTerminal = List.length accepted
      Blunders = blunders; MovesScored = scored
      Actions = t.Actions; Tokens = t.Tokens }
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test experiments/discovery-harness/test`
Expected: PASS (Optimal + Grader green).

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Grader.fs experiments/discovery-harness/test/GraderTests.fs
git commit -m "feat(discovery-harness): tested grader (recognize/interact/quality)"
```

---

### Task 5: Cold-start Driver (one seat, base-URL-only)

**Files:**
- Create: `experiments/discovery-harness/Driver.fs`

**Interfaces:**
- Consumes: `ColdStart.systemPrompt`, `Transcript.*`, `LlmClient.chat`, `Personas.Persona`, `Types.Backend`.
- Produces:
  - `type SeatConfig = { Backend: Backend; Model: string; Seat: string; Persona: Persona; Base: string; MaxActions: int; MaxMoves: int; Window: int; PollSeconds: float; StartGate: System.Threading.ManualResetEventSlim option; SeatedSignal: (unit -> unit) option }`
  - `Driver.runSeat : SeatConfig -> Transcript` — plays one seat from base URL only, records the transcript, fires `SeatedSignal` after its first accepted POST (for orchestrator staggering), waits on `StartGate` before its first POST if provided.

Key differences from `oss-driver/Driver.fs`: NO `seed`/path pre-resolution (agent starts knowing only `Base`), NO role in the prompt, parses `DISCOVERY`/`ROLE` report lines into the transcript, records every request, captures Simple JSON boards, and supports staggering hooks.

- [ ] **Step 1: Write `Driver.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Driver

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading
open TicTacToe.OssDriver.Types
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

type SeatConfig =
    { Backend: Backend; Model: string; Seat: string; Persona: Persona
      Base: string; MaxActions: int; MaxMoves: int; Window: int; PollSeconds: float
      StartGate: ManualResetEventSlim option
      SeatedSignal: (unit -> unit) option }

let private newClient () =
    let h = new HttpClientHandler(AllowAutoRedirect = false, UseCookies = true, CookieContainer = CookieContainer())
    new HttpClient(h, Timeout = TimeSpan.FromSeconds 30.0)

let private actionRe = Regex(@"\b(GET|POST)\s+(/\S+)(?:\s+(.*\S))?", RegexOptions.IgnoreCase)

let parseAction (text: string) : (string * string * string option) option =
    let m = actionRe.Match(text.Replace("`", " "))
    if not m.Success then None
    else
        let body = if m.Groups.[3].Success && m.Groups.[3].Value.Trim() <> "" then Some(m.Groups.[3].Value.Trim()) else None
        Some(m.Groups.[1].Value.ToUpperInvariant(), m.Groups.[2].Value, body)

let private terminalTokens =
    [ "\"status\":\"xwins\""; "\"status\":\"owins\""; "\"status\":\"draw\""
      "data-game-status=\"won\""; "data-game-status=\"draw\""; " wins"; "x won"; "o won" ]

let private terminalOutcome (status: int) (body: string) : string option =
    if status = 404 then Some "ended"
    else
        let low = body.ToLowerInvariant()
        if terminalTokens |> List.exists low.Contains then Some "over" else None

let private send (client: HttpClient) (baseUrl: string) (m: string) (path: string) (body: string option) : int * string =
    let url = baseUrl.TrimEnd('/') + path
    use req = new HttpRequestMessage(HttpMethod(m), url)
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/x-www-form-urlencoded"))
    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/html") |> ignore
    try use resp = client.Send req in int resp.StatusCode, resp.Content.ReadAsStringAsync().Result
    with e -> 0, sprintf "<request error: %s>" e.Message

let private window (messages: ResizeArray<string * string>) (n: int) : (string * string) list =
    let sys = messages.[0]
    let rest = messages |> Seq.skip 1 |> Seq.toList
    let tail = if List.length rest > n then rest |> List.skip (List.length rest - n) else rest
    sys :: tail

let private debug (seat: string) (fmt: Printf.StringFormat<'a, unit>) =
    if not (isNull (Environment.GetEnvironmentVariable "HARNESS_DEBUG")) then eprintf "[%s] " seat
    Printf.eprintfn fmt

// Capture report lines from an assistant reply into the transcript.
let private captureReports (t: Transcript) (reply: string) =
    if t.Discovery.IsNone then Transcript.tryParseDiscovery reply |> Option.iter (fun d -> t.Discovery <- Some d)
    if t.Role.IsNone then Transcript.tryParseRole reply |> Option.iter (fun r -> t.Role <- Some r)

let runSeat (cfg: SeatConfig) : Transcript =
    use client = newClient ()
    let t = Transcript.empty cfg.Seat cfg.Persona.Name cfg.Model
    let messages = ResizeArray<string * string>()
    messages.Add("system", ColdStart.systemPrompt cfg.Base cfg.Persona)
    messages.Add("user", "Begin Stage 1: explore the base URL to learn what this is.")
    let mutable firstSeated = false
    let mutable stop = false
    while not stop && t.Actions < cfg.MaxActions do
        let reply =
            try LlmClient.chat cfg.Backend cfg.Model (window messages cfg.Window)
            with e -> sprintf "<chat error: %s>" e.Message
        messages.Add("assistant", reply)
        captureReports t reply
        match parseAction reply with
        | None ->
            messages.Add("user", "Reply with exactly one line: a DISCOVERY/ROLE JSON report, or GET <path>, or POST <path> <body>.")
        | Some(m, path, body) ->
            // Stagger: gate the first POST until the orchestrator opens the seat order.
            if m = "POST" && not firstSeated then cfg.StartGate |> Option.iter (fun g -> g.Wait())
            let status, text = send client cfg.Base m path body
            let reqIndex = t.Requests.Count
            t.Requests.Add { Method = m; Path = path; Body = body; Status = status
                             BodySnippet = (if text.Length <= 300 then text else text.[..299]) }
            Transcript.tryParseBoard reqIndex text |> Option.iter t.Boards.Add
            debug cfg.Seat "%s %s -> %d" m path status
            if m = "POST" then
                t.MovesSubmitted <- t.MovesSubmitted + 1
                if status < 400 && not firstSeated then
                    firstSeated <- true
                    cfg.SeatedSignal |> Option.iter (fun f -> f())
            let obs = if text.Length <= 4000 then text else text.[..3999] + " …[truncated]"
            messages.Add("user", sprintf "HTTP %d\n%s" status obs)
            match terminalOutcome status text with
            | Some o -> t.Outcome <- o; stop <- true
            | None ->
                if t.MovesSubmitted >= cfg.MaxMoves then t.Outcome <- "move_cap"; stop <- true
                elif m = "GET" then Thread.Sleep(int (cfg.PollSeconds * 1000.0))
        t.Actions <- t.Actions + 1
    t
```

- [ ] **Step 2: Build**

Run: `dotnet build experiments/discovery-harness`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add experiments/discovery-harness/Driver.fs
git commit -m "feat(discovery-harness): cold-start single-seat driver"
```

---

### Task 6: Orchestrator (stagger 3 agents → deterministic X/O/observer)

**Files:**
- Create: `experiments/discovery-harness/Orchestrator.fs`

**Interfaces:**
- Consumes: `Driver.runSeat`, `Driver.SeatConfig`, `Grader.grade`, `Transcript.Transcript`.
- Produces:
  - `type RunConfig = { Backend: Backend; Model: string; Persona: Persona; Base: string; MaxActions: int; MaxMoves: int; Window: int; PollSeconds: float }`
  - `Orchestrator.runGame : RunConfig -> Transcript list` — launches 3 seats with staggered gates: seat A unrestricted, seat B's first POST gated until A is seated, seat C's first POST gated until B is seated (→ C is forced to spectator). Returns the 3 transcripts (labelled `X`/`O`/`observer` by realized seating, re-derived from each transcript's accepted-move success + role report).
  - `Orchestrator.resultsJson : RunConfig -> Transcript list -> string` — emits `{arm, model, persona, parties:[{seat, scores...}]}` via `Grader.grade`.

Staggering: two `ManualResetEventSlim` gates. A has no gate + signals gateB on seated. B waits gateB + signals gateC on seated. C waits gateC (only opens after both seats are taken, so C's POST is `NotAPlayer`). Bound the wait so a stalled seat can't hang the run (R10): gates use a timeout; on timeout the dependent seat proceeds anyway and its seat is whatever the server gives.

- [ ] **Step 1: Write `Orchestrator.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Orchestrator

open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open TicTacToe.OssDriver.Types
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

type RunConfig =
    { Backend: Backend; Model: string; Persona: Persona; Base: string
      MaxActions: int; MaxMoves: int; Window: int; PollSeconds: float }

[<Literal>]
let private gateTimeoutMs = 120000  // bound: a stalled seat must not hang the game

let private seatCfg (rc: RunConfig) (label: string) (gate: ManualResetEventSlim option) (signal: (unit -> unit) option) : Driver.SeatConfig =
    { Backend = rc.Backend; Model = rc.Model; Seat = label; Persona = rc.Persona
      Base = rc.Base; MaxActions = rc.MaxActions; MaxMoves = rc.MaxMoves
      Window = rc.Window; PollSeconds = rc.PollSeconds
      StartGate = gate; SeatedSignal = signal }

let runGame (rc: RunConfig) : Transcript list =
    use gateB = new ManualResetEventSlim(false)
    use gateC = new ManualResetEventSlim(false)
    let openWith (g: ManualResetEventSlim) = fun () -> g.Set()
    let waitBounded (g: ManualResetEventSlim) = Some g  // Driver.Wait is unbounded; see note
    // Seat A: no gate, opens B when seated. Seat B: waits B, opens C. Seat C: waits C (=> spectator).
    let cfgA = seatCfg rc "seatA" None (Some(openWith gateB))
    let cfgB = seatCfg rc "seatB" (Some gateB) (Some(openWith gateC))
    let cfgC = seatCfg rc "seatC" (Some gateC) None
    // Safety: auto-open gates after the bound so nothing hangs.
    let armTimeout (g: ManualResetEventSlim) =
        Task.Run(fun () -> Thread.Sleep gateTimeoutMs; g.Set())
    armTimeout gateB |> ignore
    armTimeout gateC |> ignore
    [ cfgA; cfgB; cfgC ]
    |> List.map (fun c -> Task.Run(fun () -> Driver.runSeat c))
    |> List.map (fun task -> task.Result)

// Realized seat from a transcript: a role report wins; else infer from accepted moves.
let private realizedSeat (t: Transcript) : string =
    match t.Role with
    | Some r when r.MyRole.ToLowerInvariant().Contains "x" -> "X"
    | Some r when r.MyRole.ToLowerInvariant().Contains "o" -> "O"
    | Some r when (let m = r.MyRole.ToLowerInvariant() in m.Contains "observ" || m.Contains "spectat" || m.Contains "watch") -> "observer"
    | _ ->
        let accepted = t.Requests |> Seq.exists (fun r -> r.Method = "POST" && r.Status < 400)
        if accepted then "player" else "observer"

let resultsJson (rc: RunConfig) (transcripts: Transcript list) : string =
    let parties = JsonArray()
    for t in transcripts do
        // Re-stamp the transcript's seat with the realized seat for correct grading.
        let realized = realizedSeat t
        let graded = Grader.grade { t with Seat = realized }
        let p = JsonObject()
        p["seat"] <- JsonValue.Create realized
        p["recognize_appIs"] <- JsonValue.Create graded.Recognize.AppIsHit
        p["recognize_goal"] <- JsonValue.Create graded.Recognize.GoalHit
        p["recognize_multiplayer"] <- JsonValue.Create graded.Recognize.MultiplayerHit
        p["role_named"] <- JsonValue.Create graded.Recognize.RoleNamed
        p["role_discrimination"] <- JsonValue.Create graded.Recognize.RoleDiscriminationCorrect
        p["first_action_coherent"] <- JsonValue.Create graded.Recognize.FirstActionCoherent
        p["accepted_moves"] <- JsonValue.Create graded.AcceptedMoves
        p["rejected_moves"] <- JsonValue.Create graded.RejectedMoves
        p["rejection_codes"] <- JsonValue.Create (String.concat "," graded.RejectionCodes)
        p["outcome"] <- JsonValue.Create graded.Outcome
        p["blunders"] <- JsonValue.Create graded.Blunders
        p["moves_scored"] <- JsonValue.Create graded.MovesScored
        p["actions"] <- JsonValue.Create graded.Actions
        parties.Add p
    let root = JsonObject()
    root["model"] <- JsonValue.Create rc.Model
    root["persona"] <- JsonValue.Create rc.Persona.Name
    root["base"] <- JsonValue.Create rc.Base
    root["parties"] <- parties
    root.ToJsonString()
```

> Note for implementer: `Driver.SeatConfig.StartGate` uses `g.Wait()` (unbounded). The orchestrator arms a timeout task that `Set()`s each gate after `gateTimeoutMs`, so the unbounded wait is bounded in practice. If you prefer the bound inside the driver, change `g.Wait()` to `g.Wait(120000) |> ignore` in `Driver.fs` Step 1 — pick one, don't do both.

- [ ] **Step 2: Build**

Run: `dotnet build experiments/discovery-harness`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add experiments/discovery-harness/Orchestrator.fs
git commit -m "feat(discovery-harness): 3-seat staggered orchestrator + results json"
```

---

### Task 7: CLI + end-to-end validation run

**Files:**
- Create: `experiments/discovery-harness/Program.fs`
- Create: `experiments/discovery-harness/run.sh`

**Interfaces:**
- Consumes: `Orchestrator.*`, `LlmClient.defaultModel`, `Personas.get`, `Backend.autoDetect`.
- Produces: an executable that runs one game and writes results JSON to `--out`.

- [ ] **Step 1: Write `Program.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Program

open System.IO
open TicTacToe.OssDriver.Types
open TicTacToe.DiscoveryHarness

let private argVal (argv: string[]) name dflt =
    match Array.tryFindIndex ((=) name) argv with
    | Some i when i + 1 < argv.Length -> argv.[i + 1]
    | _ -> dflt

[<EntryPoint>]
let main argv =
    let backend = Backend.autoDetect ()
    let baseUrl = argVal argv "--base" ""
    if baseUrl = "" then eprintfn "--base <proxy url> required"; exit 2
    let rc: Orchestrator.RunConfig =
        { Backend = backend
          Model = argVal argv "--model" (LlmClient.defaultModel backend)
          Persona = Personas.get (argVal argv "--persona" "expert")
          Base = baseUrl
          MaxActions = argVal argv "--max-actions" "40" |> int
          MaxMoves = argVal argv "--max-moves" "12" |> int
          Window = argVal argv "--window" "10" |> int
          PollSeconds = argVal argv "--poll-seconds" "3" |> float }
    let transcripts = Orchestrator.runGame rc
    let json = Orchestrator.resultsJson rc transcripts
    let out = argVal argv "--out" ""
    if out <> "" then File.WriteAllText(out, json)
    printfn "%s" json
    0
```

- [ ] **Step 2: Build**

Run: `dotnet build experiments/discovery-harness`
Expected: succeeds.

- [ ] **Step 3: Write `run.sh`**

```bash
#!/usr/bin/env bash
# End-to-end: start a fresh arm, run 3 cold-start agents on one game, capture friction, tear down.
# Usage: run.sh <simple|proto> [persona]
set -euo pipefail
ARM="${1:-simple}"
PERSONA="${2:-expert}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ARENA="$HERE/../haiku-subagents/arena.sh"
OUT="/tmp/discovery-$ARM.results.json"

eval "$("$ARENA" up "$ARM" | sed 's/^/ARENA_/')" || true
URL="$(grep -oE 'URL=http://[^ ]+' /tmp/arena-$ARM.up 2>/dev/null | head -1 | cut -d= -f2- || true)"
# arena.sh prints URL=<proxy>; capture it directly:
URL="${ARENA_URL:?arena did not report URL}"

dotnet run --project "$HERE" --no-build -- --base "$URL" --persona "$PERSONA" --out "$OUT"

echo "--- friction ---"
uv run "$HERE/../haiku-subagents/friction.py" proxy "/tmp/arena-$ARM.http.jsonl" || true

"$ARENA" down "$ARM"
echo "results: $OUT"
```

> Implementer: confirm how `arena.sh up` surfaces the proxy URL (it prints `URL=...`). If `eval` capture is awkward, parse the printed `URL=` line into `$ARENA_URL` before the `dotnet run`. The contract: pass the **proxy** base (e.g. `http://localhost:6328` for simple) as `--base` so friction is logged.

- [ ] **Step 4: End-to-end validation run (Simple, the floor `0000`)**

```bash
chmod +x experiments/discovery-harness/run.sh
dotnet build experiments/discovery-harness
ANTHROPIC_API_KEY=$KEY experiments/discovery-harness/run.sh simple expert
```

Expected (success criteria — the walking skeleton is alive):
- Three parties in the results JSON; realized seats cover **X, O, observer** (the stagger worked).
- The **observer** party: `role_discrimination=true` (it reported `canIAct=false`) AND has a `rejected_moves≥1` with `NotAPlayer` in `rejection_codes` if it attempted a move, OR zero move attempts.
- At least the X/O parties: `recognize_appIs=true` and `recognize_goal=true` (Haiku recognizes tic-tac-toe cold).
- A terminal `outcome` (`over`/`ended`) for the players, or a bounded `move_cap` — not a hang.
- Friction summary prints a read:write ratio.

If the stagger fails to produce a clean X/O/observer split (e.g. two seats race), record the realized seats and adjust `gateTimeoutMs` / poll pacing; do not fake the split.

- [ ] **Step 5: End-to-end validation run (Proto-no-JS, `A=1`)**

```bash
ANTHROPIC_API_KEY=$KEY experiments/discovery-harness/run.sh proto expert
```

Expected: same structural success. `moves_scored` may be 0 (Proto has no JSON board snapshot → blunder scoring deferred to JSON surfaces); that is acceptable and noted in the spec.

- [ ] **Step 6: Commit**

```bash
git add experiments/discovery-harness/Program.fs experiments/discovery-harness/run.sh
git commit -m "feat(discovery-harness): CLI + end-to-end run script; SP1 walking skeleton validated"
```

---

## Self-Review

**Spec coverage (SP1 portion of the design):**
- Cold-start, URL+abstract-goal only → Task 1 `discoveryInstruction` (frozen, app-agnostic). ✓
- Two-moment recognize (pre + post-assignment report) → Tasks 2 (parse) + 4 (grade). ✓
- Role server-assigned, discovered by interaction → Tasks 5 (driver learns from accept/reject) + 6 (staggered determinism). ✓
- All-three-parties discovery agents → Task 6 runs 3 seats. ✓
- Observer affordance probe (recognizes it can't move) → Task 4 `RoleDiscriminationCorrect` + `NotAPlayer` tally. ✓
- DV: recognize / interact / pursue-completion / pursue-quality (blunder) / friction → Tasks 3+4 (scores) + 7 (friction via existing tooling). ✓
- Runs against existing Simple (`0000`) + Proto (`A=1`) → Task 7. ✓
- Generic HTTP, no pre-baked endpoints → Task 5 (base-URL-only, no `seed`). ✓
- Constant discovery instruction held across cells → Task 1 frozen text. ✓
- **Deferred to later SP:** 16-cell toggles (SP2), model titration (SP4), V_swagger/ERPC brackets (SP4), full N=5 replication + effect computation (SP3). SP1 proves ONE end-to-end graded game.

**Placeholder scan:** none — every step has concrete code/commands.

**Type consistency:** `Transcript`/`DiscoveryReport`/`RoleReport`/`BoardSnapshot` defined in Task 2, consumed identically in Tasks 4–6. `Optimal.positions`/`isBlunder` defined Task 3, used in Task 4. `SeatConfig`/`runSeat` defined Task 5, consumed Task 6. `RunConfig`/`runGame`/`resultsJson` defined Task 6, consumed Task 7. The Task-6 `{ t with Seat = realized }` re-stamp matches `Grader`'s use of `t.Seat` for role-discrimination expectation. ✓

**Known soft spots flagged for the implementer (not placeholders):**
- `run.sh` proxy-URL capture depends on `arena.sh up` output format — Step 3 note tells the implementer to verify and wire `$ARENA_URL`.
- Gate-bounding lives in the orchestrator timeout task; the driver's `g.Wait()` is unbounded by itself — the Task-6 note says pick one bounding site, not both.
- Staggable determinism is empirical; Task-7 Step-4 says record realized seats honestly if the split races, don't fabricate.
