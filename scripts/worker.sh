#!/usr/bin/env bash
# Starts one worker process for one task queue: entities | dispatch | maintenance | integrations | all
# Usage: scripts/worker.sh maintenance        (background, log in .logs/)
#        scripts/worker.sh maintenance --fg   (foreground, logs in this terminal)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
QUEUE=${1:-all}
DLL=src/Fleet.Worker/bin/Debug/net8.0/Fleet.Worker.dll
[ -f "$DLL" ] || dotnet build src/Fleet.Worker -c Debug --nologo -v quiet
if [ "${2:-}" = "--fg" ]; then
  exec dotnet "$DLL" --queues "$QUEUE"
fi
mkdir -p .run .logs
nohup dotnet "$DLL" --queues "$QUEUE" > ".logs/worker-$QUEUE.log" 2>&1 &
echo $! > ".run/worker-$QUEUE.pid"
echo "worker-$QUEUE started (pid $!). Log: .logs/worker-$QUEUE.log"
