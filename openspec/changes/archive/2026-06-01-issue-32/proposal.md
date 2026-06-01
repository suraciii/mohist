## Why

Mohist users cannot currently trust the System settings page to identify the server process that is actually running: `/api/system/info` is missing, git identity can show as unknown, and the existing rebuild action is intentionally unsupported. Local-source deployments also need a safe way to see when the checked-out source is newer than the running server and update the deployment without guessing at systemd state or shell commands.

## What Changes

- Add a formal System Runtime view that reports running server version, running git hash, startup time when available, source path, source branch, source HEAD, source dirty state, install mode, service manager, server unit, runner unit, update status, service status, and existing Mohist paths.
- Add `/api/system/info` as the typed source of truth for runtime identity, installation detection, local source state, service state, and update eligibility.
- Add `/api/system/update` to start a guarded local-source update job without accepting arbitrary paths, commands, unit names, or environment input from Web.
- Add `/api/system/update/status` so the Web UI can show update progress and recover status after the server restarts.
- Introduce stable runtime build identity captured once at server startup, preferring build metadata and falling back to local git HEAD in source-run development mode.
- Detect supported local-source deployments from the installed user systemd unit and the trusted `WorkingDirectory`, not from request input.
- Replace the existing System settings `Rebuild & Restart` affordance with `Update & Restart`, including eligibility, dirty-source, unsupported, progress, reconnect, and post-restart confirmation states.
- Persist update job state outside process memory so the restarted server can report the latest update result.

## Capabilities

### New Capabilities

- `system-runtime`: Runtime identity, install detection, local-source update eligibility, update execution status, and service state reporting for Mohist System settings.

### Modified Capabilities

- `http-api`: Adds typed System endpoints for runtime info, update start, and update status.
- `web-ui`: Extends Settings > System to display runtime/source/install/update state and perform eligible local-source updates.
- `server-daemon`: Extends daemon/service behavior expectations for detecting local-source systemd installs and restarting trusted Mohist services during update.

## Impact

- Backend API surface under `packages/server/src/Mohist.Server/Api` for `/api/system/info`, `/api/system/update`, and `/api/system/update/status`.
- Backend services for runtime build metadata, trusted systemd install detection, git source inspection, service status checks, and persisted update job state.
- Update execution path that runs only the fixed local-source build/restart flow for the detected Mohist systemd installation.
- Web API client, settings types, hooks, and Settings > System UI under `packages/web`.
- Tests covering runtime/source info, unsupported installs, local-source detection, dirty source reporting, update availability, update command safety, concurrent update rejection, persisted update status, and System page rendering states.
