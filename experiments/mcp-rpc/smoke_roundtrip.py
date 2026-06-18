#!/usr/bin/env python3
"""End-to-end round trip against the REAL stdio MCP server.

Proves PER-REQUEST identity via MCP `_meta`: ONE server process / ONE stdio
connection carries MANY distinct identities. The orchestrator (here, this
script) sets params._meta.identityToken on each tools/call; the server's message
filter projects it onto context.User so make_move derives the seat from it.

Cases on ONE connection:
  1. authenticate (no _meta) x2 -> token A, token B (distinct).
  2. new_game -> gameId.
  3. make_move _meta=A, TopLeft -> board[0]='X', turn 'O'   (A binds to X).
  4. make_move _meta=B, TopCenter -> board[1]='O', turn 'X' (B binds to O; alternation).
  5. make_move _meta=A, MiddleCenter -> success            (A still X across the shared connection).
  6. make_move with NO _meta token -> clean {"error":"unauthenticated"}.
  7. make_move with _meta.identityToken=123 (integer, not string) -> clean {"error":"unauthenticated"}
     (non-string token degrades to anonymous; no framework error).
"""

import json
import os
import select
import subprocess
import sys

READ_TIMEOUT_S = 30  # fail fast if the server goes silent instead of blocking forever

DLL = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "bin", "Release", "net10.0", "TicTacToe.McpRpc.dll",
)


def fail(msg, extra=None):
    print(f"FAIL: {msg}")
    if extra is not None:
        print(extra)
    sys.exit(1)


class Server:
    def __init__(self):
        self.proc = subprocess.Popen(
            ["dotnet", DLL],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            bufsize=1,
        )

    def send(self, obj):
        self.proc.stdin.write(json.dumps(obj) + "\n")
        self.proc.stdin.flush()

    def request(self, req):
        """Send a request and read response lines until the matching id."""
        self.send(req)
        want = req["id"]
        for _ in range(50):  # bounded: tolerate interleaved notifications
            ready, _, _ = select.select([self.proc.stdout], [], [], READ_TIMEOUT_S)
            if not ready:
                fail(f"server went silent for {READ_TIMEOUT_S}s waiting on id={want}")
            line = self.proc.stdout.readline()
            if not line:
                fail(f"server closed stdout before responding to id={want}")
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if msg.get("id") == want:
                return msg
        fail(f"no response correlated to id={want} within bound")

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


def tool_payload(result_msg):
    """Extract the tool's returned object: prefer structuredContent, else parse text content as JSON."""
    result = result_msg.get("result", {})
    if "structuredContent" in result and result["structuredContent"] is not None:
        return result["structuredContent"]
    content = result.get("content") or []
    for item in content:
        if item.get("type") == "text":
            try:
                return json.loads(item["text"])
            except (json.JSONDecodeError, KeyError):
                return {"text": item.get("text")}
    return result


def handshake(srv):
    """initialize + notifications/initialized on a fresh server."""
    init = srv.request({
        "jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "smoke", "version": "1.0"},
        },
    })
    if "result" not in init:
        fail("initialize failed", init)
    srv.send({"jsonrpc": "2.0", "method": "notifications/initialized"})


def call(srv, req_id, name, arguments, identity_token=None):
    """tools/call with optional per-request _meta.identityToken."""
    params = {"name": name, "arguments": arguments}
    if identity_token is not None:
        params["_meta"] = {"identityToken": identity_token}
    return srv.request({
        "jsonrpc": "2.0", "id": req_id, "method": "tools/call", "params": params,
    })


def expect_move(srv, req_id, token, position, game_id, idx, mark, next_turn, label):
    """make_move with _meta token; assert board[idx]==mark and next turn."""
    mv = call(srv, req_id, "make_move",
              {"gameId": game_id, "position": position}, identity_token=token)
    payload = tool_payload(mv)
    print(f"{label}: make_move _meta={token[:8]}.. {position} ->")
    print(json.dumps(payload, indent=2))
    if "error" in payload:
        fail(f"{label}: expected success, got error", json.dumps(payload))
    board = payload.get("board")
    if not isinstance(board, list) or len(board) != 9:
        fail(f"{label}: not a 9-cell board", json.dumps(payload))
    if board[idx] != mark:
        fail(f"{label}: expected {mark!r} at index {idx}, got {board[idx]!r}", json.dumps(payload))
    if payload.get("whoseTurn") != next_turn:
        fail(f"{label}: expected turn {next_turn!r}, got {payload.get('whoseTurn')!r}", json.dumps(payload))
    print(f"PASS: {label} -> board[{idx}]={mark!r}, turn {next_turn!r}.")
    return payload


def main():
    if not os.path.exists(DLL):
        fail(f"DLL not found: {DLL} (build Release first)")

    print("=== MULTI-IDENTITY on ONE shared stdio connection (per-request _meta) ===")
    srv = Server()
    try:
        handshake(srv)

        # 1. authenticate x2 (no _meta) -> two distinct tokens
        a = tool_payload(call(srv, 2, "authenticate", {})).get("token")
        b = tool_payload(call(srv, 3, "authenticate", {})).get("token")
        if not a or not b:
            fail("authenticate returned no token", f"a={a!r} b={b!r}")
        if a == b:
            fail("expected DISTINCT tokens from two authenticate calls", f"a={a!r} b={b!r}")
        print(f"authenticate x2 -> A={a}")
        print(f"                  B={b}  (distinct)")

        # 2. new_game
        game_id = tool_payload(call(srv, 4, "new_game", {})).get("gameId")
        if not game_id:
            fail("new_game returned no gameId")
        print(f"new_game -> gameId={game_id}")

        # 3. A -> X at TopLeft
        expect_move(srv, 5, a, "TopLeft", game_id, 0, "X", "O", "case3 A->X")

        # 4. B -> O at TopCenter, turn back to X
        expect_move(srv, 6, b, "TopCenter", game_id, 1, "O", "X", "case4 B->O alternation")

        # 5. A still X -> MiddleCenter
        expect_move(srv, 7, a, "MiddleCenter", game_id, 4, "X", "O", "case5 A-still-X")

        # 6. make_move with NO _meta token -> clean unauthenticated
        print("case6 no-_meta: make_move WITHOUT identityToken ->")
        mv = call(srv, 8, "make_move", {"gameId": game_id, "position": "TopRight"})
        if "error" in mv:
            fail("framework JSON-RPC error instead of clean structured unauthenticated",
                 json.dumps(mv.get("error")))
        if mv.get("result", {}).get("isError"):
            fail("tool result isError=true (exception path) instead of clean unauthenticated",
                 json.dumps(mv.get("result")))
        payload = tool_payload(mv)
        print(json.dumps(payload, indent=2))
        if payload.get("error") != "unauthenticated":
            fail("expected clean {'error':'unauthenticated'}", json.dumps(payload))
        print("PASS: case6 no-_meta -> clean {'error':'unauthenticated'}.")

        # 7. make_move with _meta.identityToken as integer (malformed) -> clean unauthenticated
        print("case7 malformed-_meta: make_move with identityToken=123 (integer, not string) ->")
        mv7 = srv.request({
            "jsonrpc": "2.0", "id": 9, "method": "tools/call",
            "params": {
                "name": "make_move",
                "arguments": {"gameId": game_id, "position": "TopRight"},
                "_meta": {"identityToken": 123},
            },
        })
        if "error" in mv7:
            fail("framework JSON-RPC error on malformed identityToken (integer) — expected clean unauthenticated",
                 json.dumps(mv7.get("error")))
        if mv7.get("result", {}).get("isError"):
            fail("tool result isError=true on malformed identityToken — expected clean unauthenticated",
                 json.dumps(mv7.get("result")))
        payload7 = tool_payload(mv7)
        print(json.dumps(payload7, indent=2))
        if payload7.get("error") != "unauthenticated":
            fail("expected clean {'error':'unauthenticated'} for integer token", json.dumps(payload7))
        print("PASS: case7 malformed-_meta integer -> clean {'error':'unauthenticated'}.")

        print("\nALL CASES PASS: two tokens bound to two distinct seats (A=X, B=O) on ONE "
              "shared connection via per-request _meta; A stayed X across requests; the "
              "unauthenticated path is clean for both absent and malformed (non-string) tokens.")
    finally:
        srv.close()


if __name__ == "__main__":
    main()
