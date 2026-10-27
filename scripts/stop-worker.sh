#!/usr/bin/env bash
# Hard-kills one worker process (kill -9), like a node dying. Workflows on its queue pause; nothing is lost.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
QUEUE=${1:?usage: scripts/stop-worker.sh entities|dispatch|maintenance|integrations}
PID_FILE="$ROOT/.run/worker-$QUEUE.pid"
if [ -f "$PID_FILE" ] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
  kill -9 "$(cat "$PID_FILE")"
  rm -f "$PID_FILE"
  echo "worker-$QUEUE killed. Watch its pill turn red in the fleet UI."
else
  echo "worker-$QUEUE is not running."
fi
