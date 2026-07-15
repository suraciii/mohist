# Review Report

## Result: FAIL

Reviewed the live issue, proposal, design, tasks, all candidate changes through `fe33e8de6`, the new aggregate-coordination design, durable event handlers, and the affected server, runner, and web paths. The candidate correctly removes Epic transactions that write Issue/WorkflowRun state and makes the binding process recoverable. It nevertheless changes the issue's event-time affiliation contract to eventual propagation, and cancellation can strand an `AwaitingBinding` workflow permanently.

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: acceptance criterion for affiliation at event time
  Evidence: The issue requires lineage to record the affiliation when an event occurs. The candidate explicitly replaces that with causal, eventually consistent producer snapshots (`design.md:46-55`; `specs/event-lineage-stamping/spec.md:68-80`). Epic membership commits first, then only best-effort/direct and durable handler commands update Issue and WorkflowRun (`EpicGrain.cs:152-168`; `EpicAutoDoneHandler.cs:517-583`). In the interval, `IssueStore` and `WorkflowRunStore` stamp their still-local prior `EpicId` (`IssueStore.cs:58-75`; `WorkflowRunStore.cs:52-71`). Thus an Issue/workflow event produced after a committed link, unlink, terminalization, or reopen can permanently carry the old or absent `epicid`, contrary to the current issue's stated event-time affiliation rule. [disallowed:product behavior and consistency contract]
  SuggestedAction: Obtain explicit product acceptance for causal lineage semantics and update the live issue acceptance criteria, or provide a design that meets the current event-time criterion without violating aggregate boundaries.
  Verification: Commit an Epic affiliation change, deliberately hold its Issue propagation, emit an Issue/workflow event, and assert the expected `epicid` under the accepted contract.
  Status: unresolved

- [ID: item-2]
  Severity: blocking
  Scope: cancellation during durable workflow binding
  Evidence: `CancelAsync` rejects created/pending/ready/running/paused/approval workflows but omits `awaiting-binding` (`IssueGrain.cs:430-451`). `Close` then clears `WorkflowBindingPending` (`Issue.Transitions.cs:233-243`). The durable `IssueWorkStarted` reaction subsequently exits without creating or confirming the pending binding (`IssueGrain.cs:279-288`), leaving the prepared run non-terminal. After reopening, `StartWorkAsync` treats that run as reusable because it is not terminal and the pending marker is false (`IssueGrain.cs:320-359`), so the issue remains backlog while work never starts.
  SuggestedAction: Reject cancellation while binding is pending, or stop/resolve the prepared run before clearing the marker and make the subsequent reopen/start path create a fresh binding.
  Verification: Interrupt binding after Issue commit, cancel, redeliver `IssueWorkStarted`, reopen, and start again. Assert no awaiting-binding orphan remains and the resulting Issue/workflow pair reaches `InProgress`/`Pending` exactly once.
  Status: unresolved

- [ID: item-3]
  Severity: warning
  Scope: user-visible awaiting-binding status
  Evidence: The new workflow state maps to `awaiting-binding` (`WorkflowStatusMapper.cs:10-16`), but the Issue projection falls through to `active` (`MohistDefaultWorkflowProjection.cs:90-102`) and the runtime decision surface renders that as `Workflow running` (`derive-runtime-decision.ts:124-136`; `runtime-presentations.ts:200-212`). `WorkflowRunStatusPill` renders `Starting`, but it is not used by the production issue workflow surface. Users are told work is executing while the run is deliberately non-assignable.
  SuggestedAction: Add an explicit starting/binding runtime projection and render it in the Issue decision surface, with no execution-only actions.
  Verification: Load an Issue whose workflow is `awaiting-binding` in the full detail view and assert a starting/waiting state rather than running/executing text.
  Status: unresolved

- [ID: item-4]
  Severity: warning
  Scope: specification and producer validation consistency
  Evidence: The proposal and D6 say lineage is printed when present and absent labels are omitted (`proposal.md:7-13`; `design.md:60-61`), while workflow and session producers throw when `projectId` is absent (`WorkflowRunLineage.cs:55-88`; `AgentSessionLineage.cs:98-108`). The event-lineage spec treats conditional affiliations as omittable but does not state this mandatory-project rejection behavior. The documented contract and runtime behavior disagree.
  SuggestedAction: State that `projectid` is mandatory and producer events without it are rejected, or relax the producers and catalog consistently.
  Verification: Add producer specs for missing project annotations/labels that assert the selected contract.
  Status: unresolved

- [ID: item-5]
  Severity: test-gap
  Scope: durable Issue-to-Workflow binding and lineage propagation
  Evidence: Binding integration specs directly invoke `EnsureWorkflowBindingAsync` (`IssueWorkflowLifecycleSpecs.cs:238-290`), while the subscription test only records a command invocation (`IssueWorkflowCompletionHandlerSpecs.cs:403-439`). There is no dispatcher-driven test for crashes between Issue prepare/confirm/marker-clear, cancellation while binding, or reopen after a failed binding. `ApplyIssueLineageAsync` has no duplicate, stale, or equal-revision conflict coverage despite implementing that protocol (`WorkflowGrain.cs:154-170`).
  SuggestedAction: Add end-to-end dispatcher/redelivery specs for each binding boundary and revision ordering case.
  Verification: Exercise duplicate, delayed, stale, and conflicting affiliation deliveries, asserting the workflow snapshot never regresses and the binding marker eventually converges.
  Status: open

- [ID: item-6]
  Severity: test-gap
  Scope: runner handling of `AwaitingBinding`
  Evidence: The runner wire union includes `AwaitingBinding` (`workflow-terminal-status.ts:36-46`), but its terminal-status tests omit it (`workflow-terminal-status.spec.ts:14-89`), as do SignalR push and cleanup-convergence scenarios. The default currently treats unknown/nonterminal states conservatively, but the new server status has no explicit cross-plane regression coverage.
  SuggestedAction: Add terminal-predicate, server-push, and cleanup convergence cases for `AwaitingBinding`.
  Verification: Assert it preserves workspace ownership, never marks cleanup eligible, and transitions correctly after later `Pending`, `Stopped`, and `Completed` statuses.
  Status: open

- [ID: item-7]
  Severity: test-gap
  Scope: bounded batch affiliation persistence retries
  Evidence: The candidate still lacks a spec that injects the changed `DbUpdateConcurrencyException` path for batch membership persistence. Existing coverage uses generic active-membership failures, so it does not prove the three-total-attempt contract.
  SuggestedAction: Add link and unlink specs for one through four concurrency failures while preserving committed membership outcomes.
  Verification: Assert success through attempt three and deterministic failure on attempt four.
  Status: open

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: Issue lineage revision scope
  Evidence: `IssueStore` increments `LineageVersion` for every Issue save (`IssueStore.cs:92-114`), but WorkflowRun receives it only on binding and affiliation propagation. The revision name suggests a lineage-only ordering contract while the implementation uses a broader aggregate revision.
  SuggestedAction: Either rename/document it as the Issue state revision or isolate a lineage-specific revision to reduce protocol ambiguity.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: warning
  Scope: rehomed retained links in epic progression
  Evidence: Reopen retains a link that another active epic owns, while later progress selection evaluates retained membership without filtering active ownership. A restarted epic can attempt to start an Issue owned by another epic. This predates the lineage propagation redesign.
  SuggestedAction: Track a separate epic-progression change to restrict execution candidates to active ownership or reclassify retained historical links.
  Status: pre-existing

## Verification

- `npm test` passed: 865 CLI, 1,411 server unit, 2,802 server spec, 22 architecture, 4,654 web, and 1,014 runner tests.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 333 files and 4,654 tests.
- `git diff --check master...HEAD` passed.

<promise>FAIL</promise>
