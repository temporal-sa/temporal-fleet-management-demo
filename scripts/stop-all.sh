#!/usr/bin/env bash
# Stops every process started by start-all.sh. The dev server is in-memory, so the next start is a clean demo.
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
for f in "$ROOT"/.run/*.pid; do
  [ -e "$f" ] || continue
  pid=$(cat "$f")
  if kill -0 "$pid" 2>/dev/null; then
    kill "$pid" 2>/dev/null || true
    echo "stopped $(basename "$f" .pid) ($pid)"
  fi
  rm -f "$f"
done
