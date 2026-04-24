#!/bin/bash
set -euo pipefail

DEST="/app/results"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"

mkdir -p "$DEST"

if [ -d /home/motest/.mohist/logs/ ]; then
    cp -r /home/motest/.mohist/logs/ "$DEST/logs-${TIMESTAMP}/"
    echo "Logs collected to $DEST/logs-${TIMESTAMP}/"
else
    echo "No logs directory found"
fi

if [ -f /home/motest/.mohist/mohist.db ]; then
    cp /home/motest/.mohist/mohist.db "$DEST/mohist-${TIMESTAMP}.db"
    echo "Database snapshot: $DEST/mohist-${TIMESTAMP}.db"
fi
