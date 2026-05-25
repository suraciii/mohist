#!/bin/bash
set -euo pipefail

ISSUE_ID="${1:?Usage: check-status.sh <issue-id>}"

echo "=== Issue #${ISSUE_ID} ==="
curl -sf "http://localhost:3456/api/issues/${ISSUE_ID}" 2>&1 || echo "(show command failed)"
echo ""
echo "=== Agent Processes ==="
ps aux | grep -E "opencode|Mohist.Server|dotnet" | grep -v grep || echo "No agent processes"
echo ""
echo "=== Server Health ==="
curl -sf http://localhost:3456/api/health || echo "(health check failed)"
