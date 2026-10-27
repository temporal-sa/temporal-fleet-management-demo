#!/usr/bin/env bash
# Saves a workflow's event history for the replay test (tests/Fleet.Tests/ReplayTests.cs).
# Usage: scripts/export-history.sh maint-41207-001
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ID=${1:?usage: scripts/export-history.sh <workflowId>}
OUT="$ROOT/tests/Fleet.Tests/histories/$ID.json"
temporal workflow show --workflow-id "$ID" --output json > "$OUT"
echo "Saved $OUT. Run: dotnet test"
