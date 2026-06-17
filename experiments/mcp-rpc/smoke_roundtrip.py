#!/usr/bin/env python3
"""End-to-end round trip against the REAL stdio MCP server.

Proves the identity handshake: authenticate -> new_game -> make_move, where
make_move carries NO token/player and relies entirely on the message filter
bridging SessionIdentity.Current onto context.User. If make_move succeeds with
X on the board, the bridge works. If it returns unauthenticated/error, it does not.
"""

import json
import os
import subprocess
import sys

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


def main():
    if not os.path.exists(DLL):
        fail(f"DLL not found: {DLL} (build Release first)")

    srv = Server()
    try:
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

        auth = srv.request({
            "jsonrpc": "2.0", "id": 2, "method": "tools/call",
            "params": {"name": "authenticate", "arguments": {}},
        })
        auth_payload = tool_payload(auth)
        token = auth_payload.get("token")
        if not token:
            fail("authenticate returned no token", auth)
        print(f"authenticate -> token={token}")

        ng = srv.request({
            "jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": {"name": "new_game", "arguments": {}},
        })
        ng_payload = tool_payload(ng)
        game_id = ng_payload.get("gameId")
        if not game_id:
            fail("new_game returned no gameId", ng)
        print(f"new_game -> gameId={game_id}")

        mv = srv.request({
            "jsonrpc": "2.0", "id": 4, "method": "tools/call",
            "params": {
                "name": "make_move",
                "arguments": {"gameId": game_id, "position": "TopLeft"},
            },
        })
        mv_payload = tool_payload(mv)
        print("make_move response:")
        print(json.dumps(mv_payload, indent=2))

        if "error" in mv_payload:
            fail(
                "make_move returned an error -> identity bridge is BROKEN "
                "(session token did not reach make_move's ClaimsPrincipal)",
                json.dumps(mv_payload),
            )

        board = mv_payload.get("board")
        if not isinstance(board, list) or len(board) != 9:
            fail("make_move did not return a 9-cell board", json.dumps(mv_payload))
        if board[0] != "X":
            fail(f"expected X at index 0, got {board[0]!r}", json.dumps(mv_payload))

        print("\nPASS: authenticate -> new_game -> make_move succeeded; board[0]='X'.")
        print("The message filter bridged SessionIdentity -> ClaimsPrincipal across calls.")
    finally:
        srv.close()


if __name__ == "__main__":
    main()
