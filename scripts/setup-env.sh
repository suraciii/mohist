#!/bin/bash
# Source this file before running crawlph tests
# Usage: source ~/.openclaw/agents/crawlph-test/setup-env.sh

# Export GitHub token from gh CLI
export GH_TOKEN=$(gh auth token)

# Set data directory for test agent
export CRAWLPH_DATA_DIR="$HOME/.openclaw/agents/crawlph-test/data"

# Verify token
if [ -n "$GH_TOKEN" ]; then
    echo "✓ GH_TOKEN set (${#GH_TOKEN} chars)"
else
    echo "✗ Failed to set GH_TOKEN"
    exit 1
fi

echo "✓ CRAWLPH_DATA_DIR set to $CRAWLPH_DATA_DIR"
echo ""
echo "Environment ready. You can now run:"
echo "  openclaw agent --agent crawlph-test --local --message '/crawlph 1 --yes'"
