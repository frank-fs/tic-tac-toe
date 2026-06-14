"""Manual smoke for the http_request tool against live game servers.

Drives the tool logic directly (fake Context) — exercises req/resp, the visible
4xx envelope, and the held SSE stream. Not a unit test; run with servers up:
  Simple on :5328, Proto on :5228 (both locked to one game).
"""

import asyncio
import re
from types import SimpleNamespace

import httpx

from mcp_http.server import HttpState, http_request


def ctx_for(state: HttpState) -> SimpleNamespace:
    return SimpleNamespace(request_context=SimpleNamespace(lifespan_context=state))


async def main() -> None:
    async with httpx.AsyncClient(follow_redirects=True, timeout=30.0) as client:
        state = HttpState(client=client)
        ctx = ctx_for(state)

        print("===== SIMPLE: GET home =====")
        home = await http_request("GET", "http://localhost:5328/", ctx)
        print(home[:400])
        arena = re.search(r"/arenas/[0-9a-f-]{36}", home)
        assert arena, "no arena link found on home"
        path = arena.group(0)

        print("\n===== SIMPLE: GET arena =====")
        print((await http_request("GET", f"http://localhost:5328{path}", ctx))[:300])

        print("\n===== SIMPLE: POST move X TopLeft (expect 2xx/3xx) =====")
        form = {"Content-Type": "application/x-www-form-urlencoded"}
        print(
            (
                await http_request(
                    "POST", f"http://localhost:5328{path}", ctx, headers=form, body="player=X&position=TopLeft"
                )
            )[:200]
        )

        print("\n===== SIMPLE: POST same cell again (expect 4xx — envelope must be VISIBLE) =====")
        print(
            (
                await http_request(
                    "POST", f"http://localhost:5328{path}", ctx, headers=form, body="player=O&position=TopLeft"
                )
            )[:200]
        )

        print("\n===== PROTO: open SSE stream /sse (must stay open, return events) =====")
        pstate = HttpState(client=client)
        pctx = ctx_for(pstate)
        # authenticate first
        await http_request("GET", "http://localhost:5228/", pctx)
        stream1 = await http_request("GET", "http://localhost:5228/sse", pctx)
        print(stream1[:400])

        print("\n===== PROTO: re-request /sse to drain (proves hold-open + send-more) =====")
        await asyncio.sleep(0.5)
        print((await http_request("GET", "http://localhost:5228/sse", pctx))[:300])


if __name__ == "__main__":
    asyncio.run(main())
