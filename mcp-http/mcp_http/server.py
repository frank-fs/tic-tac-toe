"""A concise, hypertext-faithful HTTP client exposed as an MCP stdio server.

One tool, ``http_request``, whose behaviour is driven by the response media
type — the way HTTP itself intends:

* a normal response returns the envelope (status line + key headers) and the
  raw body, with ``<style>``/``<script>`` presentation stripped to respect a
  small context window;
* a ``text/event-stream`` response is held open in the background — the agent
  re-requests the same URL to drain newly pushed events, and may send other
  requests while it stays open.

This exists because a browser-automation tool reduces HTTP to "click and scrape
the DOM": status codes, headers and the streaming envelope never reach the
agent. This client transports them, so the agent can use HTTP as hypertext.
"""

from __future__ import annotations

import asyncio
import re
from contextlib import asynccontextmanager
from dataclasses import dataclass, field

import httpx
from mcp.server.fastmcp import Context, FastMCP

# Bounded wait after opening a stream so the initial server push (current state)
# is captured before the tool returns.
STREAM_INITIAL_WAIT_SECONDS = 0.5
# Hard cap on returned body length — a guard against flooding a small context.
BODY_CHAR_LIMIT = 6000
REQUEST_TIMEOUT_SECONDS = 30.0

# Headers worth surfacing to the agent (the hypermedia/uniform-interface ones).
SURFACED_HEADERS = ("content-type", "location", "link", "retry-after", "allow")

_PRESENTATION = re.compile(r"<(style|script)\b[^>]*>.*?</\1>", re.IGNORECASE | re.DOTALL)


@dataclass
class OpenStream:
    """A held text/event-stream response and its buffered, undrained events."""

    response: httpx.Response
    reader: asyncio.Task | None = None
    events: list[str] = field(default_factory=list)
    cursor: int = 0


@dataclass
class HttpState:
    """Per-session client (cookies persist) plus any open streams keyed by URL."""

    client: httpx.AsyncClient
    streams: dict[str, OpenStream] = field(default_factory=dict)


@asynccontextmanager
async def _lifespan(_server: FastMCP):
    async with httpx.AsyncClient(
        follow_redirects=True, timeout=REQUEST_TIMEOUT_SECONDS
    ) as client:
        state = HttpState(client=client)
        try:
            yield state
        finally:
            for stream in state.streams.values():
                if stream.reader is not None:
                    stream.reader.cancel()


mcp = FastMCP("http", lifespan=_lifespan)


def _strip_presentation(body: str) -> str:
    """Remove style/script blocks — presentation, not hypermedia controls."""
    return _PRESENTATION.sub("", body)


def _header_lines(response: httpx.Response) -> str:
    lines = [f"{h}: {response.headers[h]}" for h in SURFACED_HEADERS if h in response.headers]
    return "\n".join(lines)


def _drain(stream: OpenStream) -> str:
    new = stream.events[stream.cursor :]
    stream.cursor = len(stream.events)
    return "\n".join(new) if new else "(no new events)"


async def _read_stream(stream: OpenStream) -> None:
    try:
        async for line in stream.response.aiter_lines():
            if line.strip():
                stream.events.append(line)
    except Exception:
        # The stream ended or was cancelled; buffered events remain drainable.
        pass


@mcp.tool()
async def http_request(
    method: str,
    url: str,
    ctx: Context,
    headers: dict | None = None,
    body: str | None = None,
) -> str:
    """Send an HTTP request; return the raw response (status, headers, body).

    The response Content-Type tells you how to proceed. If it is
    ``text/event-stream`` the connection stays open: re-request the same URL
    with GET to drain newly pushed events, and you may send other requests
    meanwhile. For a form submission set the ``Content-Type`` header to
    ``application/x-www-form-urlencoded`` and put fields in ``body`` (for
    example ``player=X&position=TopLeft``). The session (cookies) persists
    across calls.
    """
    state: HttpState = ctx.request_context.lifespan_context
    method = method.upper()

    open_stream = state.streams.get(url)
    if method == "GET" and open_stream is not None:
        return (
            f"HTTP (open stream, last status {open_stream.response.status_code})\n"
            f"content-type: text/event-stream\n"
            f"--- events ---\n{_drain(open_stream)}"
        )

    request = state.client.build_request(method, url, headers=headers, content=body)
    try:
        response = await state.client.send(request, stream=True)
    except httpx.HTTPError as exc:
        return f"HTTP request failed: {exc}"

    if "text/event-stream" in response.headers.get("content-type", ""):
        stream = OpenStream(response=response)
        stream.reader = asyncio.create_task(_read_stream(stream))
        state.streams[url] = stream
        await asyncio.sleep(STREAM_INITIAL_WAIT_SECONDS)
        return (
            f"HTTP {response.status_code} {response.reason_phrase} "
            f"(stream open — re-request {url} to drain new events)\n"
            f"{_header_lines(response)}\n--- events ---\n{_drain(stream)}"
        )

    raw = await response.aread()
    await response.aclose()
    text = _strip_presentation(raw.decode(response.encoding or "utf-8", errors="replace"))
    if len(text) > BODY_CHAR_LIMIT:
        text = text[:BODY_CHAR_LIMIT] + f"\n…[truncated {len(text) - BODY_CHAR_LIMIT} chars]"
    return (
        f"HTTP {response.status_code} {response.reason_phrase}\n"
        f"{_header_lines(response)}\n--- body ---\n{text}"
    )


def main() -> None:
    mcp.run()


if __name__ == "__main__":
    main()
