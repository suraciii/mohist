# Review Report

## Result: FAIL

## Scope Reviewed

- Issue context: `mo issue show 361 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Specs and design: `proposal.md`, `design.md`, `tasks.json`, `specs/event-publisher/spec.md`, and `specs/transactional-event-append/spec.md`.
- Product changes across server event storage, event publisher, workflow/issue/session stores, grains, subscription handlers, migrations, and related tests.
- Adjacent retry/recovery/artifact paths for workflow runs, issues, agent sessions, subscriptions, dead letters, and dispatch recovery.

## Repaired During Review

- Updated stale comments that still described synchronous in-memory bus dispatch or no-outbox/no-retry behavior in subscription and publisher-adjacent code.
- Updated the AgentSessionEvents migration spec to assert the final `IX_AgentSessionEvents_Type_Time` index.
- Replaced an obsolete issue creation assertion that still expected duplicate `workflow.run.stopped` rows from an earlier intermediate implementation.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:357`
  Evidence: `SaveIssueAsync` now quarantines the activation on event-aware save failure by setting `_issueReloadRequired` and deactivating at `IssueGrain.cs:647-659`, and most mutation methods pass through `EnsureIssue()`, which rejects the dirty activation at `IssueGrain.cs:664-671`. `CompleteWorkAsync` does not call `EnsureIssue()`; it only checks `_issue is null`, mutates via `_issue.Complete(workflowRunId)`, then saves at `IssueGrain.cs:357-362`. After a rolled-back issue state/event transaction, a workflow-completion delivery can still enter this method on the same dirty activation and attempt to persist mutated in-memory state. The existing quarantine spec covers `ReopenAsync` retry rejection but not this delivery path.
  Impact: The issue producer still has a same-activation recovery gap in a path used by event replay/subscription delivery. That leaves the acceptance criterion for consistent exception handling and crash/retry safety incomplete.
  SuggestedAction: Route `CompleteWorkAsync` through the same reload-required guard before it reads or mutates `_issue`, and add a grain spec that fails an event-aware save, then invokes `CompleteWorkAsync` on the same activation and asserts it rejects before persisting dirty state.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:265`
  Evidence: The normal flush path clears committed domain events after `_stateStore.SaveAsync(SessionId, session, events, ct)` succeeds and retries only transcript persistence if `_transcriptStore.SaveAsync(transcript, ct)` fails (`AgentSessionGrain.cs:624-648`). The recovery path does not use that split. `PersistRecoveryAsync` accepts transcript entries, builds a transcript flush, commits session state plus durable lifecycle events at `AgentSessionGrain.cs:270-277`, then saves transcript rows at `AgentSessionGrain.cs:290-294`. If transcript save fails after the state/event transaction commits, the method throws without committing the transcript flush and without any idempotence boundary around the already-committed recovery events. A retry of `CompactAsync`/`ResetAsync` can append duplicate durable compaction/rebind events or leave committed state/events without matching recovery transcript evidence.
  Impact: Agent-session recovery remains non-atomic across the state/event rows and recovery transcript evidence, and its retry behavior is weaker than the repaired normal flush path. This is inside the issue-361 producer set and directly affects crash-after-commit semantics.
  SuggestedAction: Make recovery persistence follow the same committed-domain-events/transcript-retry split as `CommitAsync`, or otherwise make recovery events and transcript persistence idempotent across a transcript failure. Add a recovery spec that fails transcript save once after state/events commit, retries, and asserts no duplicate `AgentSessionEvents` rows and complete transcript evidence.
  Status: open

## Verification

- Passed: `git diff --check`.
- Passed: focused server spec run covering event store delivery progress, issue creation, epic publish, transactional append, scoped append, workflow state, issue save-failure quarantine, and related specs: 114 passed, 0 failed.
- Failed: `npm test`.
  Details: .NET suites passed (`Mohist.Cli.Tests`: 865 passed; `Mohist.Server.ArchTests`: 24 passed, 3 skipped; `Mohist.Server.SpecTests`: 4159 passed, 9 skipped). `packages/web` failed 1 Vitest assertion in `src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx` looking for `08:00:00.000`; `packages/runner` later passed 1031 tests. The web failure is outside the server files touched for issue 361 but means the full repo verification command is not green.

## Notes

- The core event append shape is otherwise in place: the publisher is write-only, scoped event-store append exists, and the three targeted stores use explicit state/event transactions.
- Workflow-run save-failure quarantine, dead-letter origin parsing for AgentSession, AgentSessionEvents indexing, and the normal AgentSession flush transcript retry behavior have been addressed since the stale previous review.

<promise>FAIL</promise>
