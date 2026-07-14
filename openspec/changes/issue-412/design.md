## Context

The event envelope already has two stable axes — `type` (what happened) and `source` (who emitted) — but the third axis, **business lineage** (which issue / epic / workflow run / stage the event belongs to), is only partially stamped and inconsistently named. Today:

- `WorkflowRunStore.ToCloudEvent` stamps only `projectid`/`issueid` from run annotations (`Infrastructure/Data/Workflow/WorkflowRunStore.cs:81-103`); `workflowrunid`, `issue` (number), and `stage` are never lifted to the envelope.
- `IssueStore.BuildIdentityExtensions` stamps `projectid`/`issueid`/`issueno` (`Infrastructure/Data/Issue/IssueStore.cs:130-138`); `epicid` is absent, and the user-visible name is `issueno`, not the protocol name `issue`.
- `EpicGrain` stamps `projectid`/`epicid`/`epicno` (`Epic/Grains/EpicGrain.cs:1098-1103,1148-1153`); `epicno` is not a routing dimension.
- `AgentSessionStore.ToCloudEvent` passes `extensions: null` (`Infrastructure/Data/Sessions/AgentSessionStore.cs:118-131`) despite `Metadata.Labels` already carrying project/agent/workflow/issue/stage identity (`Sessions/Services/AgentSessionQueryMetadataKeys.cs`).
- `InboxProjectionHandler` synthesizes `inbox.item-persisted` with only `projectid` (`Events/Subscriptions/InboxProjectionHandler.cs:148-167`), ignoring the issue identity already in the hint.
- `EventCatalog` is a flat `IReadOnlyList<string>` of type names (`Infrastructure/Events/EventCatalog.cs:17-100`) — no per-type required-attribute declaration, no conformance.
- Read-side handlers key on `issueno` (`EpicAutoDoneHandler.cs:374,388`, `HermesIssueNotificationHandler.cs:163`, `InboxProjectionHandler.cs:213`) and the web envelope reader documents `issueno` (`packages/web/.../event-envelope.ts:28-29`).

The target protocol is `design/event-protocol.md:57-73` (the stamping matrix). Persistence needs no schema change — the `ExtensionsJson` JSON column has existed on all event tables since migration `20260609154024_AddWorkflowRunEvents`.

**Architectural constraint that drives the key decision:** Issue and Epic are separate aggregates (`design/architecture.md`, facts/decisions separation). The Issue aggregate's own state carries no epic reference — epic↔issue membership lives in the `EpicIssueRow` join table owned by the Epic domain. So stamping `epicid` on `issue.*` events cannot be satisfied "from the producing aggregate's own state" the way the other attributes can. This conflict must be resolved explicitly (see Decisions).

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

### D5 — `epicid` on `issue.*` events via a bounded join-table read (the one cross-aggregate exception)
The Issue aggregate does not own epic membership, so `epicid` cannot be stamped from its own state. `IssueStore.SaveAsync` resolves the current epicid with a single indexed lookup against the `EpicIssueRow` join table (`db.EpicIssues`, by `ProjectId`+`IssueId`) **inside its existing append transaction**, and stamps `epicid` only when a link row exists. This is explicitly the single, narrow exception to the "no cross-aggregate queries" rule: it reads one relationship row, not the Epic aggregate's domain logic, and not via a grain call. The transaction-scoped read preserves the snapshot invariant (the epicid reflects membership at the moment of emit). **Alternatives considered:**
- *Denormalize `epicid` onto `Issue` state, written by `EpicGrain` on link/unlink.* Rejected — it inverts ownership (epic owns the link per architecture) and introduces a second copy that can drift; also conflicts with Non-Goal of not rewriting history on relationship change.
- *Omit `epicid` from `issue.*` events.* Rejected — violates the spec/protocol matrix and breaks "subscribe to everything under an epic" before it starts.

### D6 — `AgentSession` lineage projected from `Metadata.Labels`
`AgentSessionStore.ToCloudEvent` projects `Metadata.Labels` onto extensions using the existing label keys: `projectid` (`mohist.io/project-id`), `sessionid` (the session id itself), `agentid` (`mohist.io/agent-id`, when present), and `issue`/`workflowrunid`/`stage` (`mohist.io/issue-number`/`mohist.io/source-id`/`mohist.io/stage`) for workflow/issue-origin sessions. Absent labels are omitted. This satisfies the constraint that stamping uses only identity the aggregate already holds — labels are the session's own metadata.

### D7 — Inbox-synthesized event lifts issue identity from its own hint payload
`InboxProjectionHandler` already holds `IssueId`/`IssueNumber` in the `InboxItemPersistedHint` it constructs; it lifts these onto extensions alongside `projectid`. No additional lookup.

### D8 — Conformance test drives every real production path; catalog-only types are excluded until they have a producer
A spec test (in `tests/Mohist.Server.SpecTests`, alongside the existing `*TransactionalEventAppendSpecs`) drives each producer (`WorkflowRunStore`, `IssueStore`, `EpicGrain`, `AgentSessionStore`, `InboxProjectionHandler`) through a representative event of each family and asserts the emitted envelope's extensions are a superset of the type's catalog-declared required attributes, and that absent affiliations are omitted (no empty values). Catalog types with no producer today (`runner.disconnected`, `workflow.repair-scheduled`) are declared in the registry but not exercised — the test iterates production paths, not catalog entries, so it neither falsely passes nor falsely fails on them. A negative case (a type whose producer drops a required attribute) proves the check actually fails.

## Risks / Trade-offs

- **[epicid bounded read relaxes the "no cross-aggregate query" rule for one case]** -> Mitigation: it is read inside the append transaction (snapshot-consistent), reads only the join row (no Epic domain logic, no grain call), and is documented as the single permitted exception. The conformance test asserts `epicid` presence on affiliated issue events and absence on unaffiliated ones.
- **[Historical rows retain `issueno`/`epicno`; new readers key on `issue`]** -> Non-Goal forbids backfill, so pre-change events keep the old names. Mitigation: the few handlers that traverse historical rows read `issue` first and fall back to `issueno` (a one-line dual-key read in `EpicAutoDoneHandler`/`InboxProjectionHandler`); newly produced rows are single-key. Net effect: history degrades gracefully rather than silently losing lineage.
- **[`stage` stamping couples to domain event shapes]** -> A producer that adds a new stage-bearing event without exposing `Stage` would silently miss the stamp. Mitigation: the conformance test asserts `stage` on every stage-family type; a new stage event that forgets it fails the check.
- **[Matrix drift between catalog declarations and actual producers]** -> This is exactly what D8's conformance test exists to prevent: declaring a type's required attrs without stamping them turns the check red.
- **[Rename lands in a single coordinated server+web release]** -> If web and server temporarily disagree on the key, issue-number resolution on the web flickers. Mitigation: ship the rename atomically; web only reads `issue` after server stamps it.

## Migration Plan

1. **No schema migration.** Lineage rides the existing `ExtensionsJson` column.
2. **Server first:** update the five producers (D1, D2, D5, D6, D7), raise `EventCatalog` to the registry (D4), add the conformance spec (D8), and reconcile the three `issueno`-reading handlers (D3) — all in one server change. The build's `TreatWarningsAsErrors` plus the conformance test gate correctness.
3. **Web:** update the envelope reader and the fixtures that hardcode `epicno`/`issueno` to `issue`/`epicid` in the same release.
4. **Deploy:** coordinated server + web release (single workspace, no external API consumers).
5. **History:** left as-is; historical events keep their old partial stamping and old key names (Non-Goal). Read-side dual-key fallback (see Risks) covers the transition for handlers that read history.
6. **Rollback:** revert the producers, catalog, handlers, and web reader together. Rows stamped with `issue` during the release window would then be read by rolled-back consumers as missing the issue number — bounded to the release window and non-corrupting (the envelope is still well-formed; only issue-number routing degrades). No data loss.

## Open Questions

- **Read-side fallback scope:** should the dual-key `issue`/`issueno` read shim live permanently in history-traversing handlers, or be removed once pre-change events age out of any active query window? Lean: keep it minimal and permanent in the two handlers that read historical event rows, since backfill is explicitly a Non-Goal.
- **`FeedbackRequested` and `stage`:** the spec names `workflow.stage.*`/`task.*`/`check.*` for the `stage` attribute; `feedback.*` is structurally stage-bearing. D2 stamps `stage` on it via structural inspection — confirm this is desired (recommendation: yes, since approval-loop consumers will route on stage).
- **Catalog-only types (`runner.*`, `repair-scheduled`):** confirm they are declared in the registry now (required attrs per matrix) but excluded from conformance until a producer exists. Recommend yes — declaration is cheap and the check only exercises real paths.
