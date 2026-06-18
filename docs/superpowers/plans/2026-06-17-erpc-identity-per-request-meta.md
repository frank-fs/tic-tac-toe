# ERPC Identity — per-request `_meta` (Revision A) Implementation Plan

> Continuation of `2026-06-16-erpc-identity-handshake.md`. Same branch `erpc-identity-handshake`. Execute task-by-task with adversarial spec + quality review after each.

**Goal:** Make ERPC identity work over the SHARED stdio connection the orchestrator uses for all agents, by carrying identity per-request in `_meta` (orchestrator-injected), instead of per-connection.

**Why:** Verified — `Orchestrator.fs:176-204` shares ONE ERPC process across all 3 agents (so they share game state). Per-connection `SessionIdentity` cannot distinguish them. MCP stdio is 1:1; HTTP-style per-request identity is the faithful analog.

**Reuse:** `PlayerAssignmentStore`, `resolveMove`, `make_move(ClaimsPrincipal, gameId, position)`, instance/DI tools all stand. Net change: drop `SessionIdentity`; filter reads `_meta`; `authenticate` stateless; orchestrator injects `_meta` per agent.

---

## Task 7: Server — read identity from request `_meta`, drop SessionIdentity

**Files:** `experiments/mcp-rpc/Identity.fs`, `experiments/mcp-rpc/Program.fs`, `experiments/mcp-rpc/Tools.fs`, `test/TicTacToe.McpRpc.Tests/IdentityTests.fs`, `experiments/mcp-rpc/smoke_roundtrip.py`

- [ ] **Step 1: Remove `SessionIdentity` from `Identity.fs`.** Delete the `SessionIdentity` type entirely. Everything else (RejectionReason, MoveValidationResult, Assignment, StoreMessage, PlayerAssignmentStore, MoveOutcome, helpers, resolveMove) stays.

- [ ] **Step 2: `authenticate` becomes a stateless minter in `Tools.fs`.** The `TicTacToeTools` constructor drops the `session: SessionIdentity` parameter → `TicTacToeTools(supervisor: GameSupervisor, assignments: PlayerAssignmentStore)`. `authenticate()` returns a fresh GUID token directly:
```fsharp
member _.authenticate() : AuthResponse =
    { token = System.Guid.NewGuid().ToString("N") }
```
`make_move`/`new_game`/`get_board`/`get_state` unchanged (make_move still reads `user: ClaimsPrincipal`). Remove the `open ...Identity` reference to SessionIdentity only if it errors (the module open stays for the other types).

- [ ] **Step 3: Rewrite the Program.fs message filter to read `_meta.identityToken`.** Replace the `SessionIdentity`-based filter body. The filter must: cast `context.JsonRpcMessage` to `JsonRpcRequest`; read `request.Params` → the `_meta` object → `identityToken` string; if present, set `context.User <- ClaimsPrincipal(ClaimsIdentity([Claim(ClaimTypes.Name, token)], "MetaAuth", ClaimTypes.Name, ClaimTypes.Role))`; else set an empty `ClaimsPrincipal(ClaimsIdentity())` (preserve the always-set-a-principal invariant so unauthenticated → clean `unauthenticated`, not a framework throw). Always call `next`. Remove the `SessionIdentity` singleton registration; keep `GameSupervisor` + `PlayerAssignmentStore` singletons.
  - The exact shape of `JsonRpcRequest.Params` and how `_meta` is exposed (it may be `request.Params` as a `JsonNode`/`JsonElement` with a `_meta` property, or a typed `RequestParams.Meta`) MUST be confirmed against the installed `ModelContextProtocol` 1.2.0 assembly by reflection (as was done for the filter API previously). Read it from wherever the SDK puts incoming request metadata. If you cannot locate per-request `_meta` on the incoming `JsonRpcRequest`, STOP and report BLOCKED with the real `JsonRpcRequest`/params API you found — do not fake it.

- [ ] **Step 4: Update unit tests.** In `IdentityTests.fs`, DELETE the 3 `SessionIdentity` tests (the type is gone). Add ONE test that `TicTacToeTools(...).authenticate()` returns a non-empty token and two calls return distinct tokens (stateless minter). PlayerAssignmentStore tests unchanged. In `ResolveMoveTests.fs`, the `toolsTests` block constructs `TicTacToeTools(sup, SessionIdentity(), store)` — update those constructions to the new 2-arg ctor `TicTacToeTools(sup, store)`. The make_move tests pass a `ClaimsPrincipal` directly (unchanged behavior).

- [ ] **Step 5: Rewrite `smoke_roundtrip.py` for per-request `_meta` + multi-identity.** The smoke now drives ONE connection and sets `_meta.identityToken` per `tools/call` request (the orchestrator's role, simulated). Cases, all on ONE server process / ONE connection:
  - authenticate (×2, no identity needed) → token A, token B.
  - new_game → gameId.
  - make_move with `_meta.identityToken=A`, position TopLeft → success, board[0]="X" (A binds X).
  - make_move with `_meta.identityToken=B`, position TopCenter → success, board[1]="O" (B binds O, turn alternated).
  - make_move with `_meta.identityToken=A`, position MiddleCenter while it's X's turn again → success (proves A is still X, identity stable per-request).
  - make_move with NO `_meta` identity → clean `{"error":"unauthenticated"}`.
  - (optional) a third token C make_move → `game_full`.
  Build the JSON-RPC `tools/call` params as `{"name": ..., "arguments": {...}, "_meta": {"identityToken": "<tok>"}}`. Assert each outcome; fail loudly on mismatch. This proves per-request identity separation on a shared connection — the thesis.

- [ ] **Step 6: Verify.** `dotnet build -c Release experiments/mcp-rpc/TicTacToe.McpRpc.fsproj` (0 errors); `dotnet test test/TicTacToe.McpRpc.Tests/` (all pass); `cd experiments/mcp-rpc && uv run python smoke_roundtrip.py` (all cases pass — paste output). Confirm `make_move` schema still only `gameId`+`position`.

- [ ] **Step 7: Commit** `git commit -am "feat(erpc): per-request identity via _meta; drop SessionIdentity; stateless authenticate (#67)"`

---

## Task 8: Orchestrator — per-agent authenticate + `_meta` injection

**Files:** `experiments/orchestrator/McpClient.fs`, `experiments/orchestrator/Agent.fs`, `experiments/orchestrator/Orchestrator.fs` (+ `Types.fs` if a token field is needed)

Goal: each ERPC agent's tool calls carry that agent's identity token in `_meta`, transparently (the LLM never sees it). The 3 agents share one connection but are distinguished per request.

- [ ] **Step 1: Identity-scoped tool calls in `McpClient.fs`.** `McpConnection` currently stores only converted `ToolDef`s and calls `client.CallToolAsync(name, args, ct)`. To attach `_meta`, keep the raw `McpClientTool` instances and route identity-bearing calls through `McpClientTool.WithMeta(JsonObject)` then invoke. Add a path: `CallToolAsync(name, args, identityToken: string option, ct)` — when `identityToken` is Some, build `JsonObject(["identityToken", JsonValue.Create token])`, get the raw `McpClientTool`, `tool.WithMeta(meta)`, and invoke it (confirm the invoke API on `McpClientTool` — `.CallAsync(args, ct)` or via the client — by reflection against ModelContextProtocol 1.2.0). When None, current behavior. `McpClientSet.CallToolAsync` gains the same optional `identityToken`. Keep the existing no-identity overload working for non-ERPC arms.
  - If the exact `WithMeta`/invoke API can't be made to send `_meta`, STOP and report BLOCKED with the real `McpClientTool` API surface — do not silently fall back to a tool-arg (that would be the rejected option B).

- [ ] **Step 2: Per-agent token in `Agent.fs`.** `AgentConfig` (in Types.fs) gains an optional `IdentityToken: string option`. In `executeTurn`, the `mcpClients.CallToolAsync(call.Name, args, ...)` call passes `config.IdentityToken`. The LLM-generated args are unchanged (no token in args).

- [ ] **Step 3: Orchestrator wires the handshake (`Orchestrator.fs`).** In the ERPC branch (where `sharedClientsOpt` is built and the game is pre-created), additionally: for each of the 3 agent slots, call `authenticate()` via the shared client to mint a token, and thread that token into the agent's `AgentConfig.IdentityToken` (via `makeAgentConfig`). So each agent gets a distinct server-issued identity. Non-ERPC arms pass `None`.
  - Note the existing comment at `Orchestrator.fs:176` ("one shared MCP server process for all 3 agents") stays TRUE and correct; update it to also note identities are now per-request via `_meta`.

- [ ] **Step 4: Build + existing orchestrator tests.** `dotnet build experiments/orchestrator/TicTacToe.Orchestrator.fsproj` (0 errors). `dotnet test test/TicTacToe.Orchestrator.Tests/` — report counts; if pre-existing failures unrelated to identity exist, note them, don't fix. The identity wiring must not regress them.

- [ ] **Step 5: Commit** `git commit -am "feat(orchestrator): per-agent authenticate + _meta identity injection for ERPC arm (#67)"`

---

## Task 9: Close-out — multi-agent E2E + full build + whole-branch adversarial review

- [ ] **Step 1:** Full solution build `dotnet build TicTacToe.sln` (0 errors; pre-existing warnings OK).
- [ ] **Step 2:** Run `dotnet test test/TicTacToe.Engine.Tests/`, `dotnet test test/TicTacToe.McpRpc.Tests/`, `dotnet test test/TicTacToe.Orchestrator.Tests/` — report all counts.
- [ ] **Step 3:** Re-run the multi-identity `smoke_roundtrip.py` — paste output proving distinct per-request identities bind to distinct seats on one connection.
- [ ] **Step 4:** Whole-branch adversarial review (`f0e88e9..HEAD`): identity is per-request (no SessionIdentity), orchestrator injects `_meta` per agent transparently (no token in LLM-facing args/schema), multi-agent separation proven, scope = only experiments/mcp-rpc + experiments/orchestrator + tests + docs, solution builds. Verdict READY/NOT READY.
- [ ] **Step 5:** Update `docs/.../specs/2026-06-16-erpc-identity-handshake-design.md` Definition-of-done if needed; ensure spec revision matches what was built.
