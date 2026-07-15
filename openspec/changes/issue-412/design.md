## Context

The event envelope already has two stable axes — `type` (what happened) and `source` (who emitted) — but the third axis, **business lineage** (which issue / epic / workflow run / stage the event belongs to), is only partially stamped and inconsistently named. Today:

- `WorkflowRunStore.ToCloudEvent` stamps only `projectid`/`issueid` from run annotations (`Infrastructure/Data/Workflow/WorkflowRunStore.cs:81-103`); `workflowrunid`, `issue` (number), and `stage` are never lifted to the envelope.
- `IssueStore.BuildIdentityExtensions` stamps `projectid`/`issueid`/`issueno` (`Infrastructure/Data/Issue/IssueStore.cs:130-138`); `epicid` is absent, and the user-visible name is `issueno`, not the protocol name `issue`.
- `EpicGrain` stamps `projectid`/`epicid`/`epicno` (`Epic/Grains/EpicGrain.cs:1098-1103,1148-1153`); `epicno` is not a routing dimension.
- `AgentSessionStore.ToCloudEvent` passes `extensions: null` (`Infrastructure/Data/Sessions/AgentSessionStore.cs:118-131`) despite `Metadata.Labels` already carrying project/agent/workflow/issue/stage identity (`Sessions/Services/AgentSessionQueryMetadataKeys.cs`).
- `InboxProjectionHandler` synthesizes `inbox.item-persisted` with only `projectid` (`Events/Subscriptions/InboxProjectionHandler.cs:148-167`), ignoring the issue identity already in the hint.
- `EventCatalog` is a flat `IReadOnlyList<string>` of type names (`Infrastructure/Events/EventCatalog.cs:17-100`) — no per-type required-attribute declaration, no conformance.
- Read-side handlers key on `issueno` (`EpicAutoDoneHandler.cs:374,388`, `HermesIssueNotificationHandler.cs:163`, `InboxProjectionHandler.cs:213`) and the web envelope reader documents `issueno` (`packages/web/.../event-envelope.ts:28-29`).

The target protocol is `design/event-protocol.md:57-73` (the stamping matrix). Issue and WorkflowRun persist scalar `EpicId` producer snapshots, initialized from the affiliation visible during the schema migration; event rows remain immutable.

**Architectural constraint that drives the key decision:** Issue and Epic are separate aggregates (`design/architecture.md`, facts/decisions separation). The Issue aggregate's own state carries no epic reference today — epic↔issue membership lives in the `EpicIssueRow` join table owned by the Epic domain. The protocol forbids cross-aggregate queries for stamping, yet AC2 requires `epicid` on `issue.*` events; the only way to satisfy both is to give `Issue` a denormalized `EpicId` written by the Epic domain at link/unlink (D5) — the same cross-aggregate-reference pattern `Issue` already uses for `WorkflowRunId`.

## Goals / Non-Goals

**Goals:**
- Every event family stamps its full business lineage into envelope extensions at production time, per the matrix in `design/event-protocol.md:59-70`, using only identity already held at the producer.
- Lineage attribute names unify to the protocol: user-visible issue number is `issue` (replacing `issueno`); `epicno` is removed; internal ids keep the `id` suffix.
- `EventCatalog` rises from a flat type list to the protocol registry: each registered type declares the lineage attributes it must carry.
- A conformance spec test drives every event production path and fails when an emitted envelope is missing a required attribute.
- All server producers and read-side consumers (handlers + web envelope reader) are reconciled to the unified names in this change.

**Non-Goals:**
- Expression-based subscription / filtering (separate issue; this change only ensures there is something to match).
- Backfill of historical events — old rows keep whatever they were stamped with.
- Routing on `data` (payload).
- Creating producers for catalog types that currently have none (`runner.disconnected`, `workflow.repair-scheduled`).

## Decisions

### D1 — Stamp lineage in the store layer at append time, as a production-time snapshot
Lineage is stamped where identity stamping already lives: each store's `ToCloudEvent`/envelope-construction path, inside the existing append transaction. Attributes record affiliation at emit time; later relationship changes do not rewrite history. This keeps stamping co-located with the single write that persists the envelope, so the snapshot is consistent with the transaction. **Alternative considered:** a shared post-emit decorator that enriches envelopes centrally — rejected because it would re-introduce exactly the cross-aggregate lookups the protocol forbids and split the write from the stamp.

### D2 — `stage` is stamped by structural inspection of the event payload, not by type-string prefix
Whether an event carries `stage` is decided by whether the domain event record exposes a `Stage` member, not by matching the bus type prefix. This matters because the prefixes are not aligned with stage-carriage: `FeedbackRequested` is typed `com.mohist.workflow.feedback.requested` (not `workflow.stage.*`) yet carries `Stage`; conversely `WorkflowArtifactRecorded` is `com.mohist.workflow.artifact.recorded` and carries no stage. `WorkflowRunStore.ToCloudEvent` will pattern-match the unwrapped `WorkflowEvent` union variants that have `Stage` (`Stage*`, `StageApproval*`, `FeedbackRequested`, `Task*`, `Check*`) and stamp `stage` only for those. **Alternative considered:** prefix rules on the bus type — rejected as fragile against the existing taxonomy drift and silent on `feedback.*`.

### D3 — Name unification (`issueno` → `issue`, drop `epicno`) lands in the same change as stamping
`issue` is the name users will write in subscription expressions; it must be stable before expressions ship. The rename and the read-side reconciliation are scoped here so producers and consumers never disagree on the key. All seven non-test `issueno`/`epicno` touchpoints (producers + `EpicAutoDoneHandler`, `HermesIssueNotificationHandler`, `InboxProjectionHandler`, web reader) move to `issue`. **Alternative considered:** dual-write both keys for a transition window — rejected as needless complexity given there is no external consumer yet and the project is pre-stable-version.

### D4 — `EventCatalog` becomes a registry with per-type required-attribute sets
`EventCatalog` gains a per-type declaration of required lineage attributes (matching the matrix), exposing them for introspection and for the conformance test. The existing `All` name list is preserved as a derived/introspection surface so current callers keep compiling. Declarations are grouped by family so stage/task/check types inherit the `workflow.*` base plus `stage`. **Alternative considered:** `[Attribute]`-decorated enum or a separate `LineageMatrix` class — rejected; keeping the matrix inside the catalog that is already the single source of truth for type names avoids a second registry to keep in sync.

### D5 — `epicid` uses atomic producer-owned lineage snapshots
The protocol forbids cross-aggregate queries for stamping, yet AC2 requires `epicid` on `issue.*` and `workflow.*` events while epic membership is owned by `EpicIssueRow`. Each producer therefore owns a scalar `EpicId` lineage snapshot beside its serialized state: `Issues.EpicId` for Issue and `WorkflowRuns.EpicId` for the issue's current workflow run. The producer stores hydrate their domain state/metadata from that scalar immediately before serialization and envelope construction; they never query membership while stamping. `Issue.EpicId` remains the aggregate-visible projection, while `WorkflowRun.Metadata.Annotations["epicId"]` remains the in-memory source used by `WorkflowRunLineage`.

Write path: the Epic link/unlink transaction updates membership, the Issue snapshot, and the current WorkflowRun snapshot together. The only cross-aggregate write is a parameterized scalar projection update; it never deserializes or replaces another aggregate's `State` document, and it never changes a historical event. After commit, the existing `IIssueGrain.SetEpicAffiliationAsync` push refreshes active Issue state; durable link/unlink handling repeats that refresh after recovery. Workflow persistence independently hydrates from its snapshot before every append, so an active workflow cannot stamp a stale affiliation while a refresh message is in flight.

**Alternatives considered:**
- *Bounded `EpicIssueRow` join-table read inside the append transaction (the original D5).* Rejected — it is a cross-aggregate query at stamp time, directly violating the issue-body and protocol invariant ("不允许生产端为 stamping 发起跨聚合查询"). A spec test asserting "no cross-aggregate query" fails against it, and blessing the exception would require overriding a user-voice invariant. (Earlier rejection of denormalization cited three concerns, all resolved: it does not invert ownership — Epic keeps `EpicIssueRow` as truth and `Issue.EpicId` is a read cache, exactly like `WorkflowRunId`; drift is self-healing via the durable handlers; and it does not conflict with the Non-Goal, which forbids rewriting historical *events*, not current issue *state*.)

### D6 — `AgentSession` lineage projected from `Metadata.Labels`
`AgentSessionStore.ToCloudEvent` projects `Metadata.Labels` onto extensions using the existing label keys: `projectid` (`mohist.io/project-id`), `sessionid` (the session id itself), `agentid` (`mohist.io/agent-id`, when present), and `issue`/`workflowrunid`/`stage` (`mohist.io/issue-number`/`mohist.io/source-id`/`mohist.io/stage`) for workflow/issue-origin sessions. Absent labels are omitted. This satisfies the constraint that stamping uses only identity the aggregate already holds — labels are the session's own metadata.

### D7 — Inbox-synthesized event lifts issue identity from its own hint payload
`InboxProjectionHandler` already holds `IssueId`/`IssueNumber` in the `InboxItemPersistedHint` it constructs; it lifts these onto extensions alongside `projectid`. No additional lookup.

### D8 — Conformance test drives every real production path; catalog-only types are excluded until they have a producer
A spec test (in `tests/Mohist.Server.SpecTests`, alongside the existing `*TransactionalEventAppendSpecs`) drives each producer (`WorkflowRunStore`, `IssueStore`, `EpicGrain`, `AgentSessionStore`, `InboxProjectionHandler`) through a representative event of each family and asserts the emitted envelope's extensions are a superset of the type's catalog-declared required attributes, and that absent affiliations are omitted (no empty values). Catalog types with no producer today (`runner.disconnected`, `workflow.repair-scheduled`) are declared in the registry but not exercised — the test iterates production paths, not catalog entries, so it neither falsely passes nor falsely fails on them. A negative case (a type whose producer drops a required attribute) proves the check actually fails.

## Risks / Trade-offs

- **[Producer snapshots must follow membership atomically]** -> Mitigation: the Epic membership transaction updates only the Issue and current WorkflowRun `EpicId` scalar snapshots. Stores hydrate their own state from those snapshots immediately before appending, so no producer consults membership or overwrites unrelated aggregate state. Focused interleaving specs prove an unrelated Issue update survives both link and unlink while the resulting events use the committed snapshot.
- **[Historical rows retain `issueno`/`epicno`; new readers key on `issue`]** -> Non-Goal forbids backfill, so pre-change events keep the old names. Mitigation: the few handlers that traverse historical rows read `issue` first and fall back to `issueno` (a one-line dual-key read in `EpicAutoDoneHandler`/`InboxProjectionHandler`); newly produced rows are single-key. Net effect: history degrades gracefully rather than silently losing lineage.
- **[`stage` stamping couples to domain event shapes]** -> A producer that adds a new stage-bearing event without exposing `Stage` would silently miss the stamp. Mitigation: the conformance test asserts `stage` on every stage-family type; a new stage event that forgets it fails the check.
- **[Matrix drift between catalog declarations and actual producers]** -> This is exactly what D8's conformance test exists to prevent: declaring a type's required attrs without stamping them turns the check red.
- **[Rename lands in a single coordinated server+web release]** -> If web and server temporarily disagree on the key, issue-number resolution on the web flickers. Mitigation: ship the rename atomically; web only reads `issue` after server stamps it.

## Migration Plan

1. **Schema migration:** add nullable `Issues.EpicId` and `WorkflowRuns.EpicId` lineage snapshot columns. Backfill them from the already-denormalized `State` JSON; no historical event row is changed.
2. **Server first:** add `Issue.EpicId` + the `IssueGrain.SetEpicAffiliationAsync` command + atomic scalar snapshot staging in the `EpicGrain` membership transaction (D5), update the five producers (D1, D2, D5, D6, D7), raise `EventCatalog` to the registry (D4), add the conformance spec (D8), and reconcile the three `issueno`-reading handlers (D3) — all in one server change. The build's `TreatWarningsAsErrors` plus the conformance test gate correctness.
3. **Backfill:** the new migration copies current Issue affiliation and resolves each WorkflowRun from its explicit annotation or its associated Issue snapshot. The earlier membership-state backfill remains the source for Issue JSON on installations upgrading from before this change; no historical event is rewritten.
4. **Web:** update the envelope reader and the fixtures that hardcode `epicno`/`issueno` to `issue`/`epicid` in the same release.
5. **Deploy:** coordinated server + web release (single workspace, no external API consumers).
6. **History:** left as-is; historical events keep their old partial stamping and old key names (Non-Goal). Read-side dual-key fallback (see Risks) covers the transition for handlers that read history.
7. **Rollback:** revert the producers, catalog, handlers, denormalization push, and web reader together. Rows stamped with `issue` during the release window would then be read by rolled-back consumers as missing the issue number — bounded to the release window and non-corrupting (the envelope is still well-formed; only issue-number routing degrades). No data loss.

## Open Questions

- **Read-side fallback scope:** should the dual-key `issue`/`issueno` read shim live permanently in history-traversing handlers, or be removed once pre-change events age out of any active query window? Lean: keep it minimal and permanent in the two handlers that read historical event rows, since backfill is explicitly a Non-Goal.
- **`FeedbackRequested` and `stage`:** RESOLVED — `workflow.feedback.requested` is structurally stage-bearing (`FeedbackRequested` carries a `Stage` member, per D2), so it is added to the stage-required families in the protocol matrix, the `EventCatalog` declarations (T-001), and the spec. Approval-loop consumers will route on stage, so the stamp is required, not optional.
- **Catalog-only types (`runner.*`, `repair-scheduled`):** confirm they are declared in the registry now (required attrs per matrix) but excluded from conformance until a producer exists. Recommend yes — declaration is cheap and the check only exercises real paths.
