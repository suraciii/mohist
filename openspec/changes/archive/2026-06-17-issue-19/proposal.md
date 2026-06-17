## Why

Settings currently exposes broken controls on the .NET server: System log level can appear to change while the backend returns 404, and Runtime fails to load because the Web UI calls a missing `/api/agent-runtime` endpoint. This blocks users from trusting diagnostics and from configuring existing agent scheduling settings that are already available through supported configuration APIs.

## What Changes

- Restore System Log Level so it displays the real current value, supports `DEBUG`, `INFO`, `WARN`, and `ERROR`, persists changes through a supported API, and reports failures visibly instead of silently accepting them.
- Restore Settings > Runtime so it loads on the .NET server using a backend contract that reflects effective agent runtime values.
- Allow supported runtime settings such as concurrency, session timeout, task timeout, stage timeout, and grace-period behavior to be saved and reset through persistent configuration APIs.
- Render unsupported or missing runtime fields as explicit disabled controls with explanatory text instead of failing the whole Runtime panel.
- Remove or replace Web UI calls to nonexistent settings endpoints so settings pages depend only on implemented backend contracts.
- Add backend and Web regression coverage for log-level success/failure behavior, runtime load states, partial unsupported fields, and save/reset outcomes.

## Capabilities

### New Capabilities
- `settings-system-diagnostics`: Covers Settings > System diagnostics behavior, including truthful log-level display, supported log levels, persistence, and explicit unavailable/error states.

### Modified Capabilities
- `agent-runtime`: Adds the user-visible runtime configuration contract for effective agent scheduling values, supported persistence, reset behavior, and explicit unsupported fields.
- `http-api`: Updates the settings/config API contract used to read and update log level and runtime configuration on the .NET server.
- `web-ui`: Updates Settings > System and Settings > Runtime requirements so the Web UI loads from supported APIs, shows real values, disables unsupported fields, and surfaces save/load failures.

## Impact

- Affects .NET server settings/config endpoints and tests around configuration reads and writes.
- Affects Web settings client code and Settings > System / Runtime UI behavior under `packages/web`.
- Affects API compatibility for internal Web UI settings calls by removing reliance on missing `/api/log-level` or `/api/agent-runtime` routes unless they are implemented as supported contracts.
- No new external dependencies are expected.
