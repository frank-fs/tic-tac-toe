#!/usr/bin/env python3
"""Logging reverse-proxy for the haiku-subagent harness (curl arms only).

The dotnet servers configure their own logging and suppress ASP.NET request
logs, so HTTP-status friction (302 session-loss, 400 format-guess, 403 rule)
is otherwise invisible — `move_rejected` only captures game-rule rejections
that reach the handler with a valid session. Point agents at this proxy; it
forwards verbatim to the real server and appends one JSONL line per request:
{ts, method, path, status}. Redirects are NOT followed (302 passes through so
the agent behaves exactly as against the real server).

    uv run --no-project experiments/haiku-subagents/proxy.py <listen_port> <target_port> <log_path>
"""
import sys
import json
import time
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LISTEN_PORT = int(sys.argv[1])
TARGET_PORT = int(sys.argv[2])
LOG_PATH = sys.argv[3]

# Hop-by-hop headers must not be forwarded (RFC 7230 §6.1).
HOP_BY_HOP = {
    "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
    "te", "trailers", "transfer-encoding", "upgrade", "content-length",
}


def _log(method, path, status):
    line = json.dumps({"ts": time.time(), "method": method, "path": path, "status": status})
    with open(LOG_PATH, "a") as f:
        f.write(line + "\n")


class ProxyHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _proxy(self, method):
        body = b""
        length = int(self.headers.get("Content-Length", 0) or 0)
        if length:
            body = self.rfile.read(length)

        conn = http.client.HTTPConnection("localhost", TARGET_PORT, timeout=30)
        fwd = {k: v for k, v in self.headers.items() if k.lower() not in HOP_BY_HOP}
        try:
            conn.request(method, self.path, body=body, headers=fwd)
            resp = conn.getresponse()
            data = resp.read()
        except Exception as e:
            self.send_error(502, f"proxy error: {e}")
            _log(method, self.path, 502)
            return
        finally:
            conn.close()

        _log(method, self.path, resp.status)
        self.send_response(resp.status)
        for k, v in resp.getheaders():
            if k.lower() in HOP_BY_HOP:
                continue
            self.send_header(k, v)        # replays each Set-Cookie individually
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._proxy("GET")

    def do_POST(self):
        self._proxy("POST")

    def log_message(self, *args):
        pass  # silence default stderr access log; we write our own JSONL


if __name__ == "__main__":
    open(LOG_PATH, "w").close()  # fresh log per run
    ThreadingHTTPServer(("localhost", LISTEN_PORT), ProxyHandler).serve_forever()
