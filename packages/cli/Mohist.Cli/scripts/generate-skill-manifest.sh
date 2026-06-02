#!/usr/bin/env bash
# Generates a manifest.json for the Mohist CLI skill-data directory.
# Usage: generate-skill-manifest.sh <manifest-path> <version>

set -euo pipefail

MANIFEST_PATH="${1:-}"
VERSION="${2:-1.0.0}"

if [[ -z "${MANIFEST_PATH}" ]]; then
    echo "Usage: $0 <manifest-path> <version>" >&2
    exit 1
fi

GIT_HASH="${MOHIST_GIT_HASH:-$(git rev-parse HEAD 2>/dev/null || echo dev)}"

mkdir -p "$(dirname "${MANIFEST_PATH}")"

cat > "${MANIFEST_PATH}" <<EOF
{
  "schemaVersion": 1,
  "cliVersion": "${VERSION}",
  "gitHash": "${GIT_HASH}",
  "skills": [ "mohist", "mohist-explore" ]
}
EOF
