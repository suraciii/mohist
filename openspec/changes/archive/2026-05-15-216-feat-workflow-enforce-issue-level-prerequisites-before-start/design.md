## Context

### User Story

A Mohist user can create Issue #201 before Issue #200 is delivered, keep #201 visible in backlog, and declare that #201 has a start prerequisite: prerequisite issue #200 must be delivered first. Until #200 is delivered, #201 is waiting for delivery and must not enter the pipeline.

Today this intent can only appear as natural language in the Issue body. The start path checks lifecycle status and stage before queueing `start-pipeline`, and `AgentRunnerService.executeStartPipelineTask` has a backstop before worktree and WorkflowRun creation, but neither place evaluates issue-level start prerequisites.

### Domain Events

The implementation should make these domain events explicit in service behavior and test names, even if they are not persisted as an event stream in this change:

1. `IssuePrerequisiteDeclared(#201, prerequisite: #200)`
2. `IssueStartRequested(#201)`
3. `IssueStartRejected(#201, waitingFor: #200)`
4. `IssueDelivered(#200)`
5. `IssueStartPrerequisitesSatisfied(#201)`
6. `IssueStartRequested(#201)`
7. `IssueStartAllowed(#201)`

Invalid declaration flow:

1. `IssuePrerequisiteDeclarationRequested(#200, prerequisite: #201)`
2. `IssuePrerequisiteDeclarationRejected(#200, prerequisite: #201, reason: circular-prerequisite)`

### Domain Model

The domain model is intentionally small:

- **Issue**: the Mohist work item with existing lifecycle fields such as `stage`, `status`, and `mergeState`.
- **Start prerequisite / prerequisite issue**: a role one Issue can play for another Issue. For Issue #201, prerequisite issue #200 must be delivered before #201 may start.
- **Start eligibility**: a computed value answering whether the Issue may enter the pipeline now, including `waitingForDelivery` when prerequisite issues are not delivered.

This feature must not promote later Issues or queue ordering into the core model. It also must not reuse task-level `tasks.json` `dependsOn`; that remains only Build task execution ordering inside one Issue.

## Goals / Non-Goals

**Goals:**

- Persist explicit issue-level start prerequisites within one project.
- Compute prerequisite issue delivery from current Issue lifecycle state: `stage=done`, `status=completed`, and `mergeState=merged`.
- Expose structured `prerequisites`, `startEligibility`, and `waitingForDelivery` data in issue list/detail APIs.
- Reject starts through the same backend start eligibility guard before queueing `start-pipeline`.
- Recheck start eligibility in queued `start-pipeline` execution before worktree, WorkflowRun, or agent session creation.
- Show start prerequisites and concise waiting-for-delivery reasons in CLI and Web UI surfaces.
- Reject circular start prerequisite declarations.

**Non-Goals:**

- Do not auto-start Issues when prerequisite issues are delivered.
- Do not change workflow stage order.
- Do not model waiting for prerequisite delivery as `blocked` status, agent failure, session failure, or workflow stage failure.
- Do not parse historical Issue body text for start prerequisites.
- Do not migrate or redefine task-level `tasks.json` `dependsOn`.
- Do not build a broad prerequisite management UI beyond declaring and displaying start prerequisites.

## Decisions

### D1: Centralize start prerequisite behavior in an IssuePrerequisiteService

Add an `IssuePrerequisiteService` that owns declaration validation, delivery evaluation, start eligibility projection, and start rejection messages. API routes, CLI-visible responses, Web UI data, and `AgentRunnerService` should consume the same service result shape instead of duplicating rules.

Core methods:

```ts
type IssueStartEligibility = {
  startable: boolean;
  reason: 'ready' | 'not-startable-lifecycle' | 'waiting-for-delivery';
  message?: string;
  waitingForDelivery: IssuePrerequisiteSummary[];
};

type IssuePrerequisiteSummary = {
  issueId: string;
  number: number;
  title: string;
  delivered: boolean;
  stage: Stage;
  status: IssueStatus;
  mergeState?: MergeState | null;
};
```

The service should expose operations such as:

- `declarePrerequisite(projectId, issueNumber, prerequisiteNumber)`
- `removePrerequisite(projectId, issueNumber, prerequisiteNumber)`
- `getIssuePrerequisiteView(projectId, issue)`
- `getIssuePrerequisiteViews(projectId, issues)` for batched list projection
- `evaluateStartEligibility(projectId, issue)`
- `assertStartEligible(projectId, issue)` returning or throwing a typed rejection

**Alternatives considered:** Put the checks in API handlers and the queue worker. That is simpler initially but creates two sources of truth for start eligibility and makes drift likely. Put the behavior in `IssueService`. That keeps fewer classes but makes IssueService absorb validation, traversal, projection, and start-message concerns that are cohesive on their own.

### D2: Derive storage from the domain model with an issue_start_prerequisites table

Persist start prerequisites in a dedicated SQLite table after the domain model is established:

```sql
CREATE TABLE issue_start_prerequisites (
  issue_id TEXT NOT NULL,
  prerequisite_issue_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  PRIMARY KEY (issue_id, prerequisite_issue_id),
  FOREIGN KEY (issue_id) REFERENCES issues(id) ON DELETE CASCADE,
  FOREIGN KEY (prerequisite_issue_id) REFERENCES issues(id) ON DELETE CASCADE
);
CREATE INDEX idx_issue_start_prerequisites_issue ON issue_start_prerequisites(issue_id);
CREATE INDEX idx_issue_start_prerequisites_prerequisite ON issue_start_prerequisites(prerequisite_issue_id);
```

Add `IssueStartPrerequisiteRepo` near `IssueRepo`, expose it through `StateManager`, and keep the table scoped to Issue ids. The service should validate that both Issues belong to the same project before storing a row.

**Alternatives considered:** Store prerequisite numbers as JSON on the Issue row. That makes validation, deletion, and batched projection harder and stores mutable display identifiers instead of Issue ids. Parse Issue body text. That is explicitly excluded and cannot produce reliable structured start eligibility.

### D3: Use delivery semantics that match later work availability

Treat a prerequisite issue as delivered only when all three facts are true: `stage === Stage.Done`, `status === IssueStatus.Completed`, and `mergeState === MergeState.Merged`. This rule belongs in the start prerequisite service, preferably by reusing or extending existing lifecycle helper code so the definition is named once.

Start eligibility should recompute from current Issue rows. When #200 transitions to delivered, #201 becomes startable on the next read or start attempt without mutating #201 and without deleting the start prerequisite.

**Alternatives considered:** Use `stage=done` alone. That lets later work start before prerequisite code is actually available. Use `status=completed` alone. That does not distinguish completed-but-not-merged states. Store a derived waiting status on #201. That introduces stale state and another lifecycle to maintain.

### D4: Reject circular prerequisite declarations before persistence

On declaration, load the current prerequisite relationships for the project and test whether adding `issue -> prerequisite` would make the Issue require itself before start. Reject direct self-prerequisites and indirect cycles with `reason: 'circular-prerequisite'` before writing the row.

This check is a validation rule for declaring start prerequisites. It is not a general planning graph feature and should not expose traversal details to clients.

**Alternatives considered:** Allow the row and rely on start eligibility to never become startable. That creates confusing permanent waiting states. Check only direct self-reference. That misses common two-Issue and longer cycles.

### D5: Extend Issue response shape, not Issue body conventions

Add structured fields to shared Issue API types:

```ts
type IssuePrerequisiteView = {
  prerequisites: IssuePrerequisiteSummary[];
  startEligibility: IssueStartEligibility;
};
```

`GET /api/issues` and `GET /api/issues/:number` should include these fields for each Issue. List projection should batch-load prerequisites for the returned Issues to avoid per-Issue queries. Start rejection responses should include the same `startEligibility` data so clients can display the exact waiting reason.

**Alternatives considered:** Add only a string such as `waitingReason`. That is easy to render but forces clients to parse text for details. Add a separate endpoint only. That would make common list/card/detail surfaces perform extra requests for data needed on every render.

### D6: Provide minimal structured declaration APIs

Add minimal endpoints for issue-level start prerequisites, for example:

- `POST /api/issues/:number/prerequisites` with `{ prerequisiteNumber }`
- `DELETE /api/issues/:number/prerequisites/:prerequisiteNumber`

Responses return the updated Issue or prerequisite view with `prerequisites` and `startEligibility`. These endpoints keep declaration separate from body text and avoid overloading task-level Build artifacts.

**Alternatives considered:** Only allow declaration through `PATCH /api/issues/:number`. That fits metadata updates but makes add/remove intent less explicit and complicates concurrent edits. Build a larger management surface. That exceeds the issue scope.

### D7: Enforce start eligibility at both start request and queued execution

Replace direct start-route prerequisite decisions with the shared start eligibility guard before calling `agentRunner.enqueue(issue.id, 'start-pipeline')`. Existing status/stage checks can either be folded into the guard or kept adjacent, but the final start route result must come from one composed eligibility result.

In `AgentRunnerService.executeStartPipelineTask`, call the same guard before worktree creation, `workflowRunService.startRun`, or agent session creation. If the Issue is waiting for delivery, complete the queue item as skipped with the service message and do not mark the Issue or a session as failed.

**Alternatives considered:** Guard only in the API. Stale or manually queued `start-pipeline` work could still start. Guard only in the queue worker. Users would get accepted start requests followed by silent skips.

### D8: Keep CLI and Web UI thin

CLI commands should call the API for declaration and start. `mo issue list`, `mo issue show`, and `mo issue start` should render server-provided `startEligibility` and `waitingForDelivery` data. The CLI should not infer prerequisites from Issue body text.

Web UI should add a small Issue Detail prerequisite section, card waiting reason, and Start control explanation using the API response. The declaration interaction can be a minimal issue-number input on Issue Detail. It should not introduce a broad prerequisite management interface.

**Alternatives considered:** Compute waiting state in clients from raw Issue rows. That duplicates delivery rules and increases the chance that CLI, API, and Web UI disagree.

## Risks / Trade-offs

- [Risk] Issue list responses can become slow if prerequisite projection performs one query per Issue. → Mitigation: batch-load all relevant prerequisite rows and prerequisite Issue rows for the list result in `getIssuePrerequisiteViews`.
- [Risk] Delivery semantics may be too strict for future non-code Issue types. → Mitigation: keep delivery evaluation centralized behind a named service method so future Issue categories can extend the rule without changing clients.
- [Risk] Queue items accepted before a prerequisite declaration might later become ineligible. → Mitigation: the queue execution guard rechecks start eligibility immediately before any worktree, WorkflowRun, or agent session creation.
- [Risk] Circular validation can miss an indirect cycle if it only checks the new row. → Mitigation: validate against the full project start-prerequisite set before writing and cover multi-hop cycles in tests.
- [Risk] Users may confuse waiting for delivery with failure. → Mitigation: use waiting language in API messages, CLI output, and Web UI cards; do not set `status=blocked` or create agent/session failure evidence for this condition.

## Migration Plan

1. Add the SQLite migration for `issue_start_prerequisites`, indexes, and foreign keys.
2. Add `IssueStartPrerequisiteRepo` and expose it through server initialization alongside existing repositories.
3. Add shared types for `prerequisites`, `startEligibility`, and `waitingForDelivery`.
4. Implement `IssuePrerequisiteService` with declaration validation, delivery evaluation, batched projection, circular rejection, and start eligibility guard.
5. Wire issue list/detail APIs to include prerequisite and start eligibility data.
6. Add minimal prerequisite declaration/removal API endpoints.
7. Update `POST /api/issues/:number/start` to use the shared guard before enqueueing `start-pipeline`.
8. Update `AgentRunnerService.executeStartPipelineTask` to use the same guard before worktree, WorkflowRun, or agent session creation.
9. Update CLI issue list/show/start and prerequisite declaration commands to use the structured API data.
10. Update Web Issue Card, Issue Detail, Start controls, frontend API types, and the minimal declaration interaction.
11. Add tests for declaration, circular rejection, delivery evaluation, list/detail response shape, API start rejection without queueing, queue backstop behavior, CLI rendering, Web UI rendering, and separation from task-level `tasks.json` `dependsOn`.

Rollback is safe after migration because the new table is additive. Older code will ignore `issue_start_prerequisites`; newer code should tolerate an empty table and compute all Issues as having no start prerequisites.

## Open Questions

None.
