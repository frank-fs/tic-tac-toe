#!/usr/bin/env bash
# End-to-end: fresh Simple arm, 3 cold-start agents on one game, friction, teardown.
# Usage: run.sh [persona]
set -euo pipefail
PERSONA="${1:-expert}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ARENA="$HERE/../haiku-subagents/arena.sh"
OUT="/tmp/discovery-simple.results.json"

UPTMP="$(mktemp)"
# arena.sh stays alive until its dotnet child exits (macOS orphan adoption).
# Background it to a temp file and wait for PROXY_PID line instead of pipe-EOF.
"$ARENA" up simple >"$UPTMP" 2>&1 &
ARENA_PID=$!
# Bounded wait: 60 * 0.5s = 30s for PROXY_PID line to appear.
i=0; while [ $i -lt 60 ] && ! grep -q '^PROXY_PID=' "$UPTMP" 2>/dev/null; do sleep 0.5; i=$((i+1)); done
cat "$UPTMP"
GAME_URL="$(grep -oE 'URL=http://[^ ]+' "$UPTMP" | head -1 | cut -d= -f2-)"
# --base is the proxy root (scheme+host+port); Driver appends paths from there.
URL="$(printf '%s' "$GAME_URL" | grep -oE 'https?://[^/]+')"
rm -f "$UPTMP"
[ -n "$URL" ] || { echo "no proxy URL from arena"; kill $ARENA_PID 2>/dev/null || true; exit 1; }

dotnet run --project "$HERE" --no-build -- --base "$URL" --persona "$PERSONA" --out "$OUT"
echo "--- friction ---"
uv run "$HERE/../haiku-subagents/friction.py" proxy "/tmp/arena-simple.http.jsonl" || true
"$ARENA" down simple
echo "results: $OUT"
