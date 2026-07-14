#!/usr/bin/env bash
# Server lifecycle for the haiku-subagent harness (Proto + Surface arms).
#
# A shell script can manage the two dotnet web servers but NOT the players
# (Claude haiku subagents, spawned by the driving agent) nor the ERPC arm
# (a stdio MCP server launched by Claude's own config — drive that via the
# mcp__tictactoe-rpc__* tools: list_games / new_game).
#
#   arena.sh up   <proto|surface>   # fresh server, wait ready, print GAME_ID/URL/LOG
#   arena.sh down <proto|surface>   # kill by pidfile
#   arena.sh status <proto|surface>
#
# `up` always tears down a stale instance and truncates the log first, so a run
# starts from a clean board (the servers are single-game: MAX_GAMES=1).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
READY_TRIES=60          # bound: 60 * 0.5s = 30s, then fail loudly
READY_SLEEP=0.5

arm_config() {          # one job: set globals for the named arm
  case "$1" in
    proto)
      PROJECT="$REPO_ROOT/src/TicTacToe.Web"
      PORT=5228
      ROUTE=games
      EXTRA_ENV=(TICTACTOE_DISABLE_JS=1)
      ;;
    surface)
      # The Surface twin is retired (spec 003b): the primary app IS the factorial surface,
      # driven by TICTACTOE_CELL. Same binary as `proto`, different port + cell.
      PROJECT="$REPO_ROOT/src/TicTacToe.Web"
      PORT=5328
      ROUTE=games
      # LOCK_GAME: the experiment game is immutable to agents (delete/reset -> 409), so an
      # agent that discovers or invents /reset cannot reset+replay the board and corrupt the run.
      # DISABLE_JS: no datastar bundle, no SSE auto-connect — the cold-start page is the
      # progressive-enhancement HTML the twin served, with no stream URL to waste a turn on.
      EXTRA_ENV=(${CELL:+TICTACTOE_CELL=$CELL} TICTACTOE_LOCK_GAME=1 TICTACTOE_DISABLE_JS=1)
      ;;
    *)
      echo "unknown arm: $1 (expected proto|surface)" >&2
      exit 2
      ;;
  esac
  URL="http://localhost:$PORT"
  PROXY_PORT=$((PORT + 1000))             # agents hit the proxy; it logs HTTP status
  # IPv4 loopback, NOT localhost: the proxy's IPv4 bind is what the .NET seat connects to.
  # Polling localhost (resolves to [::1]) reports ready before 127.0.0.1 is listening, so the
  # seat starts into a connection-refused gap. Gate on the seat's actual path.
  PROXY_URL="http://127.0.0.1:$PROXY_PORT"
  LOG="/tmp/arena-$1.jsonl"
  HTTPLOG="/tmp/arena-$1.http.jsonl"       # one JSONL line per request: method/path/status
  SERVERLOG="/tmp/arena-$1.server.log"
  PIDFILE="/tmp/arena-$1.pid"
  PROXY_PIDFILE="/tmp/arena-$1.proxy.pid"
}

stop_server() {         # one job: kill the server, by PIDFILE *and* by port.
  # `dotnet run` spawns a Kestrel child that outlives the wrapper pid, so the
  # pidfile alone leaks a zombie holding the port + stale game state. Kill the
  # actual listener by port too, or `up` silently reattaches to the old server.
  if [ -f "$PIDFILE" ]; then
    local pid; pid="$(cat "$PIDFILE")"
    kill -0 "$pid" 2>/dev/null && kill "$pid" 2>/dev/null || true
    rm -f "$PIDFILE"
  fi
  if [ -f "$PROXY_PIDFILE" ]; then
    local ppid; ppid="$(cat "$PROXY_PIDFILE")"
    kill -0 "$ppid" 2>/dev/null && kill "$ppid" 2>/dev/null || true
    rm -f "$PROXY_PIDFILE"
  fi
  local listeners; listeners="$(lsof -nP -tiTCP:$PORT -tiTCP:$PROXY_PORT 2>/dev/null || true)"
  [ -n "$listeners" ] && kill $listeners 2>/dev/null || true
  # bounded wait for both ports to free (10 * 0.3s = 3s); SIGKILL anything that
  # ignores SIGTERM — dotnet has survived `down` and left a zombie holding stale
  # game state, which silently invalidated a later run.
  local i=0
  while [ "$i" -lt 10 ] && lsof -nP -tiTCP:$PORT -tiTCP:$PROXY_PORT >/dev/null 2>&1; do
    i=$((i + 1)); sleep 0.3
  done
  local survivors; survivors="$(lsof -nP -tiTCP:$PORT -tiTCP:$PROXY_PORT 2>/dev/null || true)"
  [ -n "$survivors" ] && kill -9 $survivors 2>/dev/null || true
}

wait_ready() {          # bounded poll of /login; fail with server log on cap-hit
  local i=0
  while [ "$i" -lt "$READY_TRIES" ]; do
    if curl -s -o /dev/null -w '%{http_code}' "$URL/login" 2>/dev/null | grep -qE '200|302|303'; then
      return 0
    fi
    i=$((i + 1)); sleep "$READY_SLEEP"
  done
  echo "FATAL: $URL not ready after $READY_TRIES tries" >&2
  tail -20 "$SERVERLOG" >&2 || true
  return 1
}

discover_game_id() {    # login (throwaway jar) then read the game link off /
  local jar; jar="$(mktemp)"
  curl -s -c "$jar" "$URL/login" -o /dev/null
  local gid
  gid="$(curl -s -b "$jar" "$URL/" | grep -oE "$ROUTE/[0-9a-f-]{36}" | head -1)"
  rm -f "$jar"
  [ -n "$gid" ] || { echo "FATAL: no $ROUTE/<id> on $URL/" >&2; return 1; }
  echo "$gid"
}

cmd_up() {
  arm_config "$1"
  stop_server
  rm -f "$LOG"
  ( cd "$REPO_ROOT" && env "${EXTRA_ENV[@]}" \
      TICTACTOE_INITIAL_GAMES=1 TICTACTOE_MAX_GAMES=1 \
      TICTACTOE_REQUEST_LOG_PATH="$LOG" \
      dotnet run --project "$PROJECT" --no-build --urls="$URL" \
      >"$SERVERLOG" 2>&1 & echo $! >"$PIDFILE" )
  wait_ready
  # Logging reverse-proxy: agents hit $PROXY_URL; it forwards to $URL and records
  # every request's HTTP status (302 session-loss / 400 format / 403 rule / 303 ok)
  # that the server's own logs and the game-event log do not capture.
  ( dotnet run --project "$REPO_ROOT/experiments/oss-driver" --no-build -- proxy \
      "$PROXY_PORT" "$PORT" "$HTTPLOG" >/dev/null 2>&1 & echo $! >"$PROXY_PIDFILE" )
  # the F# proxy is a dotnet process — give it a moment to bind (bounded: 40 * 0.25s = 10s)
  local pi=0
  while [ "$pi" -lt 40 ] && ! curl -s -o /dev/null "$PROXY_URL/login" 2>/dev/null; do
    pi=$((pi + 1)); sleep 0.25
  done
  local gid; gid="$(discover_game_id)"
  echo "ARM=$1"
  echo "GAME_ID=${gid#*/}"
  echo "GAME_PATH=$gid"
  echo "URL=$PROXY_URL/$gid"        # agents play through the proxy
  echo "DIRECT_URL=$URL/$gid"
  echo "LOG=$LOG"
  echo "HTTPLOG=$HTTPLOG"
  echo "PID=$(cat "$PIDFILE")"
  echo "PROXY_PID=$(cat "$PROXY_PIDFILE")"
}

cmd_down() {
  arm_config "$1"
  stop_server
  echo "down: $1"
}

cmd_status() {
  arm_config "$1"
  if [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
    echo "$1: UP (pid $(cat "$PIDFILE"), $URL), log=$LOG"
  else
    echo "$1: down"
  fi
}

main() {
  local cmd="${1:-}"; local arm="${2:-}"
  [ -n "$arm" ] || { echo "usage: arena.sh <up|down|status> <proto|surface>" >&2; exit 2; }
  case "$cmd" in
    up)     cmd_up "$arm" ;;
    down)   cmd_down "$arm" ;;
    status) cmd_status "$arm" ;;
    *)      echo "usage: arena.sh <up|down|status> <proto|surface>" >&2; exit 2 ;;
  esac
}

main "$@"
