# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260716190000_RemoveLegacyIssueEpicIdentity.cs`
  Evidence: The pending `WorkflowRunCompleted` upgrade matches `/mohist/workflows/{id}` at line 40, while the actual persisted WorkflowRun source is `/mohist/workflow-runs/{id}` (`WorkflowRunEventPersistence.cs:6`). Real pending completion events therefore retain no canonical context. `IssueWorkflowCompletionHandler` logs and returns when context is missing (`IssueWorkflowCompletionHandler.cs:55`), so the dispatcher marks the event delivered without completing its Issue. The migration spec seeds the same obsolete `/mohist/workflows/` source (`RemoveLegacyIssueEpicIdentityMigrationSpecs.cs:40`) and cannot detect the loss.
  SuggestedAction: Upgrade the real `/mohist/workflow-runs/` source format and add a dispatcher-level predecessor-schema regression that proves the owning Issue completes exactly once.
  Verification: Seed an undispatched real-source completion event for an in-progress Issue, migrate through `20260716190000`, dispatch it, and assert canonical extensions, one `CompleteWorkAsync`, and a terminal Issue.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: workflow AgentSession lineage after affiliation changes
  Evidence: The runner posts `epicNumber` only while opening a new workflow AgentSession (`packages/runner/src/actions/acp/session-strategies.ts:85`). When `getWorkflowAgentSession` returns an existing session, its metadata is never refreshed. Although `IssueEpicChangedHandler` updates the active WorkflowRun context, subsequent events from that reused session continue to stamp its old Epic label (`AgentSessionLineage.cs:51`), after the Issue has moved to a different Epic.
  SuggestedAction: Refresh existing workflow AgentSession context when the workflow context changes, or derive session event lineage from an updated local workflow-session context without querying another aggregate.
  Verification: Open a session under Epic 7, move the active Issue to Epic 9, reuse the session, and assert subsequent AgentSession envelopes carry `epic=9`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: Web AgentSession realtime invalidation
  Evidence: `agentSessionHandler` invalidates only runtime-bound, usage-recorded, and model-changed events (`packages/web/src/app/providers/handle-event.ts:256-258`). Subscribed context-compacted, context-exhausted, and context-health-updated events never invalidate generic session summary/transcript data. The handler also leaves the non-polling agent session-list query `['agents', projectId, agentRef, 'sessions']` stale after session status or model changes.
  SuggestedAction: Route all AgentSession lifecycle/context events through the session invalidation handler and invalidate the affected agent's session-list key where agent lineage is available.
  Verification: Deliver each of the six AgentSession event types and assert exact generic detail/transcript invalidation plus the matching agent session-list invalidation, without invalidating another session.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/src/widgets/coder-session/model/useSessionTimeline.ts`
  Evidence: Context-compacted and context-health-updated event callbacks accept every event matching the Project and Issue (`useSessionTimeline.ts:412-421`), but do not require the normalized `sessionId`. Two sessions attached to the same Issue therefore apply each other's context updates, even though server envelopes provide `sessionid` and Web normalization preserves it.
  SuggestedAction: Require the current session identity for reverse-DNS AgentSession context events, as the other session-specific event paths do.
  Verification: Mount two same-Issue session timelines, deliver a context-health or compaction event for one `sessionid`, and assert only that session's reducer changes state.
  Status: open

- [ID: item-5]
  Severity: minor
  Scope: `packages/web/src/app/providers/model/timeline-live-event.ts`
  Evidence: `readEnvelopeIssueNumber` accepts any finite numeric `extensions.issue` value (`timeline-live-event.ts:21-28`), including `-1` and `1.5`. The normal envelope projection correctly requires a positive safe integer (`event-envelope.ts:54-60`), so timeline routing has a weaker and inconsistent canonical-identity validator.
  SuggestedAction: Use the same positive safe-integer rule for timeline extraction.
  Verification: Assert malformed, fractional, zero, and negative issue extensions do not produce a timeline Issue route; assert `"42"` continues to route.
  Status: open

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

(none)

<promise>FAIL</promise>
