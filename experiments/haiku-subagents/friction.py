#!/usr/bin/env python3
"""Classify per-arm request friction into reads / accepted-writes / rejected-writes /
auth-redirects — so polling is no longer conflated with rejection.

Two log shapes:
  proxy  — /tmp/arena-<arm>.http.jsonl  ({method,path,status} per HTTP request)
  erpc   — /tmp/erpc-game.jsonl         (event_type per RPC; needs state_read logging)

    uv run --no-project experiments/haiku-subagents/friction.py proxy /tmp/arena-proto.http.jsonl
    uv run --no-project experiments/haiku-subagents/friction.py erpc  /tmp/erpc-game.jsonl [gameId]
"""
import sys
import json

KIND = sys.argv[1]
PATH = sys.argv[2]
GAME = sys.argv[3] if len(sys.argv) > 3 else None

reads = writes_ok = writes_rej = auth = other = 0

with open(PATH) as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        try:
            e = json.loads(line)
        except ValueError:
            continue
        if KIND == "proxy":
            method, status = e.get("method"), e.get("status", 0)
            if status == 302:
                auth += 1                        # bounced to /login (session lost / no cookie)
            elif method == "GET":
                reads += 1                        # poll / read (200, or 304)
            elif method == "POST" and status == 303:
                writes_ok += 1                    # accepted move, Proto PRG
            elif method == "POST" and status == 200:
                writes_ok += 1                    # accepted move, Simple
            elif method == "POST" and status >= 400:
                writes_rej += 1                   # rejected move (400 malformed / 403 rule)
            else:
                other += 1
        elif KIND == "erpc":
            if GAME and e.get("game_id") != GAME:
                continue
            et = e.get("event_type")
            if et == "state_read":
                reads += 1
            elif et == "move_accepted":
                writes_ok += 1
            elif et == "move_rejected":
                writes_rej += 1
            # player_assigned / game_over are neither read nor write-attempt

writes = writes_ok + writes_rej
ratio = f"{reads / writes:.1f}:1" if writes else "n/a"
print(f"{PATH.split('/')[-1]}")
print(f"  reads (poll):     {reads}")
print(f"  writes accepted:  {writes_ok}")
print(f"  writes rejected:  {writes_rej}")
print(f"  auth redirects:   {auth}")
if other:
    print(f"  other:            {other}")
print(f"  read:write ratio: {ratio}   (rejections are NOT counted as reads)")
