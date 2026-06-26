# SP2 — Surface Factorial Binary — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the existing `TicTacToe.Web.Simple` app into a single `TicTacToe.Web.Surface` binary whose four discovery factors (A, C, Sd, So) are independent startup-flag conditionals, so one codebase serves any of the 16 factorial cells with no cross-cell drift.

**Architecture:** A `Surface` record parsed once at startup from `TICTACTOE_CELL` is held as a DI singleton and passed explicitly to renderers/handlers. Each factor is a pure conditional on that record. One process per cell; the harness boots the binary with `TICTACTOE_CELL`, runs games, kills it. The binary depends only on `TicTacToe.Engine` (pure game lib) — no main-`src` app coupling.

**Tech Stack:** F# / .NET 10, Frank 7.2.0 (`Frank`, `Frank.Auth`), Oxpecker.ViewEngine 2.x, xUnit (new unit-test project), cookie auth, `GameStore` + `PlayerAssignmentManager` (MailboxProcessor) in-memory state.

**Spec:** [`docs/superpowers/specs/2026-06-25-sp2-surface-factorial-binary-design.md`](../specs/2026-06-25-sp2-surface-factorial-binary-design.md)

## Global Constraints

- **One shared product dependency:** `TicTacToe.Engine` only. Do **not** add a `ProjectReference` to any `src/TicTacToe.Web*` app. Treat `TicTacToe.Engine` as a frozen contract — do not edit it for this SP.
- **One process per cell.** No per-request cell routing. `TICTACTOE_CELL` is read once at startup.
- **Config fail-fast (Holzmann R12):** unset/empty `TICTACTOE_CELL` → floor `0000`; set-but-malformed → throw at startup.
- **No module-level mutable (R13):** `Surface` flows through DI and explicit parameters, never a global.
- **Holzmann:** nesting ≤ 2 (R9), bound loops (R10), one job ≤ 60 lines (R11), one indirection layer (R15). Single parameterized renderer with `Surface` branching — **not** decorator composition, **not** per-combination templates.
- **TDD:** failing test first for `Surface.parse` and every present-iff-flag assertion. Live e2e runs remain the real validation.
- **Worktree-only.** All work on branch `experiment/discovery-reset-spec` in `.claude/worktrees/discovery-reset`. Commit per task.
- **Cell flag order is `A C Sd So`** (`TICTACTOE_CELL=ACSdSo`, 4 chars each `0`/`1`).

## Spec errata applied during planning (confirmed with user)

1. **A0 keeps the existing "X's turn" status line.** §4's "A0 = no turn statement" is imprecise; the status line is plain info, not an affordance. The A axis differs **only by action availability** (move forms). `0000` therefore stays byte-for-byte identical to today's Simple (criterion #2 unchanged).
2. **C0 keeps the pre-existing per-square `aria-label="<position>"`** that today's Simple already emits on buttons. C1's true markers are `role="grid"`/`role="gridcell"`, enriched labels (position + occupancy), `aria-live`, and `<nav>`. Present-iff-C tests target `role="grid"`/`aria-live`, **not** aria-label existence.
3. **A and So are no-ops on the home/lobby page.** A (turn/role actions) and So (schema.org `Game` typing) are arena concepts. Home gets C (ARIA/nav) and Sd (Link headers + listing). Noted so the implementer does not invent lobby affordances.

## File structure

After Task 0 the app lives at `experiments/src/TicTacToe.Web.Surface/`:

| File | Responsibility | Touched by |
|---|---|---|
| `Surface.fs` (new, first in compile order) | `Surface` record + `parse` (fail-fast) + `fromEnvironment` | Task 1 |
| `Program.fs` | DI singleton wiring, resources, OpenAPI removal, `/profile` + `/.well-known/home` resources, OPTIONS middleware | Tasks 1,2,5 |
| `Handlers.fs` | resolve `Surface`, thread to renderers, set Sd `Link`/`Allow` headers, profile/home handlers | Tasks 2,5 |
| `templates/game.fs` | `renderArenaPage surface …`; A gating, C ARIA, So JSON-LD | Tasks 2,3,4,6 |
| `templates/home.fs` | `homePage surface …`; C ARIA/nav | Tasks 2,4 |
| `experiments/test/TicTacToe.Web.Surface.Tests/` (new xUnit) | `Surface.parse` + present-iff-flag render assertions | Tasks 1,3,4,5,6 |

---

## Task 0: Rename TicTacToe.Web.Simple → TicTacToe.Web.Surface

Full mechanical rename: directory, project, assembly, **all F# namespaces**, log category, and harness path references. Behavior-preserving — no factor logic yet.

**Files:**
- Rename dir: `experiments/src/TicTacToe.Web.Simple/` → `experiments/src/TicTacToe.Web.Surface/`
- Rename proj: `…/TicTacToe.Web.Simple.fsproj` → `…/TicTacToe.Web.Surface.fsproj`
- Modify (namespace replace): all `*.fs` in that dir (`GameStore.fs`, `Model.fs`, `Logger.fs`, `Extensions.fs`, `Auth.fs`, `templates/shared/layout.fs`, `templates/game.fs`, `templates/home.fs`, `Handlers.fs`, `Program.fs`)
- Modify (path strings): `experiments/haiku-subagents/arena.sh:31`, `experiments/test/TicTacToe.Web.Simple.Tests/TestBase.fs:92`, `experiments/test/CLAUDE.md:8-9`, `experiments/discovery-harness/test/HtmlBoardTests.fs:6` (comment)

**Interfaces:**
- Produces: project `TicTacToe.Web.Surface`, root namespace/module prefix `TicTacToe.Web.Surface.*`, runnable at `experiments/src/TicTacToe.Web.Surface/`.

**Out of scope (deliberate):** the `experiments/test/TicTacToe.Web.Simple.Tests` project keeps its name (it is an HTTP/Playwright comparison suite with no project reference; only the server-launch path strings inside it change). Renaming it carries no experimental signal.

- [ ] **Step 1: Move directory and project file (git-tracked)**

```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
git mv experiments/src/TicTacToe.Web.Simple experiments/src/TicTacToe.Web.Surface
git mv experiments/src/TicTacToe.Web.Surface/TicTacToe.Web.Simple.fsproj \
       experiments/src/TicTacToe.Web.Surface/TicTacToe.Web.Surface.fsproj
```

- [ ] **Step 2: Replace the namespace in every app source file**

```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
find experiments/src/TicTacToe.Web.Surface -name '*.fs' \
  -not -path '*/obj/*' -not -path '*/bin/*' \
  -exec sed -i '' 's/TicTacToe\.Web\.Simple/TicTacToe.Web.Surface/g' {} +
```

This also rewrites the log category string in `Program.fs:41` (`AddFilter("TicTacToe.Web.Surface", …)`) — intended.

- [ ] **Step 3: Update harness + test path references**

```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
# App path string ONLY — do NOT touch "...Web.Simple.Tests" project name.
sed -i '' 's#src/TicTacToe\.Web\.Simple/#src/TicTacToe.Web.Surface/#g' \
  experiments/haiku-subagents/arena.sh \
  experiments/test/TicTacToe.Web.Simple.Tests/TestBase.fs \
  experiments/test/CLAUDE.md
# Cosmetic comment in harness test
sed -i '' 's/TicTacToe\.Web\.Simple (/TicTacToe.Web.Surface (/' \
  experiments/discovery-harness/test/HtmlBoardTests.fs
```

In `experiments/haiku-subagents/arena.sh`, also rename the arm label so it reads honestly: change the `simple)` case label to `surface)` and its echo of the expected arms (`proto|simple` → `proto|surface`). Leave `PORT=5328`, `ROUTE=arenas` unchanged.

- [ ] **Step 4: Clean stale build artifacts from the old name**

```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
rm -rf experiments/src/TicTacToe.Web.Surface/obj experiments/src/TicTacToe.Web.Surface/bin
```

- [ ] **Step 5: Build the renamed project**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet build experiments/src/TicTacToe.Web.Surface/TicTacToe.Web.Surface.fsproj
```
Expected: `Build succeeded`. No `TicTacToe.Web.Simple` identifiers remain — verify:
```bash
grep -rn 'TicTacToe\.Web\.Simple' experiments/src/TicTacToe.Web.Surface --include='*.fs' | grep -v '/obj/' | grep -v '/bin/'
```
Expected: no output.

- [ ] **Step 6: Smoke-run the renamed binary on the floor**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 TICTACTOE_INITIAL_GAMES=1 \
  dotnet run --project experiments/src/TicTacToe.Web.Surface --urls http://localhost:5328 &
sleep 6
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5328/login   # expect 302
curl -s -c /tmp/sp2.jar http://localhost:5328/login >/dev/null
curl -s -b /tmp/sp2.jar http://localhost:5328/ | grep -c 'Tic Tac Toe'  # expect >=1
kill %1 2>/dev/null
```
Expected: `302`, then `>=1`.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(surface): rename TicTacToe.Web.Simple -> TicTacToe.Web.Surface"
```

---

## Task 1: Surface config record + fail-fast parse + DI wiring

**Files:**
- Create: `experiments/src/TicTacToe.Web.Surface/Surface.fs`
- Modify: `experiments/src/TicTacToe.Web.Surface/TicTacToe.Web.Surface.fsproj` (add `Surface.fs` first in compile order)
- Modify: `experiments/src/TicTacToe.Web.Surface/Program.fs` (register `Surface` singleton)
- Create test project: `experiments/test/TicTacToe.Web.Surface.Tests/TicTacToe.Web.Surface.Tests.fsproj`
- Create: `experiments/test/TicTacToe.Web.Surface.Tests/SurfaceParseTests.fs`

**Interfaces:**
- Produces:
  - `type Surface = { A: bool; C: bool; Sd: bool; So: bool }`
  - `Surface.floor : Surface` (all false)
  - `Surface.parse : string -> Surface` (fail-fast)
  - `Surface.fromEnvironment : unit -> Surface`
  - DI: `GetRequiredService<Surface>()` available in every handler.

- [ ] **Step 1: Write the failing parse tests**

Create `experiments/test/TicTacToe.Web.Surface.Tests/SurfaceParseTests.fs`:

```fsharp
module TicTacToe.Web.Surface.Tests.SurfaceParseTests

open Xunit
open TicTacToe.Web.Surface.Surface

[<Fact>]
let ``parse 0000 is the floor`` () =
    Assert.Equal({ A = false; C = false; Sd = false; So = false }, parse "0000")

[<Fact>]
let ``parse 1111 is all factors on`` () =
    Assert.Equal({ A = true; C = true; Sd = true; So = true }, parse "1111")

[<Fact>]
let ``parse 1010 maps positionally to A and Sd`` () =
    Assert.Equal({ A = true; C = false; Sd = true; So = false }, parse "1010")

[<Fact>]
let ``parse rejects wrong length`` () =
    Assert.Throws<exn>(fun () -> parse "101" |> ignore) |> ignore

[<Fact>]
let ``parse rejects non-binary chars`` () =
    Assert.Throws<exn>(fun () -> parse "12ab" |> ignore) |> ignore

[<Fact>]
let ``parse rejects empty`` () =
    Assert.Throws<exn>(fun () -> parse "" |> ignore) |> ignore
```

Create `experiments/test/TicTacToe.Web.Surface.Tests/TicTacToe.Web.Surface.Tests.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="SurfaceParseTests.fs" />
    <Compile Include="RenderFactorTests.fs" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Update="FSharp.Core" Version="10.0.102" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/TicTacToe.Web.Surface/TicTacToe.Web.Surface.fsproj" />
  </ItemGroup>
</Project>
```

> `RenderFactorTests.fs` is created in Task 3. For this task, create it as an empty module so the project compiles:
> ```fsharp
> module TicTacToe.Web.Surface.Tests.RenderFactorTests
> ```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests
```
Expected: FAIL — `Surface` module not found / does not compile.

- [ ] **Step 3: Write Surface.fs**

Create `experiments/src/TicTacToe.Web.Surface/Surface.fs`:

```fsharp
module TicTacToe.Web.Surface.Surface

/// The 2^4 surface-factorial cell. Each flag toggles exactly one discovery
/// factor; the cube is their product. Flag order is A, C, Sd, So.
type Surface =
    { A: bool   // affordances: turn/role-aware available actions
      C: bool   // accessibility: ARIA roles / landmarks / live region
      Sd: bool  // semantic discovery: Link/Allow/OPTIONS, /profile, /.well-known/home
      So: bool } // semantic ontology: JSON-LD schema.org Game typing

/// All factors off — cell 0000, the discovery floor.
let floor = { A = false; C = false; Sd = false; So = false }

/// Parse a 4-char cell flag (order A,C,Sd,So), each char '0' or '1'.
/// Fail-fast (Holzmann R12): malformed input throws — never boot a
/// misconfigured surface.
let parse (raw: string) : Surface =
    if isNull raw then nullArg "raw"
    if raw.Length <> 4 then
        failwithf "TICTACTOE_CELL must be exactly 4 chars (order A,C,Sd,So); got length %d: %s" raw.Length raw
    let bit (i: int) =
        match raw.[i] with
        | '0' -> false
        | '1' -> true
        | c -> failwithf "TICTACTOE_CELL char %d must be '0' or '1'; got '%c'" i c
    { A = bit 0; C = bit 1; Sd = bit 2; So = bit 3 }

/// Read TICTACTOE_CELL once at startup. Unset/empty -> floor (never an
/// accidental rich surface).
let fromEnvironment () : Surface =
    match System.Environment.GetEnvironmentVariable "TICTACTOE_CELL" with
    | null | "" -> floor
    | raw -> parse raw
```

Add to `TicTacToe.Web.Surface.fsproj` as the **first** `<Compile Include>` (before `GameStore.fs`):

```xml
    <Compile Include="Surface.fs" />
    <Compile Include="GameStore.fs" />
```

- [ ] **Step 4: Run the parse tests to verify they pass**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests
```
Expected: PASS (6 parse tests).

- [ ] **Step 5: Register Surface as a DI singleton (fail-fast at startup)**

In `Program.fs`, add the open near the other app opens:
```fsharp
open TicTacToe.Web.Surface.Surface
```
In `configureServices`, add to the singleton chain (parses env at registration → throws at boot on malformed `TICTACTOE_CELL`):
```fsharp
    services
        .AddSingleton<Surface>(Surface.fromEnvironment())
        .AddSingleton<GameStore>(fun _ -> GameStore(?maxGames = maxGames()))
```
(`Surface.fromEnvironment()` is evaluated eagerly here — that is the intended startup parse.)

- [ ] **Step 6: Verify fail-fast boot**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
TICTACTOE_CELL=nope dotnet run --project experiments/src/TicTacToe.Web.Surface --urls http://localhost:5329 2>&1 | grep -m1 -i 'TICTACTOE_CELL'
```
Expected: a line containing the fail-fast message about `TICTACTOE_CELL`. Process must not stay up.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(surface): Surface config record, fail-fast parse, DI wiring + parse tests"
```

---

## Task 2: Thread Surface through renderers; remove OpenAPI (establish parameterized floor)

Plumb `Surface` into the render path and drop `useOpenApi`. No factor behavior yet — `0000` must stay identical to today's Simple (modulo the removed OpenAPI mount).

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/game.fs` (`renderArenaPage` gains `surface` param)
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/home.fs` (`homePage` gains `surface` param)
- Modify: `experiments/src/TicTacToe.Web.Surface/Handlers.fs` (resolve `Surface`, pass to renderers)
- Modify: `experiments/src/TicTacToe.Web.Surface/Program.fs` (remove `useOpenApi` + unused `open Frank.OpenApi`)

**Interfaces:**
- Produces (consumed by Tasks 3,4,6):
  - `renderArenaPage : Surface -> arenaId:string -> result:MoveResult -> userId:string -> assignment:PlayerAssignment option -> errorMsg:string option -> HtmlElement`
  - `homePage : Surface -> HttpContext -> allowCreate:bool -> arenas:(string*MoveResult) list -> HtmlElement`

- [ ] **Step 1: Add `surface` as the first parameter of the renderers**

`templates/game.fs` — change the signature of `renderArenaPage` (add `surface` first), leave the body unchanged for now:
```fsharp
let renderArenaPage (surface: Surface) (arenaId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (errorMsg: string option) =
```
Add the open at the top of `templates/game.fs` (after the existing opens):
```fsharp
open TicTacToe.Web.Surface.Surface
```

`templates/home.fs` — change `homePage` (add `surface` first):
```fsharp
let homePage (surface: Surface) (ctx: HttpContext) (allowCreate: bool) (arenas: (string * MoveResult) list) =
```
Add the open at the top of `templates/home.fs`:
```fsharp
open TicTacToe.Web.Surface.Surface
```

- [ ] **Step 2: Resolve and pass `Surface` from the handlers**

`Handlers.fs` — add the open:
```fsharp
open TicTacToe.Web.Surface.Surface
```
In `home`, resolve and pass surface:
```fsharp
    task {
        let store = ctx.RequestServices.GetRequiredService<GameStore>()
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        let arenas = store.List()
        let allowCreate =
            match store.MaxGames with
            | Some m -> arenas.Length < m
            | None -> true
        let element = homePage surface ctx allowCreate arenas |> layout.html ctx
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        do! Render.toStreamAsync ctx.Response.Body element
    }
```
In `renderArenaHtml`, resolve and pass surface:
```fsharp
let private renderArenaHtml (ctx: HttpContext) (arenaId: string) (result: MoveResult) (errorMsg: string option) =
    let store = ctx.RequestServices.GetRequiredService<GameStore>()
    let surface = ctx.RequestServices.GetRequiredService<Surface>()
    let assignmentManager = ctx.RequestServices.GetRequiredService<PlayerAssignmentManager>()
    let userId = ctx.User.TryGetUserId() |> Option.defaultValue "anonymous"
    let assignment = assignmentManager.GetAssignment(arenaId)
    let element = renderArenaPage surface arenaId result userId assignment errorMsg |> layout.html ctx
    ctx.Response.ContentType <- "text/html; charset=utf-8"
    Render.toStreamAsync ctx.Response.Body element
```
(`store` is now unused in `renderArenaHtml` — remove that line if the compiler warns.)

- [ ] **Step 3: Remove the OpenAPI mount**

`Program.fs` — delete the line `useOpenApi` (currently line 149) and the now-unused `open Frank.OpenApi` (line 11). Leave the `Frank.OpenApi` PackageReference in the `.fsproj` (the V_swagger bracket reuses it in a later SP).

- [ ] **Step 4: Build**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet build experiments/src/TicTacToe.Web.Surface
```
Expected: `Build succeeded`.

- [ ] **Step 5: Verify the floor still renders all 9 move forms**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 TICTACTOE_INITIAL_GAMES=1 \
  dotnet run --project experiments/src/TicTacToe.Web.Surface --urls http://localhost:5328 &
sleep 6
curl -s -c /tmp/sp2.jar http://localhost:5328/login >/dev/null
AID=$(curl -s -b /tmp/sp2.jar http://localhost:5328/ | grep -oE '/arenas/[a-f0-9-]+' | head -1)
curl -s -b /tmp/sp2.jar "http://localhost:5328$AID" | grep -c 'name="position"'   # expect 9
curl -s -b /tmp/sp2.jar "http://localhost:5328$AID" | grep -c 'class="status"'    # expect 1 (status line kept)
kill %1 2>/dev/null
```
Expected: `9` then `1`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(surface): thread Surface into renderers; remove OpenAPI mount"
```

---

## Task 3: Factor A — turn/role-aware action availability

A1 serves move forms **only** for the requesting agent's currently-legal moves; the non-turn player and observers get none. A0 is unchanged (all 9 cells as forms, occupied disabled). Status line stays in both.

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/game.fs`
- Modify: `experiments/test/TicTacToe.Web.Surface.Tests/RenderFactorTests.fs`

**Interfaces:**
- Consumes: `renderArenaPage : Surface -> …` (Task 2); Engine `MoveResult` (`XTurn of GameState * ValidMovesForX`, `OTurn of GameState * ValidMovesForO`), `XPosition = XPos of SquarePosition`, `OPosition = OPos of SquarePosition`, `ValidMovesForX = XPosition[]`.

- [ ] **Step 1: Write the failing A-factor render tests**

Replace the placeholder `RenderFactorTests.fs` with:

```fsharp
module TicTacToe.Web.Surface.Tests.RenderFactorTests

open System.IO
open Xunit
open Oxpecker.ViewEngine
open TicTacToe.Model
open TicTacToe.Engine
open TicTacToe.Web.Surface.Surface
open TicTacToe.Web.Surface.Model
open TicTacToe.Web.Surface.templates.game

let private html (el: HtmlElement) =
    use sw = new StringWriter()
    (Render.toTextWriterAsync sw el).GetAwaiter().GetResult()
    sw.ToString()

// A fresh game is X's turn. Seat X as "userX", O as "userO".
let private freshXTurn () : MoveResult = TicTacToe.Engine.Game.startGame ()
let private seated =
    Some { GameId = "g"; PlayerXId = Some "userX"; PlayerOId = Some "userO" }

let private cell a c sd so = { A = a; C = c; Sd = sd; So = so }

[<Fact>]
let ``A0 renders nine move forms regardless of caller`` () =
    let out = renderArenaPage (cell false false false false) "g" (freshXTurn()) "observer" seated None |> html
    Assert.Equal(9, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``A1 gives the on-turn player their legal move forms`` () =
    let out = renderArenaPage (cell true false false false) "g" (freshXTurn()) "userX" seated None |> html
    // X to move on an empty board => 9 legal squares => 9 forms
    Assert.Equal(9, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``A1 gives the off-turn player no move forms`` () =
    let out = renderArenaPage (cell true false false false) "g" (freshXTurn()) "userO" seated None |> html
    Assert.Equal(0, (out.Split("name=\"position\"").Length - 1))

[<Fact>]
let ``A1 gives an observer no move forms`` () =
    let out = renderArenaPage (cell true false false false) "g" (freshXTurn()) "observer" seated None |> html
    Assert.Equal(0, (out.Split("name=\"position\"").Length - 1))
```

> **Verify before relying on it:** the Engine entry point for a fresh game. Find the actual start function:
> ```bash
> grep -rn 'startGame\|StartGame\|let startGame\|member.*Start' src/TicTacToe.Engine/*.fs | head
> ```
> Use whatever returns the initial `XTurn` `MoveResult`. If it is a member on a type rather than `Game.startGame`, adjust `freshXTurn` accordingly. Add the matching `ProjectReference` to `TicTacToe.Engine` in the test `.fsproj` if the start function lives there (the Surface app already references it transitively, but the test project needs a direct reference to call Engine functions — add `<ProjectReference Include="../../../src/TicTacToe.Engine/TicTacToe.Engine.fsproj" />`).

- [ ] **Step 2: Run the A tests to verify they fail**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests --filter RenderFactorTests
```
Expected: the off-turn and observer tests FAIL (A0 path always renders 9 forms).

- [ ] **Step 3: Implement A gating in game.fs**

Add helpers above `renderSquare`:

```fsharp
/// The caller's seat in this arena, if any.
let private callerRole (assignment: PlayerAssignment option) (userId: string) =
    match assignment with
    | Some { PlayerXId = Some x } when x = userId -> Some X
    | Some { PlayerOId = Some o } when o = userId -> Some O
    | _ -> None

/// Squares the caller may legally move into right now (empty unless it is the
/// caller's turn). Reads the legal-move list the Engine already computed.
let private legalForCaller (result: MoveResult) (role: Player option) : Set<SquarePosition> =
    match result, role with
    | XTurn(_, vx), Some X -> vx |> Array.map (fun (XPos p) -> p) |> Set.ofArray
    | OTurn(_, vo), Some O -> vo |> Array.map (fun (OPos p) -> p) |> Set.ofArray
    | _ -> Set.empty

/// A1 non-affordance cell: plain, no form.
let private renderPlainCell (label: string) =
    button(class' = "square", type' = "button", ariaLabel = "").attr("disabled", "disabled") { label }
    :> HtmlElement
```

Change `renderSquare` to branch on `surface.A`:

```fsharp
let private renderSquare (surface: Surface) (legal: Set<SquarePosition>) (arenaId: string) (playerStr: string) (state: GameState) (isActive: bool) (position: SquarePosition) =
    let posStr = position.ToString()
    let isTaken, label =
        match state.TryGetValue(position) with
        | true, Taken X -> true, "X"
        | true, Taken O -> true, "O"
        | _ -> false, "·"
    if surface.A then
        if Set.contains position legal then
            form (method = "post", action = $"/arenas/{arenaId}") {
                input (type' = "hidden", name = "player", value = playerStr)
                input (type' = "hidden", name = "position", value = posStr)
                button (class' = "square square-clickable", type' = "submit", ariaLabel = posStr) { label }
            } :> HtmlElement
        else
            renderPlainCell label
    else
        let clickable = isActive && not isTaken
        let square =
            if clickable then
                button (class' = "square square-clickable", type' = "submit", ariaLabel = posStr) { label }
            else
                button(class' = "square", type' = "submit", ariaLabel = posStr).attr("disabled", "disabled") { label }
        form (method = "post", action = $"/arenas/{arenaId}") {
            input (type' = "hidden", name = "player", value = playerStr)
            input (type' = "hidden", name = "position", value = posStr)
            square
        } :> HtmlElement
```

In `renderArenaPage`, compute `role`/`legal` and pass `surface`+`legal` to each square:

```fsharp
let renderArenaPage (surface: Surface) (arenaId: string) (result: MoveResult) (userId: string) (assignment: PlayerAssignment option) (errorMsg: string option) =
    let (State state) = result
    let status = statusText result
    let active = isInProgress result
    let playerStr = resolvePlayerStr assignment userId result
    let role = callerRole assignment userId
    let legal = legalForCaller result role
    // … unchanged Fragment/style/header …
        div (class' = "board") {
            for position in allPositions do
                renderSquare surface legal arenaId playerStr state active position
        }
    // … unchanged status/legend/controls/back-link …
```

> Note: under A1, observers/off-turn callers get an empty `legal` set, so `resolvePlayerStr`'s spectator-defaults-to-"X" branch is unreachable for them (no form is emitted). A1 correctness comes from legal-set gating, not from `playerStr`. Leave `resolvePlayerStr` unchanged.

- [ ] **Step 4: Run the A tests to verify they pass**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests --filter RenderFactorTests
```
Expected: PASS (4 A tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(surface): factor A — turn/role-aware action availability"
```

---

## Task 4: Factor C — accessibility (ARIA roles, live region, nav landmark)

C1 adds, with no layout/markup-structure change beyond attributes/landmarks: `role="grid"` on the board, `role="gridcell"` + enriched `aria-label` per square, `aria-live="polite"` on the status region, and a `<nav>` landmark around the back-link. C0 emits none of these (the pre-existing bare position `aria-label` on buttons stays — it is part of the floor).

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/game.fs`
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/home.fs`
- Modify: `experiments/test/TicTacToe.Web.Surface.Tests/RenderFactorTests.fs`

**Interfaces:** consumes `renderArenaPage`/`homePage` (Tasks 2–3). No new exports.

- [ ] **Step 1: Add the failing C tests**

Append to `RenderFactorTests.fs`:

```fsharp
[<Fact>]
let ``C0 emits no grid role and no live region`` () =
    let out = renderArenaPage (cell false false false false) "g" (freshXTurn()) "userX" seated None |> html
    Assert.DoesNotContain("role=\"grid\"", out)
    Assert.DoesNotContain("aria-live", out)

[<Fact>]
let ``C1 emits grid role and a polite live region`` () =
    let out = renderArenaPage (cell false true false false) "g" (freshXTurn()) "userX" seated None |> html
    Assert.Contains("role=\"grid\"", out)
    Assert.Contains("role=\"gridcell\"", out)
    Assert.Contains("aria-live=\"polite\"", out)
```

- [ ] **Step 2: Run to verify failure**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests --filter RenderFactorTests
```
Expected: the two C tests FAIL.

- [ ] **Step 3: Implement C in game.fs**

The board `div` and status `div` gain conditional attributes; squares gain `role="gridcell"` and an enriched label under C1. Use `.attr(...)` (deterministic across the Oxpecker version) and apply C inside `renderSquare` and `renderArenaPage`.

In `renderSquare`, give the clickable/plain buttons the gridcell role and richer label **only when `surface.C`**. Add a helper:

```fsharp
let private occupancyLabel (posStr: string) (isTaken: bool) (label: string) =
    if isTaken then $"{posStr}, {label}" else $"{posStr}, empty"

let private applyCell (surface: Surface) (posStr: string) (isTaken: bool) (label: string) (b: HtmlElement) =
    if surface.C then
        b.attr("role", "gridcell").attr("aria-label", occupancyLabel posStr isTaken label)
    else b
```

Wrap each `button (...)` in `renderSquare` with `applyCell surface posStr isTaken label (…)` before returning it inside the form/cell. (Apply to all three button constructions: A1-clickable, A0-clickable, A0-disabled, and the A1 plain cell.)

In `renderArenaPage`, make the board and status regions conditional:

```fsharp
        (div (class' = "board") {
            for position in allPositions do
                renderSquare surface legal arenaId playerStr state active position
         } |> fun d -> if surface.C then d.attr("role", "grid") else d)

        (div (class' = "status") { status }
         |> fun d -> if surface.C then d.attr("aria-live", "polite") else d)
```

Wrap the back-link in a `<nav>` under C1:

```fsharp
        if surface.C then
            nav () { a (class' = "back-link", href = "/") { "Back to game list" } }
        else
            a (class' = "back-link", href = "/") { "Back to game list" }
```

In `templates/home.fs`, add the `<nav>` landmark + `role="list"` on the arena list under C1 (the lobby's C payload). Wrap the existing back-affordances/list similarly:

```fsharp
        (ul (class' = "arenas-list") { /* unchanged children */ }
         |> fun u -> if surface.C then u.attr("role", "list") else u)
```

> Keep all C changes attribute-only — do not restructure existing elements (criterion: "no layout/markup-structure change beyond attributes/landmarks"). The `<main>` landmark already exists in `templates/shared/layout.fs`; do not duplicate it.

- [ ] **Step 4: Run to verify pass**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests --filter RenderFactorTests
```
Expected: PASS (A + C tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(surface): factor C — ARIA roles, live region, nav landmark"
```

---

## Task 5: Factor Sd — semantic discovery (Link/Allow headers, OPTIONS, /profile, /.well-known/home)

Sd1 advertises app navigation: `Link` headers (`rel="profile"`, `rel="self"`) and a state-dependent `Allow` header on arena responses; an `OPTIONS` handler per resource; `GET /profile` (ALPS); `GET /.well-known/home` (JSON Home). Sd0 emits none and the two routes 404.

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Surface/Handlers.fs` (Sd headers; profile + home-doc handlers)
- Modify: `experiments/src/TicTacToe.Web.Surface/Program.fs` (new resources + OPTIONS middleware)
- Modify: `experiments/test/TicTacToe.Web.Surface.Tests/RenderFactorTests.fs` is **not** used here (header/route behavior is HTTP-level) — verify via curl steps below instead.

**Interfaces:**
- Produces: `Handlers.profile : HttpContext -> Task`, `Handlers.wellKnownHome : HttpContext -> Task`, and a `useOptionsDiscovery : IApplicationBuilder -> IApplicationBuilder` plug.

- [ ] **Step 1: Add the Allow + Link header helpers and the discovery doc handlers**

In `Handlers.fs`, add helpers (place after the `sessionId`/`requestId` privates):

```fsharp
/// State-dependent Allow for an arena resource.
let private arenaAllow (result: MoveResult) =
    match result with
    | XTurn _ | OTurn _ -> "GET, POST, DELETE, OPTIONS"
    | _ -> "GET, DELETE, OPTIONS"   // terminal: no further moves

let private setDiscoveryHeaders (ctx: HttpContext) (selfPath: string) (allow: string option) =
    ctx.Response.Headers.Append("Link", $"</profile>; rel=\"profile\", <{selfPath}>; rel=\"self\"")
    match allow with
    | Some a -> ctx.Response.Headers.Append("Allow", a)
    | None -> ()
```

In `home`, when `surface.Sd`, set discovery headers before writing the body:
```fsharp
        if surface.Sd then setDiscoveryHeaders ctx "/" None
```
In `getArena`, when Sd, set headers with state-dependent Allow (the `result` is in hand):
```fsharp
        | Some result ->
            let surface = ctx.RequestServices.GetRequiredService<Surface>()
            if surface.Sd then setDiscoveryHeaders ctx $"/arenas/{arenaId}" (Some (arenaAllow result))
            do! renderArenaHtml ctx arenaId result None
```

Add the two discovery-doc handlers at the end of `Handlers.fs`:

```fsharp
/// GET /profile — ALPS profile of the app's affordances (Sd only).
let profile (ctx: HttpContext) =
    task {
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.Sd then
            ctx.Response.StatusCode <- 404
        else
            ctx.Response.ContentType <- "application/alps+json; charset=utf-8"
            do! ctx.Response.WriteAsync TicTacToe.Web.Surface.Discovery.alpsProfile
    }

/// GET /.well-known/home — JSON Home document (Sd only).
let wellKnownHome (ctx: HttpContext) =
    task {
        let surface = ctx.RequestServices.GetRequiredService<Surface>()
        if not surface.Sd then
            ctx.Response.StatusCode <- 404
        else
            ctx.Response.ContentType <- "application/json-home; charset=utf-8"
            do! ctx.Response.WriteAsync TicTacToe.Web.Surface.Discovery.jsonHome
    }
```

- [ ] **Step 2: Create the discovery documents module**

Create `experiments/src/TicTacToe.Web.Surface/Discovery.fs` (add to `.fsproj` compile order **before** `Handlers.fs`):

```fsharp
module TicTacToe.Web.Surface.Discovery

/// ALPS profile describing the app's affordances and their semantics.
let alpsProfile = """{
  "alps": {
    "version": "1.0",
    "doc": { "value": "Tic-tac-toe arena. m,n,k-game (3,3,3)." },
    "descriptor": [
      { "id": "take-seat", "type": "unsafe", "doc": { "value": "Claim the X or O seat by submitting a move; first mover on each side is seated." } },
      { "id": "make-move", "type": "unsafe", "rt": "#arena", "doc": { "value": "POST player + position to /arenas/{id}; rejected if out of turn or square taken." } },
      { "id": "restart", "type": "idempotent", "doc": { "value": "POST /arenas/{id}/restart to reset the board and clear seats." } },
      { "id": "delete", "type": "idempotent", "doc": { "value": "DELETE /arenas/{id} (or POST /arenas/{id}/delete) to remove the arena." } },
      { "id": "arena", "type": "semantic", "doc": { "value": "An arena resource: the board state plus whose turn it is." } }
    ]
  }
}"""

/// JSON Home document listing resources and relations.
let jsonHome = """{
  "resources": {
    "tag:tictactoe,2026:home": { "href": "/" },
    "tag:tictactoe,2026:arena": { "href-template": "/arenas/{id}", "href-vars": { "id": "tag:tictactoe,2026:param;id" } },
    "tag:tictactoe,2026:profile": { "href": "/profile" }
  }
}"""
```

- [ ] **Step 3: Register the resources and the OPTIONS middleware in Program.fs**

Add resources (both behind `requireAuth`, consistent with the rest of the app — the agent already holds the seed cookie; this avoids a per-cell auth-policy confound):

```fsharp
let profileResource =
    resource "/profile" {
        name "Profile"
        requireAuth
        get Handlers.profile
    }

let wellKnownHomeResource =
    resource "/.well-known/home" {
        name "WellKnownHome"
        requireAuth
        get Handlers.wellKnownHome
    }
```

Add a terminal OPTIONS plug (resolves `Surface` from services; static per-path Allow):

```fsharp
let private optionsAllow (path: string) =
    match path with
    | "/" -> Some "GET, OPTIONS"
    | "/arenas" -> Some "POST, OPTIONS"
    | "/profile" | "/.well-known/home" -> Some "GET, OPTIONS"
    | p when p.StartsWith "/arenas/" -> Some "GET, POST, DELETE, OPTIONS"
    | _ -> None

let useOptionsDiscovery (app: IApplicationBuilder) =
    let surface = app.ApplicationServices.GetRequiredService<Surface>()
    if not surface.Sd then app
    else
        app.Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
            task {
                if ctx.Request.Method = "OPTIONS" then
                    match optionsAllow ctx.Request.Path.Value with
                    | Some allow ->
                        ctx.Response.StatusCode <- 204
                        ctx.Response.Headers.Append("Allow", allow)
                    | None -> do! next.Invoke ctx
                else
                    do! next.Invoke ctx
            } :> Task)
```

Wire it in the `webHost` CE alongside the other `plugBeforeRouting` calls:
```fsharp
        plugBeforeRouting useOptionsDiscovery
```
And register the two resources with the others:
```fsharp
        resource profileResource
        resource wellKnownHomeResource
```
Add any missing opens to `Program.fs`: `open System.Threading.Tasks`, `open Microsoft.AspNetCore.Http` (already present), `open Microsoft.Extensions.DependencyInjection` (already present).

- [ ] **Step 4: Build**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet build experiments/src/TicTacToe.Web.Surface
```
Expected: `Build succeeded`.

- [ ] **Step 5: Verify Sd present-iff-flag at the HTTP layer**

Run (Sd ON via `0010`, then floor `0000`):
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
run() { DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 TICTACTOE_CELL=$1 TICTACTOE_INITIAL_GAMES=1 \
  dotnet run --project experiments/src/TicTacToe.Web.Surface --urls http://localhost:5328 & sleep 6; }
stop() { kill %1 2>/dev/null; sleep 1; }

run 0010
curl -s -c /tmp/sp2.jar http://localhost:5328/login >/dev/null
curl -s -o /dev/null -w 'profile=%{http_code}\n' -b /tmp/sp2.jar http://localhost:5328/profile   # expect 200
curl -s -o /dev/null -w 'home=%{http_code}\n'    -b /tmp/sp2.jar http://localhost:5328/.well-known/home  # expect 200
AID=$(curl -s -b /tmp/sp2.jar http://localhost:5328/ | grep -oE '/arenas/[a-f0-9-]+' | head -1)
curl -s -D - -o /dev/null -b /tmp/sp2.jar "http://localhost:5328$AID" | grep -iE 'link:|allow:'  # expect Link + Allow
curl -s -D - -o /dev/null -X OPTIONS -b /tmp/sp2.jar "http://localhost:5328$AID" | grep -iE 'allow:'  # expect Allow
stop

run 0000
curl -s -c /tmp/sp2.jar http://localhost:5328/login >/dev/null
curl -s -o /dev/null -w 'profile=%{http_code}\n' -b /tmp/sp2.jar http://localhost:5328/profile  # expect 404
AID=$(curl -s -b /tmp/sp2.jar http://localhost:5328/ | grep -oE '/arenas/[a-f0-9-]+' | head -1)
curl -s -D - -o /dev/null -b /tmp/sp2.jar "http://localhost:5328$AID" | grep -ic 'link:'  # expect 0
stop
```
Expected: `0010` → `profile=200`, `home=200`, `Link`+`Allow` present, OPTIONS `Allow` present; `0000` → `profile=404`, link count `0`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(surface): factor Sd — Link/Allow headers, OPTIONS, /profile, /.well-known/home"
```

---

## Task 6: Factor So — semantic ontology (JSON-LD schema.org Game block)

So1 embeds a `<script type="application/ld+json">` block in the arena body: schema.org `Game` typing, m,n,k classification `(3,3,3)`, sibling-game relations, and the load-bearing strategy annotation ("perfect play is a known draw; solved"). So0 emits none. **Excluded by design (not deferred):** SHACL shapes, PROV-O provenance.

**Files:**
- Modify: `experiments/src/TicTacToe.Web.Surface/templates/game.fs`
- Modify: `experiments/test/TicTacToe.Web.Surface.Tests/RenderFactorTests.fs`

**Interfaces:** consumes `renderArenaPage` (Tasks 2–4). No new exports.

- [ ] **Step 1: Add the failing So tests**

Append to `RenderFactorTests.fs`:

```fsharp
[<Fact>]
let ``So0 emits no JSON-LD`` () =
    let out = renderArenaPage (cell false false false false) "g" (freshXTurn()) "userX" seated None |> html
    Assert.DoesNotContain("application/ld+json", out)

[<Fact>]
let ``So1 emits a schema.org Game JSON-LD block with mnk and strategy`` () =
    let out = renderArenaPage (cell false false false true) "g" (freshXTurn()) "userX" seated None |> html
    Assert.Contains("application/ld+json", out)
    Assert.Contains("schema.org", out)
    Assert.Contains("(3,3,3)", out)
    Assert.Contains("draw", out)
```

- [ ] **Step 2: Run to verify failure**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests --filter RenderFactorTests
```
Expected: the two So tests FAIL.

- [ ] **Step 3: Implement So in game.fs**

Add a JSON-LD fragment builder above `renderArenaPage`:

```fsharp
/// Domain ontology block, trimmed to what a cold-start LLM can actually use.
let private renderOntology () =
    script (type' = "application/ld+json") {
        raw """{
  "@context": "https://schema.org",
  "@type": "Game",
  "name": "Tic-tac-toe",
  "description": "An m,n,k-game with parameters (3,3,3): 3x3 board, 3-in-a-row to win.",
  "genre": "abstract strategy",
  "isBasedOn": [
    { "@type": "Game", "name": "Connect Four", "description": "m,n,k = (7,6,4)" },
    { "@type": "Game", "name": "Gomoku", "description": "m,n,k = (15,15,5)" }
  ],
  "strategy": "Solved: with perfect play tic-tac-toe is a draw; optimal play never loses."
}"""
    }
    :> HtmlElement
```

In `renderArenaPage`, emit it inside the `Fragment()` when `surface.So` (place after the `style ()` block, before the header):

```fsharp
        if surface.So then renderOntology () else Fragment() { }
```

- [ ] **Step 4: Run to verify pass**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet test experiments/test/TicTacToe.Web.Surface.Tests --filter RenderFactorTests
```
Expected: PASS (A + C + So tests).

- [ ] **Step 5: Full unit-test + build gate**

Run:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
dotnet build experiments/src/TicTacToe.Web.Surface
dotnet test experiments/test/TicTacToe.Web.Surface.Tests
```
Expected: build succeeds; all parse + render tests PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(surface): factor So — schema.org Game JSON-LD ontology block"
```

---

## Task 7: Live e2e on a non-floor cell (self-verified)

Validate one real agent run against a non-floor cell. This is the SP's real validation — not subagent-reported.

**Files:** none (verification only). Uses `experiments/discovery-harness` (driver hits `--base`) with a manually-launched Surface server, per `experiments/test/CLAUDE.md` and `experiments/CLAUDE.md`.

- [ ] **Step 1: Confirm criteria #2 and #3 by curl**

Run the `0000` floor and `1111` full cell and confirm the surface flips wholesale:
```bash
cd /Users/ryanr/Code/tic-tac-toe/.claude/worktrees/discovery-reset
# 1111: expect role="grid", aria-live, application/ld+json, Link header, /profile 200
# 0000: expect none of the above, /profile 404, 9 move forms
```
(Reuse the curl harness from Task 5 Step 5, adding greps for `role="grid"`, `application/ld+json`.)

- [ ] **Step 2: Run one live agent game on a non-floor cell**

Launch the Surface binary with a non-floor cell (e.g. `TICTACTOE_CELL=1111`) and drive one full game with the Haiku-4.5 cold-start harness over OpenRouter, following `experiments/test/CLAUDE.md` for the server lifecycle and the discovery-harness CLI (`--base`) for the driver. Capture the transcript.

- [ ] **Step 3: Self-verify the transcript shows the new surface**

Read the transcript yourself. Confirm: the agent observed the richer surface (e.g. discovery headers / ALPS / JSON-LD as appropriate to the cell) and the game completed (terminal state, no harness error). Do **not** rely on a subagent's summary.

- [ ] **Step 4: Run `/verification-before-completion`**

This is a large change set — run the skill, confirm all success criteria below with observed evidence, then stop.

---

## Success criteria (from spec §9 — verify all before claiming done)

1. `dotnet build experiments/src/TicTacToe.Web.Surface` succeeds; harness wiring updated for the rename (`arena.sh`, test path strings).
2. `TICTACTOE_CELL=0000` reproduces today's Simple floor (9 move forms, status line, no ARIA grid / Link / JSON-LD), modulo the removed OpenAPI mount.
3. `TICTACTOE_CELL=1111` serves A1 role/turn-aware actions, C ARIA, Sd headers + `/profile` + `/.well-known/home`, So JSON-LD.
4. Each of A/C/Sd/So toggled alone flips exactly its own payload and nothing else (the present-iff-flag unit/curl tests).
5. Invalid `TICTACTOE_CELL` fails fast at startup.
6. One live e2e run on a non-floor cell completes and the transcript shows the new surface (self-verified).

---

## Self-review (completed during planning)

- **Spec coverage:** §2 (rename, Surface record, 4 conditionals, OpenAPI removal) → Tasks 0–2,6. §3 (config, fail-fast, one-process-per-cell) → Task 1. §4 factors A/C/Sd/So → Tasks 3/4/5/6. §5 code structure (Surface.fs, renderer branching, OPTIONS, /profile, /.well-known/home, remove useOpenApi) → Tasks 1,2,5. §6 OpenAPI boundary → Task 2. §8 testing (parse + present-iff) → Tasks 1,3,4,5,6. §9 criteria → final section. §7 deferred items (MCP co-mount, repo extraction, seed back-off) intentionally **not** built — confirmed with user (single-domain → defer extraction).
- **Placeholder scan:** every code step contains concrete code; the only "verify-before-relying" note (Task 3 Engine start function) is an explicit grep-to-confirm step, not a placeholder.
- **Type consistency:** `Surface`/`Surface.parse`/`Surface.floor`/`Surface.fromEnvironment`, `renderArenaPage surface …`, `homePage surface …`, `Handlers.profile`/`Handlers.wellKnownHome`, `Discovery.alpsProfile`/`Discovery.jsonHome`, `useOptionsDiscovery` are used identically across tasks.
- **Open risk flagged for the implementer:** the Engine fresh-game entry point used in tests (Task 3 Step 1) must be confirmed by grep; adjust `freshXTurn` and the test project's `TicTacToe.Engine` ProjectReference to match.
