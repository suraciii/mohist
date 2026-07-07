# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dispatch / event envelope
  Evidence: The previous review (pre-fix) found that `WorkflowRunStore.ToCloudEvent` produced workflow CloudEvents with no `projectid` extension, causing the dispatch handler to skip every workflow event. The fix commit (`a0daeaeed`) adds `projectid` to the envelope extensions from `run.Metadata.Annotations["projectId"]` in `WorkflowRunStore.ToCloudEvent` (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:64-87`). `AgentSubscriptionDispatchHandlerSpecs.HandleAsync_WorkflowEventWithProductionEnvelope_Dispatches` now asserts a production-shaped workflow event dispatches correctly.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~WorkflowRunStoreSpecs|FullyQualifiedName~AgentSubscriptionDispatchHandlerSpecs" --no-build` → 31 passed, 0 failed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: visibility / session query
  Evidence: The previous review found that `AgentSessionQuery.QueryRowsByLabels` had no cases for `mohist.io/trigger/event-id` or `mohist.io/trigger/subscription-id`. The fix commit adds stored computed columns `LabelTriggerEventId` / `LabelTriggerSubscriptionId` to `AgentSessionRow` and the matching `json_extract` projections in `MohistDbContext`, plus the two switch arms in `AgentSessionQuery.QueryRowsByLabels` (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuery.cs:128-130`). `AgentSessionQuerySpecs.QueryByTriggerLabels_ResolvesSubscriptionTriggeredSessions` asserts both directions.
  Verification: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~AgentSessionQuerySpecs" --no-build` → passed.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: follow-1]
  Severity: follow-up
  Scope: dispatch performance
  Evidence: `AgentSubscriptionDispatchHandler.DispatchAsync` queries `AgentQuerier.GetByIdAsync` once per candidate subscription and then again for the winning Agent after arbitration (`packages/server/src/Mohist.Server/Events/Subscriptions/AgentSubscriptionDispatchHandler.cs:119-145`). This is an N+1 pattern inside the single-threaded InMemoryEventBus dispatch loop. Subscription volume per project is assumed small, but the handler could batch agent lookups (e.g., distinct `AgentId` set) and avoid the redundant second query.
  SuggestedAction: Cache the `AgentInfo` looked up during filtering and reuse it for the launch; consider a single `ListByIdsAsync`-style agent lookup for the distinct agent ids in the candidate set.
  Status: follow-up

- [ID: follow-2]
  Severity: follow-up
  Scope: API input validation
  Evidence: `AgentSubscriptionRoutes` validates required fields and blank strings, but it does not validate the filter `type` pattern syntax. A user can create `filter.type = "com.mohist.*.foo"` or `"prefix*"`; `CloudEventTypeMatcher.Matches` will simply never match. There is also no validation that `source`/`subject` values are non-whitespace when provided (the route normalizes whitespace to `null`, which is correct, but a malformed absolute-vs-relative source will silently fail to match).
  SuggestedAction: Add a lightweight validator on `filter.type` that rejects wildcards not in `{exact, |, *, prefix.*}` form, or document the syntax explicitly in the API error contract.
  Status: follow-up

- [ID: follow-3]
  Severity: follow-up
  Scope: architecture test exception
  Evidence: `ArchitectureRules.DomainInternalLayers_ShouldBeFreeOfCycles` now includes `"Agent"` in `domainsWithKnownCycles` (`packages/server/tests/Mohist.Server.Tests/Architecture/ArchitectureRules.cs:336-345`). The comment justifies the cycle as `AgentGrain → AgentQuerier` (pre-existing) plus `AgentLauncher (Services) → IAgentJobGrain (Grains)` (introduced by T-001). While documented, this broadens the accepted cycle set rather than narrowing it.
  SuggestedAction: When feasible, move `AgentGrain.ToInfo` projection out of the grain (e.g., to a dedicated projection helper) so the `AgentLauncher → Grains` edge remains but the cycle closes; then remove `"Agent"` from the exception list.
  Status: follow-up

- [ID: follow-4]
  Severity: follow-up
  Scope: CLI surface completeness
  Evidence: The CLI exposes `mo agent subscription create|list|delete` but no `archive`/`restore` commands, while the Web UI supports archive/restore and the API exposes `POST .../archive` and `POST .../restore`. Issue AC8 explicitly limits CLI to create/list/delete, so this is not a violation, but a user managing subscriptions purely from the CLI cannot toggle status without deleting and recreating.
  SuggestedAction: Add `mo agent subscription archive <agent> <subscription-id>` and `restore` subcommands when the CLI surface is next extended.
  Status: follow-up

- [ID: follow-5]
  Severity: follow-up
  Scope: Web UI edit capability
  Evidence: The Web Subscriptions section supports create/archive/restore/delete but not in-place editing of an existing subscription (`PATCH` is implemented on the server but not exposed in `SubscriptionsSection`).
  SuggestedAction: Add an edit action to the subscription row that opens the create dialog pre-filled and calls `PATCH .../subscriptions/{id}`.
  Status: follow-up

- [ID: follow-6]
  Severity: follow-up
  Scope: API PATCH type safety
  Evidence: `AgentSubscriptionUpdateRequest.BindAsync` parses `priority` with `value.TryGetInt32`; if the client sends `"priority": "not-a-number"`, `GetInt` returns `null` while `Fields` still contains `"Priority"`, causing the route to clear the priority instead of returning a 400 (`packages/server/src/Mohist.Server/Api/AgentSubscriptionRoutes.cs:184-185,345-350`).
  SuggestedAction: In `GetInt`, distinguish "property absent" from "present but not an integer" and have the route return `400 invalid_field` for the latter.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: pre-1]
  Severity: info
  Scope: Agent domain cycle
  Evidence: The `AgentGrain → AgentQuerier` dependency existed before issue-391 (noted in the architecture test comment). T-001 added the second directional edge that completed the cycle. This item is recorded for traceability only; the current change documents and accepts it.
  SuggestedAction: Address as part of follow-up item follow-3.
  Status: pre-existing

- [ID: pre-2]
  Severity: info
  Scope: per-subscription retry / outbox
  Evidence: The dispatch handler swallows exceptions and relies on event-bus replay for recovery. The issue Non-Goals explicitly exclude per-subscription retry/outbox (`agent-subscription-dispatch` non-goal). No change requested.
  SuggestedAction: None — covered by non-goal; revisit only if operational data shows missed dispatches.
  Status: out-of-scope

## Review Summary

### alignment
- All issue acceptance criteria (AC1–AC10) map to implemented code and passing specs.
- AC3/AC4/AC5 (workflow dispatch, single-Agent response, fallback/takeover) were previously blocked by missing `projectid` on workflow events; that gap is now closed.
- AC6 (event↔session visibility) was previously blocked by missing trigger-label query columns; that gap is now closed.

### completeness
- Subscription CRUD, lifecycle (active/archived), filter matching, priority arbitration, prompt rendering, shared launcher extraction, trigger labels, Web UI section, and CLI commands are all implemented.
- Server: 92 new/related specs pass for the issue-391 surface.
- Web: 4467 tests pass including new subscription component/query tests.
- CLI: 20 new subscription command specs pass.

### consistency
- Naming is consistent across server/CLI/Web: `AgentSubscription`, `mohist.io/trigger/event-id`/`subscription-id`, filter sub-fields, priority default semantics.
- The two surfaces (Web/CLI) both consume the same T-002 API shape.

### feasibility
- Dependencies between tasks were respected (T-001 → T-003, T-002 → T-003/T-004/T-005).
- The two previously unresolved design questions (workflow `projectid`, trigger-label queryability) were answered and fixed in the follow-up commit.

### dependency_completeness
- T-001 → T-002/T-003 chain intact.
- T-004/T-005 depend only on T-002.
- No cycles in the task graph.

<promise>PASS</promise>
