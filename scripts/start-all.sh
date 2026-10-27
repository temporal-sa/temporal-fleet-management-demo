#!/usr/bin/env bash
# Builds everything and starts the whole demo in the background (macOS / Linux).
# Logs go to .logs/<name>.log, process IDs to .run/<name>.pid.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
mkdir -p .run .logs

echo "Building the .NET solution..."
dotnet build FleetDemo.sln -c Debug --nologo -v quiet

if [ ! -d ui/node_modules ]; then
  echo "Installing UI packages..."
  (cd ui && npm install --no-fund --no-audit)
fi

start() {
  local name=$1; shift
  nohup "$@" > ".logs/$name.log" 2>&1 &
  echo $! > ".run/$name.pid"
  printf '  %-26s pid %-7s log .logs/%s.log\n' "$name" "$!" "$name"
}

port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

BIN=bin/Debug/net8.0
echo "Starting processes:"
if port_open 7233; then
  echo "  temporal                   already running on 7233"
else
  start temporal "$ROOT/scripts/start-temporal.sh"
  for _ in $(seq 1 30); do port_open 7233 && break; sleep 1; done
fi

start external-systems dotnet "src/Fleet.ExternalSystems/$BIN/Fleet.ExternalSystems.dll"
for q in entities dispatch maintenance integrations; do
  start "worker-$q" dotnet "src/Fleet.Worker/$BIN/Fleet.Worker.dll" --queues "$q"
done
start api dotnet "src/Fleet.Api/$BIN/Fleet.Api.dll"
(cd ui && nohup node node_modules/vite/bin/vite.js > "$ROOT/.logs/ui.log" 2>&1 & echo $! > "$ROOT/.run/ui.pid")
printf '  %-26s pid %-7s log .logs/ui.log\n' "ui" "$(cat .run/ui.pid)"

cat <<MSG

Fleet UI       http://localhost:5173
Temporal UI    http://localhost:8233
Give it about 20 seconds: the API creates the sync schedule, which starts the truck and driver workflows.

Kill a worker:     scripts/stop-worker.sh maintenance
Bring it back:     scripts/worker.sh maintenance
Stop everything:   scripts/stop-all.sh
MSG
