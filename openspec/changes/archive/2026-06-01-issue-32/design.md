## Context

Mohist currently has partial and inconsistent runtime reporting. `/api/system/info` is missing, `/api/status` exposes only partial version/source data, and the Web System page already contains an About/Rebuild shape that shows `unknown` values and intentionally rejects rebuild attempts. This leaves users unable to verify which server process is running or whether a local source checkout contains newer code than the deployed server.

Issue 32 introduces a trustworthy System Runtime view and a guarded local-source update path. The implementation spans the ASP.NET Core backend, user systemd install detection, git/source inspection, persisted update job state, and the Settings > System Web UI. The key constraint is safety: Web requests must not provide commands, paths, unit names, or environment values. All update decisions must be derived from trusted server-side installation facts.

The primary stakeholders are local-source Mohist users, who need a safe update button from the Web UI, and operators of non-local-source deployments, who need clear unsupported-state messaging rather than misleading controls.

## Goals / Non-Goals

**Goals:**

- Add typed System APIs for runtime identity, source state, install mode, update eligibility, service state, system paths, update start, and update status.
- Capture `running.version` and `running.gitHash` once at server startup so the running identity remains stable until process restart.
- Detect supported local-source installs from the trusted user systemd unit shape and source repository facts.
- Dynamically inspect the trusted source repository on each System info request so `source.head` can advance independently of the running process hash.
- Provide a guarded `Update & Restart` flow that builds the trusted source tree, restarts Mohist user systemd services, waits for readiness, and persists status across restart.
- Replace the Web UI placeholder rebuild action with a typed update mutation and reconnect-aware progress display.

**Non-Goals:**

- Supporting arbitrary deployment managers beyond the first `systemd --user` local-source path.
- Accepting user-supplied repository paths, commands, service names, or environment overrides from Web requests.
- Updating dirty local-source trees in the first iteration; dirty state is surfaced and blocks update.
- Implementing package-manager, binary, container, or remote update mechanisms.
- Replacing `/api/status`; the new `/api/system/info` becomes the typed source of truth for System settings.

## Decisions

### 1. Add a backend System domain instead of extending ad hoc status responses

Create backend types and services under the existing server project, for example `System/RuntimeBuildInfo`, `System/SystemInfoService`, `System/SystemUpdateService`, `System/SystemdInstallDetector`, `System/GitSourceInspector`, and `System/UpdateJobStore`. Expose them through new Minimal API routes under `/api/system`.

Rationale: runtime identity, install detection, git inspection, service status, and update execution are related operational concerns and should have one typed composition point. This keeps `/api/status` from accumulating partially overlapping fields and gives the Web UI a stable contract.

Alternatives considered: extend `/api/status` with all fields. This was rejected because `/api/status` already has partial semantics and does not naturally model update jobs or persisted progress.

### 2. Capture running build identity once at startup

Add `RuntimeBuildInfo` as a singleton initialized during server startup. It should prefer assembly informational version or generated build metadata for version and git hash. When build metadata is unavailable and the process is running from a local source checkout, it may fall back to reading git HEAD once. The captured hash is immutable for the lifetime of the process.

Rationale: update availability depends on comparing a fixed running hash with a dynamic source HEAD. Reading git HEAD on every request for the running identity would make the running server appear updated before restart.

Alternatives considered: keep relying on `MOHIST_GIT_HASH`. This was rejected because it is optional and currently produces `unknown`/null values in local-source runs.

### 3. Detect local-source install from trusted systemd facts

Classify `install.mode = local-source` only when the user systemd `mohist.service` exists, has a `WorkingDirectory` that contains `Mohist.sln`, and has an `ExecStart` source-run shape such as `dotnet run --project ...Mohist.Server.csproj`. Treat absent or non-matching units as `binary` or `unknown`, with Web update unsupported.

Rationale: the trusted source path must come from installation state, not from the browser. Validating both the repo marker and command shape prevents arbitrary directory updates.

Alternatives considered: infer source path from the current process working directory. This is weaker because the process directory can differ from the deployment unit and does not prove that systemd restart will use that source tree.

### 4. Compute update status from install facts, config gate, source state, and running hash

`GET /api/system/info` should compose the latest runtime payload by reading stable `RuntimeBuildInfo`, re-detecting or caching install facts with safe refresh, inspecting the trusted source repo dynamically, checking service status, and applying update eligibility rules. Status values should map to `up-to-date`, `update-available`, `dirty-source`, `unsupported`, or `unknown`, with a user-facing reason.

Rationale: the Web UI should not duplicate business rules for eligibility. It should render the server-provided status and only use simple display conditions such as showing the button for `local-source` plus `update-available`.

Alternatives considered: let the Web UI compute availability from raw fields. This was rejected because safety and supportability rules belong on the server and may evolve.

### 5. Gate update execution and use only fixed commands

`POST /api/system/update` starts a job only when System update is enabled, the detected install is supported local-source, the trusted repository is clean, and `source.head != running.gitHash`. The request body should be empty or ignored for execution decisions. The job runs only the fixed flow: validate install again, validate repo path against systemd `WorkingDirectory`, run `dotnet build Mohist.sln` in that repo, restart `mohist.service` via `systemctl --user restart mohist.service`, wait for `/api/health`, `/`, and referenced `/assets/*`, and restart `mohist-runner.service` when present.

Rationale: a fixed allowlist keeps the Web endpoint from becoming remote command execution. Revalidating immediately before execution reduces time-of-check/time-of-use drift.

Alternatives considered: call an existing CLI update command. This was rejected for the Web path because CLI arguments and shell environment are broader than the safe server-side contract needed here. Shared logic can still be extracted later if it preserves the same fixed inputs.

### 6. Persist update job state outside process memory

Store update job state in durable Mohist storage, preferably a small JSON or SQLite-backed record under the existing Mohist data directory. Persist job id, status, stage, bounded logs/messages, timestamps, target source hash, and final confirmation fields. The new server process reads this state through `GET /api/system/update/status` after restart.

Rationale: restarting the server is part of the update flow, so in-memory job state is insufficient. Durable state also lets the Web page recover after reload.

Alternatives considered: keep progress only in memory and let the browser infer success after reconnect. This was rejected because it cannot report failures around restart or confirm final hash equality reliably.

### 7. Allow only one update job at a time

Use a process-local guard plus persisted running-state check to reject concurrent update requests with `409 Conflict`. If a previous job is marked running but is stale after process restart, the status endpoint should surface the stale state clearly rather than starting a second job automatically.

Rationale: concurrent builds/restarts can corrupt user expectations and produce ambiguous status. A single active job is enough for the System settings use case.

Alternatives considered: queue update jobs. This adds complexity without product value because only the latest local-source update matters.

### 8. Bound logs and avoid sensitive output by default

Capture stage-level messages and bounded stdout/stderr tails for diagnostics. Do not expose full command output, environment variables, or unbounded logs from the default status payload.

Rationale: build output can be large and may contain local paths or sensitive context. The UI needs progress and actionable failure messages, not complete logs.

Alternatives considered: stream full process output to the Web UI. This was rejected for size, privacy, and persistence concerns.

### 9. Web UI uses `/api/system/info` and update status as source of truth

Extend Web types, API client, and hooks for `/system/info`, `/system/update`, and `/system/update/status`. Replace `useRebuildSystem()` with `useSystemUpdate()`. Settings > System should render runtime/source/install/service fields, short hashes with full hash tooltip or copy affordance, dirty/unsupported notes, and an `Update & Restart` button only for eligible update-available local-source installs.

Rationale: the current Web page shape is close, but its data source and mutation are placeholders. Keeping layout consistent minimizes UI churn while making the operational state accurate.

Alternatives considered: create a separate update page. This was rejected because update eligibility is part of System runtime state and belongs beside the existing settings paths and service information.

### 10. Reconnect flow is client-driven using health and refetches

After update start, the Web UI polls update status while available. During restart disconnect, it should poll `/api/health` until reachable, then refetch `/api/system/info` and `/api/system/update/status`. It reports success only when the refreshed `running.gitHash` equals `source.head` or when the backend persisted final confirmation says the same.

Rationale: the browser cannot rely on an uninterrupted request during server restart. Health polling and refetching typed state are simple and resilient.

Alternatives considered: use SSE for update progress. This can be added later, but polling is sufficient and survives process restart without extra stream recovery logic.

## Risks / Trade-offs

- [Risk] Systemd unit parsing may miss valid but unusual local-source installs -> Mitigation: start with conservative detection, return `unknown`/`unsupported` with clear reason, and add tests for the supported unit shapes.
- [Risk] Build succeeds but restart fails, leaving the old server running -> Mitigation: persist stage failure, keep service status visible, and require final hash confirmation before reporting Ready.
- [Risk] Server restarts before persisting the latest job stage -> Mitigation: persist stage transitions before executing restart commands and have the new process reconcile status from durable state.
- [Risk] Dirty source update is useful for developers but unsafe for a first Web update implementation -> Mitigation: surface dirty state clearly and block the button until an explicit future confirmation flow is designed.
- [Risk] Captured command output may leak sensitive local information -> Mitigation: store bounded logs, avoid environment capture, and show concise messages by default.
- [Risk] `source.head` and `running.gitHash` may be unavailable in non-git or metadata-less deployments -> Mitigation: report `unknown` status/reason and disable update instead of fabricating values.
- [Risk] Restarting runner after server readiness may temporarily report mixed versions -> Mitigation: include runner restart stage/status and keep final readiness tied to server hash confirmation.

## Migration Plan

1. Add backend DTOs and services for runtime build info, systemd install detection, git source inspection, service status, update status computation, durable update job state, and fixed update execution.
2. Register `/api/system/info`, `/api/system/update`, and `/api/system/update/status` in the server API surface.
3. Add configuration for `Mohist:SystemUpdate:Enabled`, defaulting to enabled only for supported local-source development installs or disabled where the install cannot be proven safe.
4. Add backend tests for runtime identity, local-source detection, unsupported installs, update availability, dirty source, fixed command execution, concurrent update rejection, and persisted update status.
5. Extend Web API types/hooks and replace the System page rebuild placeholder with the runtime/update UI.
6. Add Web tests for displayed fields, eligibility, dirty/unsupported states, progress stages, and reconnect/refetch behavior.
7. Deploy with unsupported states disabled by default for non-local-source installs. Rollback is straightforward: remove or hide the new Web update action and keep `/api/system/info` as read-only runtime reporting, or disable `Mohist:SystemUpdate:Enabled` to stop update starts without removing visibility.

## Open Questions

- Should durable update job state live in the existing SQLite database or a small JSON file under the Mohist data directory for the first implementation?
- What exact assembly metadata generation mechanism should provide version and git hash in packaged builds?
- Should `mohist-runner.service` restart happen before or after server readiness confirmation when both services are present?
- How long should a persisted `running` update job be considered stale after process restart before showing a recovery/failure state?
- Should a future iteration support explicit dirty-source confirmation, or should dirty local-source updates remain permanently unsupported from Web?
