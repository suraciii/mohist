# Review Report

## Result

FAIL

## Repaired Items

_None. The two findings below are behavior/contract gaps that affect acceptance criteria. They are not small, local, or low-risk repairs under the repair policy, so they are reported rather than fixed._

## Blocking Items

- [ID: block-1]
  Severity: blocking
  Scope: dispatch / event envelope
  Evidence: `AgentSubscriptionDispatchHandler.TryResolveProjectId` only accepts a `projectid` extension on the CloudEvent envelope (`packages/server/src/Mohist.Server/Events/Subscriptions/AgentSubscriptionDispatchHandler.cs:188-199`). `WorkflowRunStore.ToCloudEvent` creates workflow CloudEvents with no extensions at all (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:80-92`). Consequently, every production workflow event fails project-id resolution and is silently skipped (`AgentSubscriptionDispatchHandler.cs:105-110`). Issue 391 explicitly lists "workflow 事件 + issue 事件" as event sources, and AC3/AC4/AC5 all require workflow events to trigger Agents. The existing dispatch specs acknowledge the gap as "workflow events currently do NOT stamp projectid" and test it by manually stamping `projectid` in test events (`AgentSubscriptionDispatchHandlerSpecs.cs:409-413`), which masks the production failure.
  SuggestedAction: Stamp `projectid` on workflow CloudEvent extensions at production time (e.g., in `WorkflowRunStore.ToCloudEvent`), or provide another envelope-only project-id source that the handler can resolve without business-domain queries. Update the dispatch specs and tests so workflow-event dispatch is asserted against the real production envelope.
  Status: blocking

- [ID: block-2]
  Severity: blocking
  Scope: visibility / session query
  Evidence: The visibility spec requires "from an event id find the responding Agent and subscription" by querying sessions whose `mohist.io/trigger/event-id` label matches the event id. The dispatch handler correctly passes these labels to `IAgentLauncher.LaunchAsync` (`AgentSubscriptionDispatchHandler.cs:159-170`), and `AgentLauncher` merges them into session metadata labels (`packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:71-75`). However, `AgentSessionQuery.QueryRowsByLabels` has no switch case for `GenericAgentSessionMetadata.TriggerEventId` or `TriggerSubscriptionId` (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuery.cs:94-133`), and `AgentSessionRow` has no stored computed columns for those keys (`packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionRow.cs:13-34`). The default switch arm returns `query.Where(_ => false)`, so any query filtering by trigger labels returns zero rows. This breaks AC6 ("用户能查到「某次事件是被哪个 Agent 响应的」") and the visibility spec's event-to-session query direction.
  SuggestedAction: Add stored computed columns `LabelTriggerEventId` and `LabelTriggerSubscriptionId` to `AgentSessionRow` (with a corresponding EF migration), and add their keys to `AgentSessionQuery.QueryRowsByLabels`. Expose the query capability through an API/CLI/Web surface so users can actually resolve sessions by event id.
  Status: blocking

## Follow-up Items

- [ID: follow-1]
  Severity: follow-up
  Scope: test coverage
  Evidence: The dispatch handler specs assert that workflow events without `projectid` are skipped (`AgentSubscriptionDispatchHandlerSpecs.cs:338-360`), encoding the current gap as intended behavior. Once block-1 is fixed, that test should be replaced by a spec that asserts workflow events dispatch correctly using the production envelope. Similarly, no spec currently asserts `AgentSessionQuery.ListByLabelsAsync` can find sessions by `mohist.io/trigger/event-id` / `mohist.io/trigger/subscription-id`; such a spec should be added when block-2 is fixed.
  SuggestedAction: After resolving the blocking items, update the test suite to assert the corrected production behavior instead of the workaround.
  Status: follow-up

- [ID: follow-2]
  Severity: follow-up
  Scope: documentation consistency
  Evidence: The design doc flagged the workflow `projectid` question as an open question and recommended stamping it on the envelope (`design.md:258-260`). The implementation chose the "skip if missing" degradation instead. If the project decides to keep the skip behavior, the issue acceptance criteria and proposal should be updated to remove workflow events as a source; otherwise the implementation must be fixed.
  SuggestedAction: Reconcile issue ACs, proposal, and implementation: either fix the envelope or update the acceptance criteria.
  Status: follow-up

## Review Summary

### alignment
- The code structure maps cleanly to the four capabilities (management/dispatch/visibility/config-surface).
- All issue ACs trace to a spec requirement and an implementation area, except AC3/AC4/AC5 (workflow dispatch) and AC6 (event-to-session query), which are blocked by the two gaps above.

### completeness
- Subscription CRUD, arbitration, filter matching, prompt rendering, shared launcher extraction, and config surfaces (Web + CLI) are implemented.
- The two missing pieces are workflow-event project-id resolution and trigger-label queryability, both required by acceptance criteria.

### consistency
- Naming is consistent across server/CLI/Web: `AgentSubscription`, trigger label keys, filter semantics, and arbitration rules.
- The only inconsistency is between the documented boundary ("workflow events are a source") and the runtime behavior (workflow events are skipped).

### feasibility
- Dependencies between tasks were respected; no task consumes output of an unbuilt task.
- The blocking gaps were flagged in the design doc but not mitigated, so feasibility of the original plan is undermined only by those two unresolved decisions.

### dependency_completeness
- T-001 → T-002/T-003 dependency chain is intact.
- T-004/T-005 correctly depend only on T-002.
- No cycles.

<promise>FAIL</promise>
