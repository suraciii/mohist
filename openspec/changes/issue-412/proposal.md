## Why

An event knows who emitted it (`source`) and what happened (`type`), but not which business chain it belongs to — which issue, which epic, which workflow run, which stage. So the most natural supervision ask — "subscribe to everything happening under issue #42" — cannot be expressed: lineage is scattered across `source` paths, `subject`, and `data`, never as matchable envelope attributes. The design's three-axis envelope (`design/event-protocol.md`) already specifies a stamping matrix and naming for this third axis; this issue fills the matrix in at every producer and raises `EventCatalog` into the protocol registry that enforces it. It is the prerequisite for expression-based subscription (a later issue) — without stamped lineage there is nothing for an expression to match.

## What Changes

- **Lineage stamped on every event family at production time**, per the matrix in `design/event-protocol.md:59-70`. Stamping uses only identity the aggregate already holds (own state or existing annotations/labels) — producers SHALL NOT issue cross-aggregate queries to stamp.
  - `workflow.*`: add `workflowrunid` and `issue` (issue number); `projectid`/`issueid`/`issue`/`workflowrunid` printed when present, omitted when absent.
  - `workflow.stage.*` / `workflow.task.*` / `workflow.check.*` / `workflow.feedback.requested`: additionally print `stage` — the value is already carried on these structurally stage-bearing event records but never lifted to the envelope today.
  - `issue.*`: add `epicid` when the issue belongs to an epic. Because the Issue aggregate does not own epic membership, `epicid` is denormalized onto Issue state by the Epic domain at link/unlink and stamped from the issue's own state — no cross-aggregate query at stamp time (design D5).
  - `agent-session.*`: stamp `projectid`, `sessionid` always, `agentid` for agent-origin sessions, and `issue`/`workflowrunid`/`stage` when the session originates from a workflow/issue (all already present in `Metadata.Labels`).
  - `epic.*` and the inbox-synthesized event are brought under the same matrix.
- **Lineage is a production-time snapshot.** Attributes record affiliation at emit time; later relationship changes do not rewrite history. Absent affiliation is omitted — never an empty string.
- **BREAKING — lineage attribute names unify to the design protocol.** The short user-visible name becomes `issue` (not the current `issueno`), and `epicno` is removed (epic routes on `epicid`). This name is what users write in expressions, so it must be stable before subscription lands. Reconciliation of the three server handlers and web reads that key on `issueno`, and of already-persisted rows, is scoped here (mechanics in design.md; history is not rewritten, per Non-Goals).
- **`EventCatalog` rises from a flat type list to the protocol registry.** Each registered type declares the lineage attributes it must carry (`EventCatalog.cs` is name-only today).
- **Conformance check guards the matrix.** A spec test walks every event production path and asserts the emitted envelope satisfies the catalog's declared required attributes; adding an event type and forgetting its lineage fails the check.

## Capabilities

- `event-lineage-stamping`: Every event family SHALL stamp its full business lineage (issue number / issue id / epic id / workflow run id / agent id / session id / runner id / project id / stage, as applicable per the matrix) into envelope extensions at production time, using only identity the aggregate already holds. Absent affiliation SHALL be omitted, not empty. Attributes SHALL be a production-time snapshot and SHALL NOT be rewritten on later relationship changes. Producers SHALL NOT issue cross-aggregate queries to stamp lineage.
- `event-catalog-conformance`: `EventCatalog` SHALL declare, per registered event type, the lineage attributes that type must carry. A conformance check SHALL exercise every event production path and fail when an emitted envelope is missing a required attribute.

## Impact

- **Producer stamping sites** (all server-side; runner does not emit into this envelope system):
  - `Infrastructure/Data/Workflow/WorkflowRunStore.cs:81-103` — stamps `projectid`+`issueid` only today; add `workflowrunid`, `issue` (from annotations), and `stage` (from the event record for stage/task/check events).
  - `Infrastructure/Data/Issue/IssueStore.cs:130-138` — stamps `projectid`/`issueid`/`issueno`; rename `issueno` → `issue` and stamp `epicid` from the issue's own `EpicId` state (added by this change; written by the Epic domain at link/unlink).
  - `Issue/Domain/Issue.cs` + `Issue/Grains/IIssueGrain.cs` / `IssueGrain.cs` — `Issue` gains a nullable `EpicId` (the same cross-aggregate-reference pattern it already uses for `WorkflowRunId`) and an eventless `SetEpicId` transition; `IIssueGrain.SetEpicAffiliationAsync` persists it via the state-only save (no domain event — it is a denormalization).
  - `Epic/Grains/EpicGrain.cs:1098-1103,1148-1153` — stamps `projectid`/`epicid`/`epicno`; drop `epicno` (not a routing dimension per the matrix). The link/unlink paths additionally push `EpicId` onto the linked issue via `IIssueGrain.SetEpicAffiliationAsync` (D5 denormalization), and a one-time backfill sets `Issue.EpicId` from `EpicIssueRow` for already-linked issues.
  - `Infrastructure/Data/Sessions/AgentSessionStore.cs:118-131` — passes `extensions: null` today; project `Metadata.Labels` (`project-id`, `agent-id`, `agent-launch/issue-number`, `source-kind`, `work-id`, `stage`) onto extensions.
  - `Events/Subscriptions/InboxProjectionHandler.cs:148-167` — synthesizes `inbox.item-persisted` stamping only `projectid`; lift `issue`/`issueid` already present in the hint payload.
- **Catalog / registry**:
  - `Infrastructure/Events/EventCatalog.cs:8-100` — gains per-type required-attribute declarations (currently a flat `IReadOnlyList<string>` of names).
- **Consumers of the renamed `issueno`** (read path updates during reconciliation):
  - `Events/Subscriptions/EpicAutoDoneHandler.cs:374,388`, `Events/Subscriptions/HermesIssueNotificationHandler.cs:163`, `Events/Subscriptions/InboxProjectionHandler.cs:213`.
  - `packages/web/src/app/providers/model/event-envelope.ts:28-29` (doc/read), and web test fixtures using `epicno`/`issueno`.
- **Persistence**: no schema migration — lineage rides in the existing `ExtensionsJson` JSON column present on all four event tables since `20260609154024_AddWorkflowRunEvents`. Historical rows keep their already-stamped attributes (Non-Goal: no backfill).
- **Conformance tests**: new spec covering all production paths; no web/CLI/runner contract change.
