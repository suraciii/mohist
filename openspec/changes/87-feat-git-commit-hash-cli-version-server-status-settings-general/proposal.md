## Why

Users and developers have no way to confirm which exact code the mohist server is running. `mo --version` only shows `0.1.0` with no git commit hash, server startup logs lack version info, and the WebUI has no version display. This makes bug reporting, deployment verification, and log correlation unreliable.

## What Changes

- `mo --version` output changes from `0.1.0` to `0.1.0 (abc1234)` format, including git short hash
- `mo server status` output adds a `Version: 0.1.0 (abc1234)` line
- Server startup logs record version + git hash as the first structured log entry
- `GET /api/status` response adds `version` (string) and `gitHash` (string) fields
- `GET /api/health` response adds `version` and `gitHash` fields
- WebUI Settings > General tab shows a version info block at the bottom (version + hash)
- New `packages/cli/src/version.ts` module provides a single `getVersionInfo()` function used by CLI, server, and API

## Capabilities

### New Capabilities

- **version-reporting**: Centralized version + git commit hash retrieval and reporting across CLI, server, API, and WebUI

### Modified Capabilities

- **cli-interface**: `mo --version` output format changes; `mo server status` adds version line
- **http-api**: `GET /api/status` and `GET /api/health` response schemas gain `version` and `gitHash` fields
- **web-ui**: Settings General tab gains version info display section

## Impact

- **Files**: `packages/cli/src/version.ts` (new), `packages/cli/src/cli/index.ts`, `packages/cli/src/cli/commands/server.ts`, `packages/cli/src/server/index.ts`, `packages/cli/src/api/status.ts`, WebUI settings components
- **API**: `GET /api/status` and `GET /api/health` responses gain two new fields — backward-compatible additive change
- **Build**: No new build-time steps required; version hash is resolved at runtime via `git rev-parse --short HEAD` with package.json version as fallback
- **Dependencies**: None
