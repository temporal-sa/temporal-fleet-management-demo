#!/usr/bin/env bash
# Starts the Temporal dev server with the demo's custom search attributes.
# In-memory by default, so every start is a clean demo. Add --db-filename to persist.
set -euo pipefail
exec temporal server start-dev \
  --ui-port 8233 \
  --search-attribute FleetEntity=Keyword \
  --search-attribute FleetStatus=Keyword \
  --search-attribute UnitNumber=Keyword \
  --search-attribute NeedsAttention=Bool \
  "$@"
