## Why

Users need a visible, reliable way to understand whether Mohist has runner capacity available for the current project and what connected runners are doing. Runner registration and runtime state already contain useful operational details, but today that state is mostly implicit or only visible through logs and a minimal agent status response.

## What Changes

- Add a stable runner status read model/API for the selected project that includes project-scoped runners and global runners that can serve it.
- Expose user-facing runner details including runner id, kind, hostname, scope, liveness/status, registration or connection timing where available, heartbeat freshness, SignalR connection state, capabilities, coder model names/counts, capacity/slots where available, and active work assignment when present.
- Extend runner registry/status projection behavior so the API is backed by registered runner information enriched with runner runtime state, rather than only returning runner ids.
- Add Web UI runner status surfaces that clearly distinguish no runner, connected idle runner, and connected busy runner states without requiring users to open logs.
- Preserve the current board no-runner warning while pointing users toward the runner status surface for details and startup guidance.
- Keep existing `/api/agent/status` compatibility or migrate it safely with coverage so existing consumers do not break.

## Capabilities

### New Capabilities

- `runner-status`: Project-scoped runner visibility covering the runner status API/read model and Web UI surfaces for connected, idle, busy, and empty runner states.

### Modified Capabilities

- `http-api`: Add or migrate stable HTTP endpoints for UI-facing runner status while preserving existing agent status compatibility.
- `web-ui`: Add runner summary/list rendering and connect the existing no-runner board state to the detailed runner status view.

## Impact

- Backend runner registry and runner grain projection/query code must expose richer `RunnerInfo` and runtime state to a UI-facing read model.
- REST API surface changes around runner status, likely under `/api/runners` or `/api/agent/runners`, with compatibility coverage for `/api/agent/status`.
- Web UI status bar, activity/settings runner list, and board no-runner banner will consume the new runner status read model.
- Tests are needed for backend runner status projection, global versus project-scoped runner inclusion, API compatibility, and Web UI empty/idle/busy rendering.
