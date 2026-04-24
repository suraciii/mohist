#!/bin/bash
set -euo pipefail

ISSUE_ID="${1:?Usage: check-status.sh <issue-id>}"

echo "=== Issue #${ISSUE_ID} ==="
mo issue show "$ISSUE_ID" 2>&1 || echo "(show command failed)"
echo ""
echo "=== Agent Processes ==="
ps aux | grep -E "opencode|mo-server" | grep -v grep || echo "No agent processes"
echo ""
echo "=== Server Health ==="
curl -sf http://localhost:3456/api/health || echo "(health check failed)"
