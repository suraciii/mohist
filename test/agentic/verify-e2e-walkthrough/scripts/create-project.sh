#!/bin/bash
set -euo pipefail

PROJECT_NAME="${1:?Usage: create-project.sh <name>}"
PROJECT_PATH="/app/workspace/${PROJECT_NAME}"

git config --global user.email "motest@test.local" 2>/dev/null || true
git config --global user.name "motest" 2>/dev/null || true

mkdir -p "$PROJECT_PATH"
cd "$PROJECT_PATH"
git init
git commit --allow-empty -m "Initial commit"

mo project create "$PROJECT_NAME" --path "$PROJECT_PATH"
mo project use "$PROJECT_NAME"

echo "Project $PROJECT_NAME created and activated"
