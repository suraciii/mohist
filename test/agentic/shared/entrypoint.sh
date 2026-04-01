#!/bin/bash
set -e

export HOME="/home/motest"

echo "=== mohist Test Environment ==="
echo "Node: $(node --version), git: $(git --version)"

echo "Starting mohist server..."
cd /app/workspace
mo-server &
SERVER_PID=$!

for i in $(seq 1 30); do
    if curl -sf http://localhost:3456/api/health > /dev/null 2>&1; then
        echo "Server ready (PID: $SERVER_PID, port: 3456)"
        break
    fi
    if [ "$i" -eq 30 ]; then
        echo "FATAL: Server failed to start within 15s"
        exit 1
    fi
    sleep 0.5
done
echo ""

exec "$@"
