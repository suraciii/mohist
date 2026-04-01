#!/bin/bash
# test/agentic/verify-m1-infra/run.sh
#
# Build shared image and run tests.
#
# Usage:
#   bash run.sh                  # run all phases
#   bash run.sh phase4           # run single phase
#   bash run.sh run_all          # same as no args
set -e

IMAGE_NAME="mohist-test"
PROJECT_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
PHASE="${1:-run_all}"

echo "=== Building shared image ==="
podman build -t "$IMAGE_NAME" \
  --build-arg USER_ID=$(id -u) \
  --build-arg GROUP_ID=$(id -g) \
  -f "$PROJECT_ROOT/test/agentic/shared/Containerfile" \
  "$PROJECT_ROOT"

echo ""
echo "=== Running verify-m1-infra: $PHASE ==="
podman run --rm \
  --user $(id -u):$(id -g) \
  -v "$PROJECT_ROOT/test/agentic/verify-m1-infra/test.sh:/app/test.sh:ro,Z" \
  -w /app \
  "$IMAGE_NAME" \
  bash /app/test.sh "$PHASE"
