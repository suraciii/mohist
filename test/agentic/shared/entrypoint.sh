#!/bin/bash
set -e

export HOME="/home/motest"

echo "=== mohist Test Environment ==="
echo "Node: $(node --version), git: $(git --version)"

MAX_RESTARTS=3
restart_count=0
shutdown=0

trap 'shutdown=1; kill $SERVER_PID 2>/dev/null' TERM INT

start_server() {
    echo "Starting mohist server..."
    cd /app/workspace
    mo-server &
    SERVER_PID=$!
}

wait_for_health() {
    for i in $(seq 1 60); do
        if curl -sf http://localhost:3456/api/health > /dev/null 2>&1; then
            echo "Server ready (PID: $SERVER_PID, port: 3456)"
            return 0
        fi
        if ! kill -0 $SERVER_PID 2>/dev/null; then
            echo "ERROR: Server process exited during health check"
            return 1
        fi
        sleep 0.5
    done
    echo "FATAL: Server failed health check within 30s"
    return 1
}

start_server
if ! wait_for_health; then
    exit 1
fi
echo ""

if [ $# -gt 0 ]; then
    exec "$@"
fi

while true; do
    wait $SERVER_PID 2>/dev/null
    exit_code=$?

    if [ "$shutdown" -eq 1 ]; then
        echo "Shutting down gracefully"
        exit 0
    fi

    if [ $exit_code -eq 0 ] || [ $exit_code -eq 143 ]; then
        echo "Server exited cleanly (code: $exit_code)"
        exit 0
    fi

    restart_count=$((restart_count + 1))
    echo "WARN: Server crashed (exit: $exit_code), restart $restart_count/$MAX_RESTARTS"

    if [ $restart_count -ge $MAX_RESTARTS ]; then
        echo "FATAL: Server crashed $MAX_RESTARTS times, giving up"
        exit 1
    fi

    sleep 1
    start_server
    if wait_for_health; then
        echo ""
    else
        continue
    fi
done
