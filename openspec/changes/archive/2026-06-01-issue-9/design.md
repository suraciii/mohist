## Context

Mohist already has runner concepts in the backend runtime, but the user-facing surfaces are incomplete. Runner registration receives `RunnerInfo` with runner identity, kind, hostname, capabilities, optional project scope, and coder models. Runtime grains also know heartbeat freshness, SignalR connection state where applicable, active or leased workflow work, and capacity-related state. Today most of that information is only implicit, visible through logs, or compressed into `/api/agent/status` as availability plus a minimal runner list.

The selected-project UI needs a stable way to answer whether runner capacity exists, whether it is idle or busy, which host/scope it belongs to, and what work is currently assigned. The design must keep runner terminology separate from agent sessions: a runner is the execution host, a connection is the live SignalR channel, a heartbeat is the liveness signal, scope describes which project the runner serves, and active work is the leased workflow work item.

Primary stakeholders are users operating Mohist from the Web UI, backend workflow/runner code that owns the source state, and existing clients that still consume `/api/agent/status`.

Key constraints:

- Web clients must not assemble rows by querying individual runner grains directly.
- The read model must include global runners and selected-project runners, while excluding runners scoped only to other projects.
- The API must avoid exposing secrets, environment details, tokens, or agent transcript content.
- Existing `/api/agent/status` behavior must remain compatible or be migrated with tests.

## Goals / Non-Goals

**Goals:**

- Add a stable project-scoped runner status read model backed by server-side projection.
- Expose runner rows with identity, host, scope, liveness, heartbeat, connection state, capabilities, coder models, capacity/slot data where available, and active work summaries where available.
- Include both global runners and runners scoped to the selected project.
- Add a stable HTTP endpoint, preferably `GET /api/runners`, for Web UI consumption.
- Preserve `/api/agent/status` compatibility for existing consumers.
- Add Web UI surfaces that clearly distinguish no runner, connected idle runner, and connected busy runner states.
- Preserve the current board no-runner banner and point it to the detailed runner status surface.
- Cover backend projection/API behavior and Web empty/idle/busy rendering with regression tests.

**Non-Goals:**

- No runner start, stop, restart, install, or management actions from the Web UI.
- No exposure of environment variables, local secrets, tokens, or agent session transcripts.
- No direct Web UI dependency on individual runner grains.
- No redesign of workflow leasing, runner scheduling, or SignalR connection protocols beyond the fields needed for status projection.
- No replacement of agent session views; runner status summarizes execution hosts, not transcript details.

## Decisions

### Decision 1: Add A Dedicated Runner Status Projection Service

Create a backend query/projection service that returns `RunnerStatusView` rows for a selected project. The service should read eligible runner registration records from `RunnerRegistryGrain`, then enrich each row with runtime state from the corresponding runner runtime grain/service before mapping to a UI-facing DTO.

Rationale: this keeps the UI decoupled from Orleans grains and centralizes filtering, liveness policy, field naming, and secret redaction in one server-side boundary.

Alternatives considered:

- Let the Web UI query registry plus runner grains separately. Rejected because it leaks runtime topology to the client, duplicates projection logic, and violates the spec requirement.
- Extend only `/api/agent/status` with detailed rows. Rejected because the endpoint name and existing shape use agent terminology and are already a compatibility surface.
- Persist a separate runner read table immediately. Deferred because the data is currently runtime-derived and the smallest correct change is a live projection; persistence can be added later if stale/offline history becomes a product requirement.

### Decision 2: Extend Runner Registry Queries To Return Registration Info

Extend `RunnerRegistryGrain` to expose registered `RunnerInfo` entries or an equivalent safe registry DTO, not only runner ids. The registry query should support selected-project filtering by returning global runners and runners scoped to the requested project, excluding other project-scoped runners.

Rationale: registration data is the authoritative source for runner id, kind, hostname, capabilities, project scope, and coder models. The projection layer should not infer these fields from runtime grain ids.

Alternatives considered:

- Keep registry methods unchanged and query each runner for static metadata. Rejected because it scatters registration ownership and makes missing/offline runners harder to represent.
- Return every known runner to the API route and filter there. Rejected because scope filtering belongs in the server read model, not in presentation code.

### Decision 3: Use A Stable Runner DTO With Explicit Safe Fields

Define a UI-facing response such as:

```json
{
  "runners": [
    {
      "id": "runner-455532",
      "kind": "external",
      "hostname": "devbox",
      "scope": { "type": "project", "projectId": "...", "projectName": "..." },
      "status": "idle",
      "registeredAt": "...",
      "lastHeartbeatAt": "...",
      "connectionState": "connected",
      "capabilities": ["workflow", "workspace-query"],
      "coderModels": ["openai/gpt-5.5"],
      "coderModelCount": 1,
      "capacity": { "usedSlots": 0, "totalSlots": 1 },
      "activeWork": null
    }
  ]
}
```

`status` should be derived server-side using heartbeat and active work state, with values that distinguish at least `offline` or `stale`, `idle`, and `busy`. A runner with a fresh heartbeat and active or assigned workflow work is `busy` even if a workspace-query SignalR connection is currently disconnected; `connectionState` remains a separate transport diagnostic. Unknown fields should be omitted or `null`, rather than fabricated.

Rationale: explicit DTO fields make the API stable for Web tests and future UI work while limiting accidental exposure of internal runtime state.

Alternatives considered:

- Return raw `RunnerInfo` plus raw grain state. Rejected because it would expose internal shape and make redaction/liveness semantics unclear.
- Return only aggregate counts. Rejected because users need host, scope, capabilities, models, and active work diagnostics.

### Decision 4: Add `GET /api/runners` And Keep `/api/agent/status` Compatible

Add `GET /api/runners` as the stable runner status endpoint for the selected project. Keep `/api/agent/status` parseable by existing clients and preserve its current runner availability/minimal runner fields. It may internally reuse the same projection service for availability, but should not remove existing fields without an explicit compatibility migration.

Rationale: `/api/runners` uses correct domain language and can evolve as the runner status surface. `/api/agent/status` remains a compatibility endpoint for older consumers.

Alternatives considered:

- Put detailed rows under `/api/agent/runners`. Acceptable but less precise because the issue explicitly says not to call runners agents.
- Replace `/api/agent/status` with the new shape. Rejected because the acceptance criteria require compatibility or tested migration.

### Decision 5: Add A Compact Summary Plus Detailed List In The Web UI

Add a compact runner status summary in a stable existing surface, preferably the top status bar or Activity overview, and add a detailed list in Activity or Settings. The summary should derive its state from the runner rows: empty means no runner capacity, fresh heartbeat with no active work means idle, and fresh heartbeat with active work means busy. A disconnected workspace-query connection should be shown as a row diagnostic, but it should not erase busy capacity while the runner still heartbeats and holds active work. Stale/offline rows should remain visible in the detailed list with heartbeat diagnostics but should not count as connected capacity.

Rationale: users need quick confidence from the main UI and enough detail to diagnose host/scope/model/capacity issues without logs.

Alternatives considered:

- Only add a detailed Settings page. Rejected because users still need a visible top-level signal for runner capacity.
- Only update the board banner. Rejected because the issue asks for a status surface and detailed runner list beyond the absence warning.

### Decision 6: Preserve The Board No-Runner Banner And Link To Status

Keep the existing board warning when no connected runner can serve the selected project. Update the banner copy or action to point to the runner status view for details and startup/install guidance. Busy runners should suppress the no-runner banner because capacity exists even if currently assigned; if their workspace-query connection drops, the detailed runner row should expose that connection diagnostic separately.

Rationale: the banner remains useful at the point where users notice work cannot proceed, while the detailed surface explains why.

Alternatives considered:

- Remove the board banner after adding status UI. Rejected because the proposal explicitly preserves it.
- Treat busy as no capacity. Rejected because busy is different from disconnected; the UI must distinguish connected busy runners from no runner state.

## Risks / Trade-offs

- [Risk] Heartbeat and SignalR connection state may disagree temporarily -> Mitigation: define status precedence in the projection service and show both raw diagnostics when known.
- [Risk] Live projection can be slower if it queries many runner grains -> Mitigation: have `RunnerRegistryGrain` pre-filter eligible runners and keep enrichment bounded to returned runners; add caching later only if needed.
- [Risk] Stale registered runners can make users think capacity exists -> Mitigation: mark stale/offline distinctly and exclude them from connected-capacity summary logic.
- [Risk] Active work data may not always be available or may contain internal ids only -> Mitigation: expose a concise nullable active work reference and avoid transcript/session details.
- [Risk] Existing consumers of `/api/agent/status` could break if response fields move -> Mitigation: preserve existing fields and add compatibility regression tests before changing consumers.
- [Risk] Capability or model metadata could accidentally include sensitive values in the future -> Mitigation: map only allowlisted fields from registration/runtime state into `RunnerStatusView`.

## Migration Plan

1. Extend runner registry query behavior to return safe registered runner info for global and selected-project runners.
2. Add `RunnerStatusView` DTOs and a projection/query service that enriches registry rows with runtime runner state.
3. Add backend tests for projection shape, selected-project/global filtering, other-project exclusion, empty responses, active work, stale/offline state, and secret-safe field mapping.
4. Add `GET /api/runners` using the projection service.
5. Update `/api/agent/status` to keep existing runner availability/minimal runner fields compatible, reusing projection data only where it does not change the public contract.
6. Add Web API client types/hooks for runner status.
7. Add the compact runner summary and detailed runner list, including empty-state startup/install command guidance.
8. Update the board no-runner banner to point to the runner status surface and use connected-capacity semantics.
9. Add Web tests for empty, idle, and busy runner rendering and board banner behavior.

Rollback strategy: the new endpoint and UI surfaces can be removed or hidden without changing workflow execution. `/api/agent/status` should remain compatible throughout, so rollback should not require client migration. If projection enrichment causes runtime issues, the endpoint can return registry-only rows with unknown runtime fields while preserving the response shape.

## Open Questions

- What exact liveness threshold should classify a runner heartbeat as stale or offline?
- Which existing Web surface should own the detailed list first: Activity or Settings?
- What startup/install command hint should be shown in empty states for the current runner distribution path?
- Should project scope include a project display name immediately, or only project id until a project lookup is already available in the projection path?
- What capacity fields are currently reliable enough to expose: total slots, used slots, queued work, or only active work count?
