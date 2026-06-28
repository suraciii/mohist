## Context

The CLI exposes no entry point for three "look before you act" queries that the server already answers via stable endpoints consumed by the Web UI:

| Need | Endpoint | Scope |
|---|---|---|
| System diagnostics (version/source/install/update/services/paths) | `GET /api/system/info` (`SystemRoutes.cs:12`) | **Global** |
| Available coder models (to feed `--model`) | `GET /api/projects/{projectRef}/opencode/models` (`OpencodeRoutes.cs:16`) | **Project-scoped** |
| Online runner summary (id/heartbeat/idle-busy) | `GET /api/projects/{projectRef}/runners` (reused by `mo runner list`) | **Project-scoped** |

The issue body describes the latter two as global (`/api/opencode/models`, `/api/runner-status`), but **code verification shows both are project-scoped** (`/api/projects/{projectRef}/opencode/models` in `OpencodeRoutes.cs:13`, and the runner list endpoint that `PrintRunnerListAsync` already consumes). Only `/api/system/info` is genuinely global. This discrepancy is already captured in the proposal's Impact section and the spec's project-resolution scenarios, so the design follows the **actual** route scope.

Two existing commands overlap with the new ones and must be disambiguated:

- **`mo info`** (`InfoCommands.cs`) — reports the *CLI binary's own* environment/install source via `InfoCollector` (local). `mo system info` reports *server-side* diagnostics (remote). Different data sources; help text must distinguish.
- **`mo runner list`** (`MohistCliCommands.Server.cs:133`) — already hits the same runner endpoint and renders a full-detail table (id/kind/status/scope/capacity/heartbeat/hostname). `mo runner status` is a **focused online/idle summary**, not a duplicate.
- **`mo runner status` (existing)** (`MohistCliCommands.Server.cs:124`) — shells out to `systemctl`/scheduled-task status for the runner *service unit*. This collides with the desired online-diagnostic name. The spec resolves it by **renaming the service-lifecycle verb to `mo runner service-status`** and taking `mo runner status` for the online diagnostic.

**Stakeholders:** single-user local-first system. The project is "actively in development, no version compatibility concerns" (AGENTS.md), so a breaking rename of the service-lifecycle verb is acceptable.

## Goals / Non-Goals

**Goals:**
- Add `mo system info` (global) with graceful degradation when the server is offline, distinct from client-local `mo info`.
- Add `mo opencode models` (project-scoped) printing one model ID per line in table mode for direct copy-paste into `--model`.
- Add `mo runner status` (project-scoped) as a focused online/idle runner summary, distinct from the full-detail `mo runner list`.
- Rename the existing service-lifecycle `mo runner status` → `mo runner service-status` (behavior identical).
- All three new commands support `-o table|json`; JSON mode emits the raw server payload.

**Non-Goals:**
- No server-side changes, no new endpoints, no write operations.
- No Web UI changes.
- No rename/consolidation of `mo info` vs `mo system info` (different data sources; disambiguation via help only).
- No `mo label list` (owned by epic #8 / #151).
- No `mo config models` alias (use `mo opencode models`).
- No `--variants` rendering for `mo opencode models` table mode (variants appear only in raw JSON payload).

## Decisions

### Decision 1: Place `system` and `opencode` as new top-level command groups

**Choice:** Add two new top-level groups in `MohistCliCommands.Build` (`MohistCliCommands.cs:10`): `SystemCommands.Build(api)` and `OpencodeCommands.Build(api)`, each exposing the single verb. `mo system info` and `mo opencode models` are registered as their group's first subcommand, leaving room for future verbs (e.g. `mo system update` already has an API; `mo opencode runtime`).

**Rationale:** The proposal explicitly requests top-level placement for both (high-frequency queries). A `system` group mirrors the server's `/api/system/*` surface and is forward-compatible with the existing `POST /api/system/update` endpoint. An `opencode` group mirrors `/api/opencode/runtime` + the project-scoped `/opencode/models`.

**Alternatives considered:**
- Fold `mo system info` into the existing `mo status` (`/api/status?all=true`) — rejected: `status` is an aggregate health snapshot; `system info` is fine-grained diagnostics (different attention, per the issue's own framing).
- Name the model command `mo config models` — rejected by the proposal; `mo opencode models` is more discoverable.

### Decision 2: `mo system info` uses a dedicated render method, not a `TableShape`

**Choice:** Add `MohistCliApi.PrintSystemInfoAsync(string mode)` (mirroring the structure of `PrintRunnerShowAsync` in `MohistCliApi.cs:285`) and a `RenderSystemInfo(JsonObject data)` helper that walks the six nested sections (`running/source/install/update/services/paths`) from `SystemInfoResponse` (`SystemInfoDtos.cs:87`) and prints key/value pairs grouped by section header — the same style as `RenderRunnerShow` (`MohistCliApi.cs:341`). In JSON mode, print `node["data"]` verbatim (raw payload, no omission). This bypasses the `TableShape` dispatch because system info is a single structured object, not a row list.

**Rationale:** `RenderRunnerShow` is the established pattern for single-object, multi-section, key-value rendering. Forcing a nested object through the tabular `TableShape` mechanism would produce an awkward one-row table or require a special case anyway. A dedicated renderer keeps the `SystemInfoResponse` shape legible (identity / source / install / update / services / paths sections).

**Alternatives considered:**
- Add a `TableShape.SystemInfo` branch — rejected: `TableShape`/`Render` is built for array-of-rows; a single nested object is a poor fit and would special-case the dispatch.
- Reuse the generic JSON fallback for table mode too — rejected: the spec requires section-structured table rendering, not a raw dump.

### Decision 3: `mo system info` graceful degradation prints CLI version + notice, never hard-fails

**Choice:** `PrintSystemInfoAsync` wraps the `GET /api/system/info` in try/catch for `HttpRequestException` (server down). On failure it: (1) prints `"Server is not running. Start with: mo server start"` to `_err`, (2) prints a minimal locally-derivable subset to `_out` — CLI version (from assembly / `RuntimeBuildInfo`) and a header noting the server is unreachable, (3) returns exit code `0` (degraded-but-informative), matching the spec's "SHALL NOT abort with only a hard error". In JSON mode under degradation, print the partial object as JSON so scripts still get parseable output.

**Rationale:** The spec scenario ("Server unreachable degrades gracefully") explicitly forbids a bare hard error. The CLI version is cheap to derive locally (no InfoCollector dependency needed) and is the single most useful "what am I running" datum when the server is down. Returning `0` on degradation lets `mo system info` be chained in scripts that only want the local view.

**Alternatives considered:**
- Reuse `InfoCollector` (the `mo info` machinery) for the local subset — rejected: `InfoCollector.CollectAsync` is a heavier, verbose-capable collector with git/skills/env probes; coupling it here pulls in unrelated dependencies for a single version string.
- Return non-zero on degradation — rejected: contradicts the spec's "SHALL NOT abort with only a hard error and no diagnostic output" intent and breaks diagnostic chaining.

### Decision 4: `mo opencode models` table mode prints bare IDs, JSON mode prints raw payload

**Choice:** Add `OpencodeCommands.Build` with a `models` verb carrying `--project`/`--project-id`/`-o` (via `MohistCliCommands.ProjectRefOption()` + `OutputOption`). The action resolves the project via `ResolveProjectIdAsync` (same as `mo runner list`), then calls a new `PrintOpencodeModelsAsync(projectId, mode)`. In table mode it iterates `data.models` and writes **one ID per line with no header/decoration** (directly copy-pasteable into `--model`). In JSON mode it prints the full `data` object (`{models, modelVariants}`) verbatim — preserving variant info the spec requires.

**Rationale:** The endpoint returns `{models, modelVariants}` (`OpencodeRoutes.cs:29`). Table mode's copy-paste contract (spec: "no extra per-row decoration") rules out a tabular renderer. JSON mode must keep `modelVariants` (spec: "any model-variant information the server returns"), so it cannot be projected down to just `models`.

**Alternatives considered:**
- Render modelVariants in table mode as a second column — rejected: violates the one-ID-per-line copy-paste contract and expands scope (no `--variants` flag in the spec).
- Project down to `models[]` in JSON mode too — rejected: loses variant data the spec explicitly preserves.

### Decision 5: `mo runner status` is a focused online/idle summary reusing the runner endpoint but a new renderer

**Choice:** Add a new `BuildStatus(api)` to `RunnerCommands` (`MohistCliCommands.Server.cs:114`) that, like `BuildList`, carries `--project`/`--project-id`/`-o`. It calls a new `PrintRunnerStatusAsync(projectId, mode)` which fetches the same `GET /api/projects/{projectId}/runners` endpoint but renders via a new `RenderRunnerStatus` (added to `TableRenderer.Runners.cs`) with a narrow column set: **id | heartbeat | state**, where `state` is `idle` when `capacity.usedSlots == 0` else `busy`. Empty list prints "No runners connected" and exits `0`. JSON mode prints the raw runner payload.

**Rationale:** The spec requires `mo runner status` to "focus on the online/heartbeat/idle summary and SHALL remain distinct from `mo runner list`". Reusing the endpoint avoids a new server call; the distinction is purely in the view (3 columns vs 7, idle/busy derivation vs raw status). `RenderRunnerList` already has a `FormatHeartbeat` helper (`TableRenderer.cs:201`) that `RenderRunnerStatus` reuses for the heartbeat column.

**Alternatives considered:**
- Make `mo runner status` an alias of `mo runner list` — rejected: the spec mandates a distinct focused view, not the full-detail table.
- Add a separate narrow server endpoint — rejected as a non-goal (no server changes); the data is already present.
- Derive idle/busy from the runner `status` field instead of capacity — rejected: the spec defines idle as "used capacity is zero"; the `status` field can be `stale`/`offline` which is not the same axis.

### Decision 6: Rename the service-lifecycle `mo runner status` → `mo runner service-status`

**Choice:** In `RunnerCommands.Build` (`MohistCliCommands.Server.cs:124`) change `BuildSystemd("status", installer.StatusRunnerAsync, installer)` to `BuildSystemd("service-status", installer.StatusRunnerAsync, installer)`, and insert the new online-diagnostic `BuildStatus` under the freed `status` name. The `service-status` verb retains identical `--dry-run`/`--unit-dir` options and the same `StatusRunnerAsync` handler (`IServiceInstaller.cs:18`) — pure rename, zero behavior change. `mo runner --help` will then list both verbs with distinct descriptions.

**Rationale:** The spec resolves the collision in favor of the online diagnostic taking the intuitive `status` name. This is a **breaking rename**, but AGENTS.md states the project is in active development with no version-compatibility obligation, and `mo runner service-status` is a low-frequency lifecycle probe (the user runs it when debugging systemd), so the blast radius is small.

**Alternatives considered:**
- Name the online diagnostic differently (e.g. `mo runner online`, `mo runner ps`) and leave the service-lifecycle `status` intact — rejected by the spec, which mandates the online diagnostic own `status` (more intuitive for the frequent "are runners idle?" query).
- Keep both under `status` with a `--service` flag — rejected: ambiguous verb semantics, worse discoverability than two clearly-named verbs.

## Risks / Trade-offs

- **[Breaking `mo runner status` rename]** -> Users with scripts calling `mo runner status` for systemd status will break. Mitigation: breaking change is acceptable per AGENTS.md; `mo runner --help` lists `service-status` clearly; the rename is pure (same flags/handler) so migration is a find-and-replace.
- **[`mo runner list` vs `mo runner status` overlap]** -> Two commands hitting the same endpoint could confuse users about which to use. Mitigation: distinct help text (`list` = "full-detail runner table"; `status` = "quick online/idle summary"); the focused 3-column output of `status` is visually distinct from the 7-column `list`.
- **[`mo system info` degradation shows stale/misleading local data]** -> When the server is down, the CLI version printed may not match the server's version (e.g. after a `mo update` that rebuilt the server but not the CLI). Mitigation: the "server not running" notice is prominent and the local subset is explicitly framed as CLI-only; users reading diagnostics understand the source.
- **[Endpoint-scope mismatch with issue body]** -> The issue body claims global endpoints; the actual routes are project-scoped for models/runners. Mitigation: already reconciled in the proposal/spec (project resolution required); the design follows actual routes. No user-facing surprise since the commands require `--project` exactly like peer commands.
- **[`mo opencode models` table omits variants]** -> Power users wanting variant info in terminal must use `-o json`. Mitigation: documented in help; a future `--variants` flag is a trivial extension but out of scope.

## Migration Plan

This is a **pure CLI change** (`packages/cli/Mohist.Cli/` + `packages/cli/tests/`) — no server, database, or Web UI migration.

**Deploy steps:**
1. Add `SystemCommands` (new file `MohistCliCommands.System.cs`) with `PrintSystemInfoAsync` + `RenderSystemInfo` (Decision 2/3).
2. Add `OpencodeCommands` (new file `MohistCliCommands.Opencode.cs`) with `PrintOpencodeModelsAsync` (Decision 4).
3. Add `BuildStatus` to `RunnerCommands` + `PrintRunnerStatusAsync` + `RenderRunnerStatus` in `TableRenderer.Runners.cs` (Decision 5).
4. Rename the service-lifecycle verb to `service-status` in `RunnerCommands.Build` (Decision 6).
5. Register `SystemCommands.Build` and `OpencodeCommands.Build` in `MohistCliCommands.Build`.
6. Add xUnit specs in `packages/cli/tests/Mohist.Cli.Tests/` using the `RecordingHttpHandler` + `RunAsync` pattern (table/json rendering, graceful degradation, project resolution, empty-runner case, help-text disambiguation).
7. Verify: `npm test` (server TreatWarningsAsErrors acts as lint; CLI tests run in the same suite) passes.

**Rollback:** Revert the CLI commit(s). No persisted state is touched — the rename does not migrate any config, and the new commands only read existing endpoints. `mo runner service-status` reverts to `mo runner status` (service-lifecycle).

## Open Questions

- **`mo system info` JSON degradation shape:** when the server is down and JSON mode is requested, should the partial payload be a structured object (`{running: null, cliVersion: "..."}`) or a minimal `{error, cliVersion}`? Current design: structured object mirroring `SystemInfoResponse` with nullable server fields + a `cliVersion` extra, so JSON consumers can rely on a stable shape. Confirm this is preferred over an error envelope.
- **Idle/busy derivation edge case:** `RenderRunnerStatus` derives `state` from `capacity.usedSlots`. If a runner reports no `capacity` object (offline), should `state` show `offline`/`unknown` or be omitted? Current design: show `unknown` (consistent with `FormatCapacity` returning `-` for missing capacity). Confirm.
- **`mo opencode models` help wording:** should help mention that the listed IDs are directly usable as `mo issue update --model <id>` (cross-referencing the Issue-CLI-completeness sibling issue)? Current design: keep help self-contained; the copy-paste contract is implied by one-ID-per-line.
