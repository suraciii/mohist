# Self Review Report

## Result: PASS

The plan for issue-286 (project-scoped inbox MVP) was reviewed against the issue
requirements, all four plan artifacts (`proposal.md`, `design.md`, `tasks.json`,
`specs/project-inbox/spec.md`), and the live codebase. No blocking issues were
found and no repairs were required. All claims that the design and tasks lean on
were verified in the source tree.

## Verified Against The Codebase

The following design/task feasibility claims were checked in the actual source
and confirmed present, which is why no repair was needed:

- The four MVP event type strings exist and match the design exactly:
  `com.mohist.workflow.run.failed`, `com.mohist.workflow.stage.approval-requested`
  (`EventCatalog.cs:94,101`), and `com.mohist.issue.work-started`,
  `com.mohist.issue.work-completed` (`IssueEventSerializer.cs:32-33`). They are
  also already in the Web subscription set
  (`shared/lib/canonical-event-types.ts:7,13,20,21`), so D9 invalidation-only
  refresh is feasible without registering new event types.
- `InMemoryEventBus.PublishAsync` swallows handler exceptions per-subscription
  and supports pipe-separated `[Subscription(Type=...)]` patterns
  (`InMemoryEventBus.cs:71-86`), confirming D1/D5 and T-003's inline, non-fatal
  handler design.
- Each published CloudEvent gets `id = Guid.NewGuid()` (`InMemoryEventBus.cs:54`),
  confirming D3's choice of CloudEvent `Id` as the idempotency key.
- Reused seams all exist: `WorkflowStageLockReleaseHandler.ExtractWorkflowRunId`
  and `WorkflowRunStore.LoadAsync` (`WorkflowRunStore.cs:94`); `IssueStore`
  (`Infrastructure/Data/Issue/IssueStore.cs`); the
  `ProjectResolutionEndpointFilter` + `GetResolvedProject()` pattern used across
  `*Routes.cs`; `AddCloudEventHandlersFromAssembly` auto-registration
  (`CloudEventBusServiceCollectionExtensions.cs:17`,
  `MohistServiceRegistration.cs:70`); `MohistApiRegistration.MapMohistApi`
  (`Program.cs:69`); the `EpicAutoDoneHandler` subscription pattern; the Web
  `useProjectPath` helper and `ProjectRouteScope` (`app/App.tsx:56,90`); and the
  `ApiTestClient` test helper.
- All five task `spec` anchors resolve to real `### Requirement:` headings in
  `specs/project-inbox/spec.md`.

## Repaired Items

_None._ No safe, in-scope fix was required.

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: `design.md` references several exact source line numbers (e.g.
  `IssueGrain.cs:598`, `IssueGrain.cs:205`, `WorkflowRunStore.cs:80`). These are
  point-in-time navigational aids; only `WorkflowRunStore.cs:94` (`LoadAsync`)
  was re-confirmed at the cited line. The class/method references behind them are
  correct, so this is cosmetic, but line numbers will drift as the file changes.
  SuggestedAction: During implementation, treat the line numbers as hints and
  navigate by symbol name; optionally refresh them if the design doc is touched.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `design.md` D9 and `tasks.json` T-005 describe live refresh as
  "Subscribe to the existing EventBridge". The server-side EventBridge fans events
  to the SignalR hub, but the Web client consumes them through
  `shared/api/events-hub.ts` (`useEventsConnection`) and `LiveTaskProvider`, not a
  client component literally named EventBridge. The described behavior
  (subscribe to the four `com.mohist.*` types and invalidate the TanStack `inbox`
  query only) is accurate and feasible; only the client-side naming is loose.
  SuggestedAction: When implementing T-005, follow the `LiveTaskProvider`
  invalidation pattern and the `events-hub` subscription API rather than looking
  for a client-side `EventBridge` symbol. No spec/contract change is needed.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-001 (schema + migration) and T-002 (store/querier) together form
  the inbox data layer. They are split rather than collapsed. This is acceptable
  granularity — T-001 enforces the core invariants (UNIQUE `SourceEventId` for
  idempotency, `ProjectId` for isolation) and carries its own build/migration
  verification, and it is not a "create file / register DI / extract class"
  micro-task — so it does not trigger the over-fine rule.
  SuggestedAction: Keep as-is. If an implementer finds the two tasks collide in
  practice, they may be merged without changing the spec or contract.
  Status: follow-up

## Review Dimensions Summary

- **Alignment:** Proposal directly addresses the issue's user voice, product
  shape, domain model, invariants, and acceptance criteria. Every "What Changes"
  entry traces to an issue requirement; no requirement is missing or
  misinterpreted. All issue Non-Goals (no multi-user, no global inbox, no
  preferences, no push/email, no workflow/issue/runner changes) are respected by
  the spec and design.
- **Completeness:** All eight spec requirements map to tasks, and all eleven
  issue acceptance criteria are covered. Edge cases (missing workflow-run
  identity, cross-project mutation → 404, empty state, archived-item exclusion,
  replay idempotency) are addressed in design risks and task acceptance criteria.
- **Consistency:** `proposal.md` Capabilities, the eight spec requirements,
  design decisions D1–D9, and tasks T-001–T-005 use consistent naming
  (`project-inbox`, `InboxItem`, `NotificationKind`, the four kind strings,
  `InboxStore`/`InboxQuerier`/`InboxItemRow`/`InboxProjectionHandler`/
  `InboxRoutes`/`InboxPage`). Spec anchors in tasks are valid.
- **Feasibility:** Every external dependency the tasks rely on exists in the
  codebase (verified above) or is created by an earlier task. No circular
  dependencies. Task granularity is appropriate; tests are embedded in each
  feature-slice task rather than split out.
- **Dependency completeness:** T-001 has no `dependsOn`; T-002→T-001;
  T-003→T-002; T-004→T-002; T-005→T-004. Every `dependsOn` points to an existing
  ID with a strictly lower priority, and the directed graph is acyclic. T-004's
  note correctly documents that it needs only the store/querier contract (T-002),
  not the projection (T-003).

<promise>PASS</promise>
