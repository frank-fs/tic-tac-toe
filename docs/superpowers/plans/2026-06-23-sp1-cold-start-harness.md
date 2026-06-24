# SP1 — Cold-Start Discovery Harness + Grading (Implementation Plan, rev 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Each task is TDD: failing test first. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A cold-start harness that drives 3 discovery agents against the Simple app — each given only a URL + abstract goal, discovering the app, its role, and how to act — and grades recognize / interact / pursue per party, end-to-end, for one game. Plus a pre-task that makes Simple a true naive-HTML floor.

**Architecture:** New F# console project `experiments/discovery-harness/` that **links** the existing `oss-driver` LlmClient/Types/Personas source (no duplication, `oss-driver` untouched). Task 0 strips JSON content-negotiation from Simple so the `0000` floor is HTML-only. A cold-start `Driver` plays one seat from base-URL-only with a constant discovery instruction; an `Orchestrator` staggers 3 agents so server seat-assignment lands deterministically as X / O / observer; a pure `Grader` scores the captured transcript, reading the board from **HTML**. Friction reuses `proxy.py` / `friction.py` verbatim.

**Tech Stack:** F# / .NET 10.0, `System.Text.Json.Nodes`, `System.Net.Http` (cookie-jar = identity), `System.Text.RegularExpressions` (HTML board parse). Run validation against Simple (port 5328, `/arenas`).

## Global Constraints

- **TDD throughout (user-ordered).** Every pure/logic unit gets a FAILING test first, then code, then green: the prompt-invariant guard, all parsers (action, DISCOVERY, ROLE, HTML board), minimax, grader, orchestrator seat-realization, results-JSON shape, and the Simple JSON-strip. A task step list may show code before its test for readability — the implementer MUST reorder to test-first.
- **The live-LLM loop is the ONLY non-unit-tested boundary.** It is non-deterministic, so it is verified by an OBSERVED real end-to-end run (Task 7). That is the correct verification level for that boundary — NOT a deferral.
- **No deferrals, no partial work, no misleading passes.** A task is not done if it skipped a testable unit, loosened/weakened an assertion, faked a pass, or narrowed scope without explicit consent. `move_cap`/timeout outcomes are NOT acceptable "successes" in Task 7 — a real terminal (win/draw) and a clean X/O/observer split are required, or the run is a failure to debug.
- **Simple-only authoritative.** SP1 proves the full DV (recognize/interact/pursue-completion/pursue-quality) on Simple alone. Proto and the other 15 cells are SP2+ — not in SP1. This is scope, openly stated, not a hidden deferral.
- **Experiment isolation:** all new code under `experiments/` only — never `src/`. (The Simple app already lives in `experiments/src/`.)
- **Cold-start invariant:** the discovery instruction is CONSTANT across every future cell/arm and names NOTHING app-specific (no game, role, path, or move format) — only base URL, abstract goal, harness I/O protocol. What varies later is the served surface.
- **Controller verifies.** The controller (not just subagents) re-runs `dotnet build`, `dotnet test`, and the Task-7 e2e and reads the output before marking SP1 complete.
- **Worktree only:** branch `experiment/discovery-reset-spec`; `git merge --ff-only` back to `main`.
- **F#:** Holzmann — nesting ≤2, bound loops, ≤60-line functions, no module-level mutable, explicit errors, one indirection layer.

## Ground facts (verified against the apps — do not re-derive)

- **Simple:** `experiments/src/TicTacToe.Web.Simple`, port **5328**, route **`/arenas`**, cookie `TicTacToe.SimpleUser`. Move: `POST /arenas/{id}` body `player=X&position=<Name>` → 200/303 ok, ≥400 reject. Reject reasons surface as `NotYourTurn` / `NotAPlayer` text.
- **Simple JSON contaminant (Task 0 removes):** `Handlers.fs` `acceptsJson` (:23-24) routes `Accept: application/json` to `WriteAsJsonAsync(toArenaJson …)` at the getArena (:179-189), makeMove, restart, and create-error sites. Origin commit `c5964ce` (H6 V_simple baseline) — original, not a regression.
- **Simple HTML board (`templates/game.fs:64-88` `renderSquare`):** the board is `<div class="board">` with 9 `<form method="post" action="/arenas/{id}">`, each containing hidden inputs `player`,`position` and a `<button class="square…" type="submit" aria-label="{Position}" [disabled="disabled"]>LABEL</button>` where LABEL is `X`, `O`, or `·` (middle dot = empty). All 9 squares always render; occupied/over → disabled.
- **Identity:** cookie auto-login; one `HttpClient` w/ `CookieContainer` per agent = one stable seat (driver owns identity; no jar coaching).
- **Seat assignment (`Model.fs` `PlayerAssignmentManager`, every move):** X-slot-open + X-turn → assign X; O-slot-open + O-turn + not-X → assign O; both filled + neither → `Rejected NotAPlayer`; wrong turn → `Rejected NotYourTurn`.
- **Positions (9, board order):** `TopLeft TopCenter TopRight MiddleLeft MiddleCenter MiddleRight BottomLeft BottomCenter BottomRight`.
- **Simple test harness:** `experiments/test/TicTacToe.Web.Simple.Tests/` (`TestBase.fs`, `GameTests.fs`) — extend for the Task-0 JSON-strip test.
- **Run scripts (reuse as-is):** `experiments/haiku-subagents/arena.sh` (`up|down|status simple`, starts server+proxy on 6328→5328, prints `URL=`), `proxy.py` (JSONL `{ts,method,path,status}`), `friction.py` (`proxy <log>`).

## File Structure

```
experiments/src/TicTacToe.Web.Simple/Handlers.fs    # MODIFY (Task 0): remove JSON board negotiation
experiments/test/TicTacToe.Web.Simple.Tests/GameTests.fs  # MODIFY (Task 0): assert HTML-only
experiments/discovery-harness/
  TicTacToe.DiscoveryHarness.fsproj   # links ../oss-driver/{Types,LlmClient,Personas}.fs
  ColdStart.fs      # constant discovery instruction + cold-start prompt builder
  Transcript.fs     # per-party transcript: requests, reports, HTML board snapshots
  HtmlBoard.fs      # pure: parse Simple board HTML -> string[9]
  Optimal.fs        # pure tic-tac-toe minimax: board value + blunder detection
  Grader.fs         # pure: recognize / interact / pursue-completion / pursue-quality
  Driver.fs         # one cold-start seat: base-URL-only ReAct, Accept text/html, records transcript
  Orchestrator.fs   # stagger 3 agents -> deterministic X/O/observer; aggregate; results JSON
  Program.fs        # CLI: --base <proxy-url> --persona <p> --out <path>
experiments/discovery-harness/test/
  TicTacToe.DiscoveryHarness.Tests.fsproj
  HtmlBoardTests.fs ColdStartTests.fs TranscriptTests.fs OptimalTests.fs GraderTests.fs OrchestratorTests.fs
experiments/discovery-harness/run.sh   # arena up simple -> orchestrator -> friction -> arena down
```

---

### Task 0: Make Simple a naive-HTML floor (strip JSON board)

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Simple/Handlers.fs`
- Modify: `experiments/test/TicTacToe.Web.Simple.Tests/GameTests.fs`

**Interfaces:** none exported; behavior change only. After this task, no `/arenas/{id}` GET or move response serves `application/json` for board state.

- [ ] **Step 1: Read the test harness + handler**

Read `experiments/test/TicTacToe.Web.Simple.Tests/TestBase.fs` (how it builds a client) and `Handlers.fs:1-40,110-340` (the `acceptsJson`/`toArenaJson` sites). Confirm every board-state JSON branch.

- [ ] **Step 2: Write the failing test (HTML even when JSON requested)**

In `GameTests.fs`, add (use the existing TestBase client pattern — match its actual API):

```fsharp
[<Fact>]
let ``GET arena with Accept application-json still returns HTML board`` () = task {
    use client = TestBase.newClient ()            // match TestBase's real factory API
    let! created = client.PostAsync("/arenas", null)
    let loc = created.Headers.Location.ToString()  // /arenas/{id}
    use req = new HttpRequestMessage(HttpMethod.Get, loc)
    req.Headers.Accept.ParseAdd("application/json")
    use! resp = client.SendAsync req
    let! body = resp.Content.ReadAsStringAsync()
    Assert.DoesNotContain("application/json", resp.Content.Headers.ContentType.ToString())
    Assert.Contains("aria-label=", body)           // HTML board rendered, not JSON
    Assert.DoesNotContain("\"whoseTurn\"", body)   // no JSON board payload
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test experiments/test/TicTacToe.Web.Simple.Tests`
Expected: FAIL — current code returns JSON for `Accept: application/json`.

- [ ] **Step 4: Strip the JSON board branches**

In `Handlers.fs`: delete the `acceptsJson` helper (:22-24), the `ArenaJson` type (:115-119) and `toArenaJson` (:121-150), and at every board-state site replace `if acceptsJson ctx then … WriteAsJsonAsync(toArenaJson …) else renderArenaHtml …` with just `renderArenaHtml …`. Affected handlers: `getArena` (:179-189), `makeMove`, and the post-move/restart render sites (:256-265, :291, :330-331). Leave the `createArena` 409 capacity error as-is if it is a non-board error payload; the target is board STATE responses. After editing, no remaining reference to `acceptsJson`/`toArenaJson`/`ArenaJson` may compile.

- [ ] **Step 5: Run tests to verify pass + nothing else broke**

Run: `dotnet test experiments/test/TicTacToe.Web.Simple.Tests`
Expected: PASS — the new test green AND all pre-existing Simple tests still green (no regression).

- [ ] **Step 6: Commit**

```bash
git add experiments/src/TicTacToe.Web.Simple/Handlers.fs experiments/test/TicTacToe.Web.Simple.Tests/GameTests.fs
git commit -m "refactor(simple): strip JSON board negotiation — naive-HTML floor for 0000"
```

---

### Task 1: Scaffold + cold-start instruction (with invariant guard test)

**Files:**
- Create: `experiments/discovery-harness/TicTacToe.DiscoveryHarness.fsproj`
- Create: `experiments/discovery-harness/ColdStart.fs`
- Create: `experiments/discovery-harness/test/TicTacToe.DiscoveryHarness.Tests.fsproj`
- Create: `experiments/discovery-harness/test/ColdStartTests.fs`

**Interfaces:**
- Consumes: `TicTacToe.OssDriver.Types` (`Backend`), `LlmClient` (`chat`, `defaultModel`), `Personas` (`Persona`, `get`).
- Produces: `ColdStart.discoveryInstruction : string`; `ColdStart.systemPrompt : string -> Persona -> string`.

- [ ] **Step 1: Create both project files**

`experiments/discovery-harness/TicTacToe.DiscoveryHarness.fsproj`:
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
    <Compile Include="HtmlBoard.fs" />
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

`experiments/discovery-harness/test/TicTacToe.DiscoveryHarness.Tests.fsproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../ColdStart.fs" />
    <Compile Include="../HtmlBoard.fs" />
    <Compile Include="../Transcript.fs" />
    <Compile Include="../Optimal.fs" />
    <Compile Include="../Grader.fs" />
    <Compile Include="ColdStartTests.fs" />
    <Compile Include="HtmlBoardTests.fs" />
    <Compile Include="TranscriptTests.fs" />
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

ColdStart.fs links `Types/Personas/LlmClient`; the test project links the source files it tests directly. Test files for later tasks (HtmlBoardTests etc.) are created in those tasks — to keep this task building, create each not-yet-written test file as a one-line `module …` stub now and fill it in its task.

- [ ] **Step 2: Write the failing invariant-guard test**

`ColdStartTests.fs` — this test is load-bearing: it guards the cold-start invariant (the instruction must reveal nothing app-specific).

```fsharp
module TicTacToe.DiscoveryHarness.ColdStartTests

open Xunit
open TicTacToe.DiscoveryHarness

[<Theory>]
[<InlineData("tic-tac-toe")>]
[<InlineData("tic tac toe")>]
[<InlineData("/arenas")>]
[<InlineData("/games")>]
[<InlineData("position")>]
[<InlineData("TopLeft")>]
let ``discovery instruction reveals nothing app-specific`` (forbidden: string) =
    Assert.DoesNotContain(forbidden, ColdStart.discoveryInstruction.ToLowerInvariant())

[<Fact>]
let ``system prompt includes the base url and the report markers`` () =
    let p = ColdStart.systemPrompt "http://localhost:6328" (TicTacToe.OssDriver.Personas.get "expert")
    Assert.Contains("http://localhost:6328", p)
    Assert.Contains("DISCOVERY", p)
    Assert.Contains("ROLE", p)
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test experiments/discovery-harness/test --filter ColdStart`
Expected: FAIL — `ColdStart` not defined.

- [ ] **Step 4: Write `ColdStart.fs`** (forbidden tokens above must NOT appear in `discoveryInstruction`)

```fsharp
module TicTacToe.DiscoveryHarness.ColdStart

open TicTacToe.OssDriver.Types

// FROZEN cold-start contract. Names no app specifics (no game, role, path, move
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

- [ ] **Step 5: Run tests to verify pass; build the harness project**

Run: `dotnet test experiments/discovery-harness/test --filter ColdStart` → PASS.
Run: `dotnet build experiments/discovery-harness` → succeeds (linked oss-driver modules resolve).

- [ ] **Step 6: Commit**

```bash
git add experiments/discovery-harness/TicTacToe.DiscoveryHarness.fsproj experiments/discovery-harness/ColdStart.fs experiments/discovery-harness/test/
git commit -m "feat(discovery-harness): scaffold + frozen cold-start instruction (invariant-guarded)"
```

---

### Task 2: HTML board parser (pure, tested)

**Files:**
- Create: `experiments/discovery-harness/HtmlBoard.fs`
- Create/fill: `experiments/discovery-harness/test/HtmlBoardTests.fs`

**Interfaces:**
- Produces: `HtmlBoard.positions : string[]` (9, board order); `HtmlBoard.parse : html:string -> string[] option` — returns `Some cells` (length 9, each `"X"|"O"|""`) when all 9 `aria-label` squares are found, else `None`.

- [ ] **Step 1: Capture a REAL Simple board fixture**

Start Simple (`experiments/haiku-subagents/arena.sh up simple`), `curl -b /tmp/j -c /tmp/j http://localhost:6328/login` then GET the seeded arena page, save the `<div class="board">…</div>` HTML. Paste the real button markup into the test as the fixture (do NOT hand-invent markup — derive from real output). `arena.sh down simple` after.

- [ ] **Step 2: Write the failing test using the real fixture**

```fsharp
module TicTacToe.DiscoveryHarness.HtmlBoardTests

open Xunit
open TicTacToe.DiscoveryHarness

// Fixture = real markup captured in Step 1 (this is a representative shape; replace
// with the exact captured bytes). X at TopLeft, O at MiddleCenter, rest empty.
let private fixture = """
<div class="board">
<form method="post" action="/arenas/g1"><button class="square" type="submit" aria-label="TopLeft" disabled="disabled">X</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="TopCenter">·</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="TopRight">·</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="MiddleLeft">·</button></form>
<form method="post" action="/arenas/g1"><button class="square" type="submit" aria-label="MiddleCenter" disabled="disabled">O</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="MiddleRight">·</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="BottomLeft">·</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="BottomCenter">·</button></form>
<form method="post" action="/arenas/g1"><button class="square square-clickable" type="submit" aria-label="BottomRight">·</button></form>
</div>"""

[<Fact>]
let ``parses X O and empties in board order`` () =
    match HtmlBoard.parse fixture with
    | Some cells ->
        Assert.Equal(9, cells.Length)
        Assert.Equal("X", cells.[0])
        Assert.Equal("O", cells.[4])
        Assert.Equal("", cells.[1])
    | None -> Assert.Fail "expected a parsed board"

[<Fact>]
let ``non-board html yields None`` () =
    Assert.True((HtmlBoard.parse "<html>nope</html>").IsNone)
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test experiments/discovery-harness/test --filter HtmlBoard`
Expected: FAIL — `HtmlBoard` not defined.

- [ ] **Step 4: Write `HtmlBoard.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.HtmlBoard

open System.Text.RegularExpressions

let positions =
    [| "TopLeft"; "TopCenter"; "TopRight"
       "MiddleLeft"; "MiddleCenter"; "MiddleRight"
       "BottomLeft"; "BottomCenter"; "BottomRight" |]

// For each position, find aria-label="Pos" ... >LABEL< ; map X/O, else empty.
let private cellAt (html: string) (pos: string) : string option =
    let m = Regex.Match(html, "aria-label=\"" + Regex.Escape pos + "\"[^>]*>\\s*([^<\\s]*)\\s*<")
    if not m.Success then None
    else
        match m.Groups.[1].Value with
        | "X" -> Some "X"
        | "O" -> Some "O"
        | _ -> Some ""

let parse (html: string) : string[] option =
    let cells = positions |> Array.map (cellAt html)
    if cells |> Array.forall Option.isSome then Some(cells |> Array.map Option.get) else None
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test experiments/discovery-harness/test --filter HtmlBoard` → PASS.
If the real fixture differs from the shape above (attribute order, entity for `·`), adjust the regex AND keep the fixture as the real captured bytes — never weaken the test to pass.

- [ ] **Step 6: Commit**

```bash
git add experiments/discovery-harness/HtmlBoard.fs experiments/discovery-harness/test/HtmlBoardTests.fs
git commit -m "feat(discovery-harness): HTML board parser (real-fixture TDD)"
```

---

### Task 3: Transcript model (parsers tested)

**Files:**
- Create: `experiments/discovery-harness/Transcript.fs`
- Create/fill: `experiments/discovery-harness/test/TranscriptTests.fs`

**Interfaces:**
- Produces:
  - `type ReqRecord = { Method: string; Path: string; Body: string option; Status: int; BodySnippet: string }`
  - `type DiscoveryReport = { AppIs: string; Goal: string; IsMultiplayer: bool option; HowToParticipate: string }`
  - `type RoleReport = { MyRole: string; MyAffordances: string; CanIAct: bool option }`
  - `type BoardSnapshot = { AfterRequestIndex: int; Cells: string[] }`
  - `type Transcript = { Seat: string; Persona: string; Model: string; Requests: ResizeArray<ReqRecord>; mutable Discovery: DiscoveryReport option; mutable Role: RoleReport option; Boards: ResizeArray<BoardSnapshot>; mutable Outcome: string; mutable Tokens: int; mutable Actions: int; mutable MovesSubmitted: int }`
  - `Transcript.empty : string -> string -> string -> Transcript`
  - `Transcript.tryParseDiscovery : string -> DiscoveryReport option`
  - `Transcript.tryParseRole : string -> RoleReport option`

- [ ] **Step 1: Write failing tests**

```fsharp
module TicTacToe.DiscoveryHarness.TranscriptTests

open Xunit
open TicTacToe.DiscoveryHarness.Transcript

[<Fact>]
let ``parses a DISCOVERY report line`` () =
    let line = "DISCOVERY {\"appIs\":\"tic-tac-toe\",\"goal\":\"win\",\"isMultiplayer\":true,\"howToParticipate\":\"POST a move\"}"
    match tryParseDiscovery line with
    | Some d -> Assert.Equal("tic-tac-toe", d.AppIs); Assert.Equal(Some true, d.IsMultiplayer)
    | None -> Assert.Fail "expected discovery"

[<Fact>]
let ``parses a ROLE report line`` () =
    match tryParseRole "ROLE {\"myRole\":\"observer\",\"myAffordances\":\"watch\",\"canIAct\":false}" with
    | Some r -> Assert.Equal("observer", r.MyRole); Assert.Equal(Some false, r.CanIAct)
    | None -> Assert.Fail "expected role"

[<Fact>]
let ``non-report line yields None`` () =
    Assert.True((tryParseDiscovery "GET /arenas/g1").IsNone)
```

- [ ] **Step 2: Run to verify failure** — `dotnet test experiments/discovery-harness/test --filter Transcript` → FAIL.

- [ ] **Step 3: Write `Transcript.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Transcript

open System.Text.Json.Nodes
open System.Text.RegularExpressions

type ReqRecord = { Method: string; Path: string; Body: string option; Status: int; BodySnippet: string }
type DiscoveryReport = { AppIs: string; Goal: string; IsMultiplayer: bool option; HowToParticipate: string }
type RoleReport = { MyRole: string; MyAffordances: string; CanIAct: bool option }
type BoardSnapshot = { AfterRequestIndex: int; Cells: string[] }

type Transcript =
    { Seat: string; Persona: string; Model: string
      Requests: ResizeArray<ReqRecord>
      mutable Discovery: DiscoveryReport option
      mutable Role: RoleReport option
      Boards: ResizeArray<BoardSnapshot>
      mutable Outcome: string; mutable Tokens: int; mutable Actions: int; mutable MovesSubmitted: int }

let empty seat persona model =
    { Seat = seat; Persona = persona; Model = model
      Requests = ResizeArray(); Discovery = None; Role = None; Boards = ResizeArray()
      Outcome = "incomplete"; Tokens = 0; Actions = 0; MovesSubmitted = 0 }

let private str (o: JsonObject) k =
    match o.TryGetPropertyValue k with
    | true, v when v <> null -> v.GetValue<string>()
    | _ -> ""

let private boolOpt (o: JsonObject) k =
    match o.TryGetPropertyValue k with
    | true, v when v <> null -> (try Some(v.GetValue<bool>()) with _ -> None)
    | _ -> None

let private extract (prefix: string) (line: string) : JsonObject option =
    let m = Regex.Match(line, prefix + @"\s*(\{.*\})")
    if not m.Success then None
    else try Some(JsonNode.Parse(m.Groups.[1].Value) :?> JsonObject) with _ -> None

let tryParseDiscovery line =
    extract "DISCOVERY" line
    |> Option.map (fun o -> { AppIs = str o "appIs"; Goal = str o "goal"
                              IsMultiplayer = boolOpt o "isMultiplayer"; HowToParticipate = str o "howToParticipate" })

let tryParseRole line =
    extract "ROLE" line
    |> Option.map (fun o -> { MyRole = str o "myRole"; MyAffordances = str o "myAffordances"; CanIAct = boolOpt o "canIAct" })
```

- [ ] **Step 4: Run tests to verify pass** — `--filter Transcript` → PASS.

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Transcript.fs experiments/discovery-harness/test/TranscriptTests.fs
git commit -m "feat(discovery-harness): transcript model + report parsers"
```

---

### Task 4: Optimal-play scorer (pure, tested)

**Files:**
- Create: `experiments/discovery-harness/Optimal.fs`
- Create/fill: `experiments/discovery-harness/test/OptimalTests.fs`

**Interfaces:** `Optimal.winner : string[] -> string`; `Optimal.isBlunder : cells:string[] -> mover:string -> chosenIndex:int -> bool`.

- [ ] **Step 1: Write failing tests**

```fsharp
module TicTacToe.DiscoveryHarness.OptimalTests

open Xunit
open TicTacToe.DiscoveryHarness

let private board (s: string) = s.ToCharArray() |> Array.map (fun c -> if c = '.' then "" else string c)

[<Fact>] let ``winner detects a row`` () = Assert.Equal("X", Optimal.winner (board "XXX...OO."))
[<Fact>] let ``no winner on empty`` () = Assert.Equal("", Optimal.winner (board "........."))
[<Fact>] let ``taking the win is not a blunder`` () = Assert.False(Optimal.isBlunder (board "XX..O.O..") "X" 2)
[<Fact>] let ``missing the win is a blunder`` () = Assert.True(Optimal.isBlunder (board "XX..O.O..") "X" 3)
[<Fact>] let ``failing to block loses — blunder`` () =
    Assert.True(Optimal.isBlunder (board "XX..O....") "O" 5)
    Assert.False(Optimal.isBlunder (board "XX..O....") "O" 2)
```

- [ ] **Step 2: Run to verify failure** — `--filter Optimal` → FAIL.

- [ ] **Step 3: Write `Optimal.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Optimal

let private lines = [| (0,1,2);(3,4,5);(6,7,8);(0,3,6);(1,4,7);(2,5,8);(0,4,8);(2,4,6) |]

let winner (cells: string[]) : string =
    lines |> Array.tryPick (fun (a,b,c) ->
        if cells.[a] <> "" && cells.[a] = cells.[b] && cells.[b] = cells.[c] then Some cells.[a] else None)
    |> Option.defaultValue ""

let private other = function "X" -> "O" | _ -> "X"
let private score = function "X" -> 1 | "O" -> -1 | _ -> 0

// Minimax to absolute value (X=+1, O=-1, draw=0). Bounded by empty cells (≤9).
let rec private minimax (cells: string[]) (toMove: string) : int =
    let w = winner cells
    if w <> "" then score w
    else
        let empties = [ for i in 0..8 do if cells.[i] = "" then yield i ]
        if List.isEmpty empties then 0
        else
            let vals = empties |> List.map (fun i ->
                let n = Array.copy cells in n.[i] <- toMove; minimax n (other toMove))
            if toMove = "X" then List.max vals else List.min vals

let private bestValue (cells: string[]) (mover: string) : int =
    let empties = [ for i in 0..8 do if cells.[i] = "" then yield i ]
    let vals = empties |> List.map (fun i ->
        let n = Array.copy cells in n.[i] <- mover; minimax n (other mover))
    if List.isEmpty vals then 0 elif mover = "X" then List.max vals else List.min vals

let isBlunder (cells: string[]) (mover: string) (chosenIndex: int) : bool =
    if chosenIndex < 0 || chosenIndex > 8 || cells.[chosenIndex] <> "" then false
    else
        let n = Array.copy cells in n.[chosenIndex] <- mover
        let chosen = minimax n (other mover)
        let best = bestValue cells mover
        if mover = "X" then chosen < best else chosen > best
```

- [ ] **Step 4: Run tests to verify pass** — `--filter Optimal` → PASS.

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Optimal.fs experiments/discovery-harness/test/OptimalTests.fs
git commit -m "feat(discovery-harness): tested minimax blunder scorer"
```

---

### Task 5: Grader (pure, tested) — HTML boards, no deferral

**Files:**
- Create: `experiments/discovery-harness/Grader.fs`
- Create/fill: `experiments/discovery-harness/test/GraderTests.fs`

**Interfaces:**
- Consumes: `Transcript.*`, `Optimal.*`, `HtmlBoard.positions`.
- Produces: `type RecognizeScore`, `type Scores`, `Grader.grade : Transcript -> Scores` (fields below).

`RecognizeScore = { AppIsHit; GoalHit; MultiplayerHit; RoleNamed; RoleDiscriminationCorrect; FirstActionCoherent : bool }`
`Scores = { Recognize: RecognizeScore; AcceptedMoves; RejectedMoves: int; RejectionCodes: string list; Outcome: string; MovesToTerminal; Blunders; MovesScored; Actions; Tokens: int }`

Rules: AppIsHit/GoalHit = discovery text contains any truth keyword; MultiplayerHit = `IsMultiplayer = Some true`; RoleNamed = role report present and names x/o/observer; RoleDiscriminationCorrect = observer→`canIAct=Some false`, X/O→`Some true`; FirstActionCoherent = first POST status ≠ 404; AcceptedMoves/RejectedMoves = POST <400 / ≥400; RejectionCodes = `NotYourTurn`/`NotAPlayer`/status from snippet; Blunders/MovesScored = replay accepted moves vs the latest `Boards` snapshot preceding each, via `Optimal.isBlunder` (board index from `HtmlBoard.positions`). Quality applies on every surface with board snapshots — no surface carve-out.

- [ ] **Step 1: Write failing tests**

```fsharp
module TicTacToe.DiscoveryHarness.GraderTests

open Xunit
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

let private t0 seat = Transcript.empty seat "expert" "test"

[<Fact>]
let ``recognizes tic-tac-toe and multiplayer`` () =
    let t = t0 "X"
    t.Discovery <- Some { AppIs = "a tic-tac-toe game"; Goal = "three in a row to win"
                          IsMultiplayer = Some true; HowToParticipate = "post a move" }
    let s = Grader.grade t
    Assert.True(s.Recognize.AppIsHit); Assert.True(s.Recognize.GoalHit); Assert.True(s.Recognize.MultiplayerHit)

[<Fact>]
let ``observer that cannot act scores role-discrimination`` () =
    let t = t0 "observer"
    t.Role <- Some { MyRole = "observer"; MyAffordances = "watch"; CanIAct = Some false }
    Assert.True((Grader.grade t).Recognize.RoleDiscriminationCorrect)

[<Fact>]
let ``observer claiming it can act fails role-discrimination`` () =
    let t = t0 "observer"
    t.Role <- Some { MyRole = "observer"; MyAffordances = "watch"; CanIAct = Some true }
    Assert.False((Grader.grade t).Recognize.RoleDiscriminationCorrect)

[<Fact>]
let ``rejected NotAPlayer move tallied with code`` () =
    let t = t0 "observer"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g1"; Body = Some "player=X&position=TopLeft"
                     Status = 403; BodySnippet = "Rejected NotAPlayer" }
    let s = Grader.grade t
    Assert.Equal(0, s.AcceptedMoves); Assert.Equal(1, s.RejectedMoves); Assert.Contains("NotAPlayer", s.RejectionCodes)

[<Fact>]
let ``a blundered accepted move is counted`` () =
    let t = t0 "X"
    // Board before move: X can win at index 2 (TopRight). Agent plays index 3 instead (blunder).
    t.Boards.Add { AfterRequestIndex = 0; Cells = [| "X";"X";"";"";"O";"";"O";"";"" |] }
    t.Requests.Add { Method = "GET"; Path = "/arenas/g1"; Body = None; Status = 200; BodySnippet = "" }      // index 0
    t.Requests.Add { Method = "POST"; Path = "/arenas/g1"; Body = Some "player=X&position=MiddleLeft"        // index 1
                     Status = 200; BodySnippet = "" }
    let s = Grader.grade t
    Assert.Equal(1, s.MovesScored); Assert.Equal(1, s.Blunders)
```

- [ ] **Step 2: Run to verify failure** — `--filter Grader` → FAIL.

- [ ] **Step 3: Write `Grader.fs`**

```fsharp
module TicTacToe.DiscoveryHarness.Grader

open TicTacToe.DiscoveryHarness.Transcript

type RecognizeScore =
    { AppIsHit: bool; GoalHit: bool; MultiplayerHit: bool
      RoleNamed: bool; RoleDiscriminationCorrect: bool; FirstActionCoherent: bool }

type Scores =
    { Recognize: RecognizeScore
      AcceptedMoves: int; RejectedMoves: int; RejectionCodes: string list
      Outcome: string; MovesToTerminal: int; Blunders: int; MovesScored: int; Actions: int; Tokens: int }

let private appKw = [| "tic-tac-toe"; "tic tac toe"; "tictactoe"; "noughts" |]
let private goalKw = [| "three in a row"; "3 in a row"; "win"; "row"; "line" |]

let private hits (kws: string[]) (text: string) =
    let low = text.ToLowerInvariant()
    kws |> Array.exists (fun k -> low.Contains(k.ToLowerInvariant()))

let private roleNamed (r: RoleReport) =
    [ "x"; "o"; "observer"; "spectator"; "watcher" ] |> List.exists (r.MyRole.ToLowerInvariant().Contains)

let private isObserverSeat (seat: string) = seat.ToLowerInvariant().Contains "observ"

let private codeOf (snippet: string) (status: int) =
    [ "NotYourTurn"; "NotAPlayer" ] |> List.tryFind snippet.Contains |> Option.defaultValue (string status)

let private recognize (t: Transcript) : RecognizeScore =
    let appIs, goal, mp =
        match t.Discovery with
        | Some d -> hits appKw d.AppIs, hits goalKw d.Goal, d.IsMultiplayer = Some true
        | None -> false, false, false
    let named, discrim =
        match t.Role with
        | Some r -> roleNamed r, (r.CanIAct = Some(not (isObserverSeat t.Seat)))
        | None -> false, false
    let firstCoherent =
        match t.Requests |> Seq.tryFind (fun r -> r.Method = "POST") with
        | Some p -> p.Status <> 404
        | None -> false
    { AppIsHit = appIs; GoalHit = goal; MultiplayerHit = mp
      RoleNamed = named; RoleDiscriminationCorrect = discrim; FirstActionCoherent = firstCoherent }

let private quality (t: Transcript) : int * int =
    let mutable blunders, scored = 0, 0
    for idx in 0 .. t.Requests.Count - 1 do
        let r = t.Requests.[idx]
        if r.Method = "POST" && r.Status < 400 then
            let prior =
                t.Boards |> Seq.filter (fun b -> b.AfterRequestIndex < idx)
                         |> Seq.sortByDescending (fun b -> b.AfterRequestIndex) |> Seq.tryHead
            let posName =
                r.Body |> Option.bind (fun b ->
                    b.Split('&') |> Array.tryPick (fun kv ->
                        let p = kv.Split('=') in if p.Length = 2 && p.[0] = "position" then Some p.[1] else None))
            match prior, posName with
            | Some board, Some name when board.Cells.Length = 9 && (t.Seat = "X" || t.Seat = "O") ->
                let chosen = System.Array.IndexOf(HtmlBoard.positions, name)
                if chosen >= 0 then
                    scored <- scored + 1
                    if Optimal.isBlunder board.Cells t.Seat chosen then blunders <- blunders + 1
            | _ -> ()
    blunders, scored

let grade (t: Transcript) : Scores =
    let posts = t.Requests |> Seq.filter (fun r -> r.Method = "POST") |> Seq.toList
    let accepted = posts |> List.filter (fun r -> r.Status < 400)
    let rejected = posts |> List.filter (fun r -> r.Status >= 400)
    let blunders, scored = quality t
    { Recognize = recognize t
      AcceptedMoves = List.length accepted; RejectedMoves = List.length rejected
      RejectionCodes = rejected |> List.map (fun r -> codeOf r.BodySnippet r.Status) |> List.distinct
      Outcome = t.Outcome; MovesToTerminal = List.length accepted
      Blunders = blunders; MovesScored = scored; Actions = t.Actions; Tokens = t.Tokens }
```

- [ ] **Step 4: Run tests to verify pass** — `--filter Grader` → PASS.

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Grader.fs experiments/discovery-harness/test/GraderTests.fs
git commit -m "feat(discovery-harness): tested grader (recognize/interact/quality, HTML boards)"
```

---

### Task 6: Cold-start Driver (one seat, base-URL-only, Accept text/html)

**Files:**
- Create: `experiments/discovery-harness/Driver.fs`

**Interfaces:**
- Consumes: `ColdStart.systemPrompt`, `Transcript.*`, `HtmlBoard.parse`, `LlmClient.chat`, `Personas.Persona`, `Types.Backend`.
- Produces: `type SeatConfig`; `Driver.parseAction : string -> (string*string*string option) option`; `Driver.runSeat : SeatConfig -> Transcript`.

`parseAction` is pure → unit-test it. The loop is live-LLM → verified in Task 8. Differences from `oss-driver/Driver.fs`: no `seed`/path pre-resolution (start from `Base` only), no role in prompt, `Accept: text/html` only (no JSON), parses DISCOVERY/ROLE into the transcript, captures HTML boards via `HtmlBoard.parse`, supports staggering hooks.

- [ ] **Step 1: Write a failing parseAction test** in `test/` (add `DriverTests.fs` to the test fsproj `<Compile>` list and link `../Driver.fs` — note `Driver.fs` opens `System.Net.Http`; that's fine in test too):

```fsharp
module TicTacToe.DiscoveryHarness.DriverTests
open Xunit
open TicTacToe.DiscoveryHarness
[<Fact>] let ``parses a GET action`` () =
    Assert.Equal(Some("GET","/arenas/g1",None), Driver.parseAction "GET /arenas/g1")
[<Fact>] let ``parses a POST with body`` () =
    Assert.Equal(Some("POST","/arenas/g1",Some "player=X&position=TopLeft"),
                 Driver.parseAction "POST /arenas/g1 player=X&position=TopLeft")
```

- [ ] **Step 2: Run to verify failure** — `--filter Driver` → FAIL.

- [ ] **Step 3: Write `Driver.fs`**

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
      StartGate: ManualResetEventSlim option; SeatedSignal: (unit -> unit) option }

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
    [ "data-game-status=\"won\""; "data-game-status=\"draw\""; "wins!"; "it's a draw" ]

let private terminalOutcome (status: int) (body: string) : string option =
    if status = 404 then Some "ended"
    else
        let low = body.ToLowerInvariant()
        if terminalTokens |> List.exists low.Contains then Some "over" else None

let private send (client: HttpClient) (baseUrl: string) (m: string) (path: string) (body: string option) : int * string =
    let url = baseUrl.TrimEnd('/') + path
    use req = new HttpRequestMessage(HttpMethod(m), url)
    body |> Option.iter (fun b -> req.Content <- new StringContent(b, Encoding.UTF8, "application/x-www-form-urlencoded"))
    req.Headers.TryAddWithoutValidation("Accept", "text/html") |> ignore   // HTML-only — no JSON shortcut
    try use resp = client.Send req in int resp.StatusCode, resp.Content.ReadAsStringAsync().Result
    with e -> 0, sprintf "<request error: %s>" e.Message

let private window (messages: ResizeArray<string * string>) (n: int) : (string * string) list =
    let sys = messages.[0]
    let rest = messages |> Seq.skip 1 |> Seq.toList
    let tail = if List.length rest > n then rest |> List.skip (List.length rest - n) else rest
    sys :: tail

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
        let reply = try LlmClient.chat cfg.Backend cfg.Model (window messages cfg.Window)
                    with e -> sprintf "<chat error: %s>" e.Message
        messages.Add("assistant", reply)
        captureReports t reply
        match parseAction reply with
        | None -> messages.Add("user", "Reply with one line: a DISCOVERY/ROLE JSON report, or GET <path>, or POST <path> <body>.")
        | Some(m, path, body) ->
            if m = "POST" && not firstSeated then cfg.StartGate |> Option.iter (fun g -> g.Wait())
            let status, text = send client cfg.Base m path body
            let reqIndex = t.Requests.Count
            t.Requests.Add { Method = m; Path = path; Body = body; Status = status
                             BodySnippet = (if text.Length <= 300 then text else text.[..299]) }
            HtmlBoard.parse text |> Option.iter (fun cells -> t.Boards.Add { AfterRequestIndex = reqIndex; Cells = cells })
            if m = "POST" then
                t.MovesSubmitted <- t.MovesSubmitted + 1
                if status < 400 && not firstSeated then firstSeated <- true; cfg.SeatedSignal |> Option.iter (fun f -> f())
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

- [ ] **Step 4: Run parseAction tests + build** — `--filter Driver` → PASS; `dotnet build experiments/discovery-harness` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Driver.fs experiments/discovery-harness/test/
git commit -m "feat(discovery-harness): cold-start single-seat driver (HTML-only, tested parseAction)"
```

---

### Task 7: Orchestrator (stagger 3 → X/O/observer; seat-realization tested)

**Files:**
- Create: `experiments/discovery-harness/Orchestrator.fs`
- Create/fill: `experiments/discovery-harness/test/OrchestratorTests.fs`

**Interfaces:**
- Consumes: `Driver.*`, `Grader.grade`, `Transcript.*`.
- Produces: `type RunConfig`; `Orchestrator.realizedSeat : Transcript -> string`; `Orchestrator.runGame : RunConfig -> Transcript list`; `Orchestrator.resultsJson : RunConfig -> Transcript list -> string`.

`realizedSeat` (pure) → unit-test. `runGame` (live) → verified in Task 8. Stagger: A no gate, signals gateB on seated; B waits gateB, signals gateC on seated; C waits gateC (opens only after both seats fill ⇒ C is `NotAPlayer` observer). A bounded timeout task opens each gate after `gateTimeoutMs` so a stalled seat cannot hang the game (R10). `g.Wait()` in the driver is bounded by this timeout-set.

- [ ] **Step 1: Write the failing seat-realization test**

```fsharp
module TicTacToe.DiscoveryHarness.OrchestratorTests

open Xunit
open TicTacToe.DiscoveryHarness
open TicTacToe.DiscoveryHarness.Transcript

[<Fact>]
let ``realized seat reads X from role report`` () =
    let t = Transcript.empty "seatA" "expert" "m"
    t.Role <- Some { MyRole = "X"; MyAffordances = "move"; CanIAct = Some true }
    Assert.Equal("X", Orchestrator.realizedSeat t)

[<Fact>]
let ``no accepted move and no role => observer`` () =
    let t = Transcript.empty "seatC" "expert" "m"
    t.Requests.Add { Method = "POST"; Path = "/arenas/g"; Body = None; Status = 403; BodySnippet = "NotAPlayer" }
    Assert.Equal("observer", Orchestrator.realizedSeat t)
```

- [ ] **Step 2: Run to verify failure** — `--filter Orchestrator` → FAIL.

- [ ] **Step 3: Write `Orchestrator.fs`**

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
let private gateTimeoutMs = 120000

let private seatCfg (rc: RunConfig) label gate signal : Driver.SeatConfig =
    { Backend = rc.Backend; Model = rc.Model; Seat = label; Persona = rc.Persona; Base = rc.Base
      MaxActions = rc.MaxActions; MaxMoves = rc.MaxMoves; Window = rc.Window; PollSeconds = rc.PollSeconds
      StartGate = gate; SeatedSignal = signal }

let realizedSeat (t: Transcript) : string =
    let m = t.Role |> Option.map (fun r -> r.MyRole.ToLowerInvariant())
    match m with
    | Some s when s.Contains "observ" || s.Contains "spectat" || s.Contains "watch" -> "observer"
    | Some s when s.Contains "x" -> "X"
    | Some s when s.Contains "o" -> "O"
    | _ -> if t.Requests |> Seq.exists (fun r -> r.Method = "POST" && r.Status < 400) then "player" else "observer"

let runGame (rc: RunConfig) : Transcript list =
    use gateB = new ManualResetEventSlim(false)
    use gateC = new ManualResetEventSlim(false)
    let cfgA = seatCfg rc "seatA" None (Some(fun () -> gateB.Set()))
    let cfgB = seatCfg rc "seatB" (Some gateB) (Some(fun () -> gateC.Set()))
    let cfgC = seatCfg rc "seatC" (Some gateC) None
    Task.Run(fun () -> Thread.Sleep gateTimeoutMs; gateB.Set()) |> ignore
    Task.Run(fun () -> Thread.Sleep gateTimeoutMs; gateC.Set()) |> ignore
    [ cfgA; cfgB; cfgC ] |> List.map (fun c -> Task.Run(fun () -> Driver.runSeat c)) |> List.map (fun t -> t.Result)

let resultsJson (rc: RunConfig) (transcripts: Transcript list) : string =
    let parties = JsonArray()
    for t in transcripts do
        let realized = realizedSeat t
        let g = Grader.grade { t with Seat = realized }
        let p = JsonObject()
        p["seat"] <- JsonValue.Create realized
        p["recognize_appIs"] <- JsonValue.Create g.Recognize.AppIsHit
        p["recognize_goal"] <- JsonValue.Create g.Recognize.GoalHit
        p["recognize_multiplayer"] <- JsonValue.Create g.Recognize.MultiplayerHit
        p["role_named"] <- JsonValue.Create g.Recognize.RoleNamed
        p["role_discrimination"] <- JsonValue.Create g.Recognize.RoleDiscriminationCorrect
        p["first_action_coherent"] <- JsonValue.Create g.Recognize.FirstActionCoherent
        p["accepted_moves"] <- JsonValue.Create g.AcceptedMoves
        p["rejected_moves"] <- JsonValue.Create g.RejectedMoves
        p["rejection_codes"] <- JsonValue.Create (String.concat "," g.RejectionCodes)
        p["outcome"] <- JsonValue.Create g.Outcome
        p["blunders"] <- JsonValue.Create g.Blunders
        p["moves_scored"] <- JsonValue.Create g.MovesScored
        p["actions"] <- JsonValue.Create g.Actions
        parties.Add p
    let root = JsonObject()
    root["model"] <- JsonValue.Create rc.Model
    root["persona"] <- JsonValue.Create rc.Persona.Name
    root["base"] <- JsonValue.Create rc.Base
    root["parties"] <- parties
    root.ToJsonString()
```

- [ ] **Step 4: Run tests to verify pass + build** — `--filter Orchestrator` → PASS; `dotnet build experiments/discovery-harness` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Orchestrator.fs experiments/discovery-harness/test/OrchestratorTests.fs
git commit -m "feat(discovery-harness): staggered 3-seat orchestrator + results json (tested seat-realization)"
```

---

### Task 8: CLI + observed end-to-end validation (Simple only)

**Files:**
- Create: `experiments/discovery-harness/Program.fs`
- Create: `experiments/discovery-harness/run.sh`

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
    let json = Orchestrator.resultsJson rc (Orchestrator.runGame rc)
    let out = argVal argv "--out" ""
    if out <> "" then File.WriteAllText(out, json)
    printfn "%s" json
    0
```

- [ ] **Step 2: Build** — `dotnet build experiments/discovery-harness` → succeeds.

- [ ] **Step 3: Write `run.sh`**

```bash
#!/usr/bin/env bash
# End-to-end: fresh Simple arm, 3 cold-start agents on one game, friction, teardown.
# Usage: run.sh [persona]
set -euo pipefail
PERSONA="${1:-expert}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ARENA="$HERE/../haiku-subagents/arena.sh"
OUT="/tmp/discovery-simple.results.json"

UP="$("$ARENA" up simple)"; echo "$UP"
URL="$(printf '%s\n' "$UP" | grep -oE 'URL=http://[^ ]+' | head -1 | cut -d= -f2-)"
[ -n "$URL" ] || { echo "no proxy URL from arena"; "$ARENA" down simple; exit 1; }

dotnet run --project "$HERE" --no-build -- --base "$URL" --persona "$PERSONA" --out "$OUT"
echo "--- friction ---"
uv run "$HERE/../haiku-subagents/friction.py" proxy "/tmp/arena-simple.http.jsonl" || true
"$ARENA" down simple
echo "results: $OUT"
```

(Implementer: confirm `arena.sh up simple` prints a line matching `URL=http://...`; if the key differs, adjust the `grep`. Pass the **proxy** base so friction is logged.)

- [ ] **Step 4: Observed end-to-end run — the walking skeleton must be ALIVE**

```bash
chmod +x experiments/discovery-harness/run.sh
dotnet build experiments/discovery-harness
ANTHROPIC_API_KEY=$KEY experiments/discovery-harness/run.sh expert
```

Hard success criteria (NO soft pass — every one must hold, or debug, do not rationalize):
1. Results JSON has 3 parties; realized seats are exactly **X, O, observer** (stagger worked).
2. **observer** party: `role_discrimination=true` AND (`rejected_moves≥1` with `NotAPlayer` in `rejection_codes`, OR zero POSTs) — it recognized it cannot act.
3. X and O parties: `recognize_appIs=true` and `recognize_goal=true`.
4. Game reaches a real terminal: at least the player parties show `outcome` ∈ {`over`,`ended`}. A `move_cap` or timeout is a FAILURE to investigate, not a pass.
5. `moves_scored ≥ 1` for at least one player (HTML board parsing fed the blunder scorer end-to-end).
6. Friction prints a read:write ratio.

If any criterion fails, capture the transcript (`HARNESS_DEBUG=1`) and fix the cause; never edit the criteria to match a worse result.

- [ ] **Step 5: Commit**

```bash
git add experiments/discovery-harness/Program.fs experiments/discovery-harness/run.sh
git commit -m "feat(discovery-harness): CLI + observed e2e; SP1 walking skeleton validated on Simple"
```

---

## Self-Review

**Spec coverage (SP1 scope):** cold-start URL+goal-only → Task 1 (frozen, invariant-guarded). Naive-HTML floor → Task 0 (JSON stripped). Two-moment recognize → Tasks 3 (parse) + 5 (grade). Role discovered by interaction → Tasks 6 (driver learns from accept/reject) + 7 (staggered determinism). All-three discovery agents → Task 7. Observer affordance probe → Task 5 (`RoleDiscriminationCorrect` + `NotAPlayer`). DV recognize/interact/pursue-completion/pursue-quality(HTML board)/friction → Tasks 2+4+5 + 8. Generic HTTP, no pre-baked endpoints, Accept text/html → Task 6. Observed e2e on Simple → Task 8.

**Out of SP1 (openly scoped, not deferred):** Proto + the other 15 cells = SP2; model titration = SP4; V_swagger/ERPC brackets = SP4; N=5 replication + effect computation = SP3.

**Placeholder scan:** none — every step carries concrete code/commands. The one judgement point (real HTML fixture in Task 2) is explicitly "capture real bytes," not invent.

**Type consistency:** `Transcript`/reports/`BoardSnapshot` (Task 3) used identically in 5–7. `HtmlBoard.parse`/`positions` (Task 2) used in 5–6. `Optimal.isBlunder` (Task 4) used in 5. `SeatConfig`/`runSeat`/`parseAction` (Task 6) used in 7. `RunConfig`/`runGame`/`realizedSeat`/`resultsJson` (Task 7) used in 8. The Task-7 `{ t with Seat = realized }` re-stamp matches `Grader`'s `t.Seat` use for role-discrimination and quality.

**TDD coverage:** every pure unit has a failing-test-first step (Tasks 0,1,2,3,4,5,6,7). The live-LLM loop is the sole run-verified boundary (Task 8), per Global Constraints.
