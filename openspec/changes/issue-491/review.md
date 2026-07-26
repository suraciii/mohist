# Review Findings

## P1: Failure event uses the wrong public CloudEvent type

`packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:166` defines the emitted type as `com.mohist.agent-job.failed`. The approved proposal and `agent-response-failure` spec require exactly `com.mohist.agent.job.failed`. This is a public routing and subscription contract: consumers configured for the specified type will never receive the event. Rename the catalog constant and every producer, subscription, test, and documentation reference to the required dotted type.

## P1: Agent-failure events are never projected to the inbox or delivered to Hermes

T-003 only adds the notification kind and persistence column. `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:60` does not subscribe to the agent failure event, and `TryResolve` at `:193` has no mapping to `NotificationKinds.AgentResponseFailed`; consequently an issue-scoped job failure cannot create an inbox item. Hermes has the same gap: `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs:96` and `:170` do not resolve the event, and `packages/server/src/Mohist.Server/Notifications/HermesNotificationOptions.cs:17` does not default-enable the new type. Wire the required event through both handlers, extract its failure payload for rendering, add the renderer branch, and include the type in the Hermes default enabled set.

## P1: Existing projects receive the new inbox notification disabled

`packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260725155928_AddAgentResponseFailedNotification.cs:17` adds `AgentResponseFailedEnabled` with `defaultValue: false`. Existing `InboxSubscriptions` rows therefore suppress the notification, contrary to the acceptance criterion that this kind is default-on for every project. Migrate existing rows with `true` (and keep the model default true for newly synthesized state).

## P1: The inbox subscription API cannot return or persist the new notification setting

`packages/server/src/Mohist.Server/Api/InboxRoutes.cs:116` omits `AgentResponseFailed` from the complete required key set, and `InboxSubscriptionDto` at `:160` has no `agent_response_failed` property or state mapping. The endpoint accepts the key because `NotificationKinds.IsDefined` recognizes it, but JSON deserialization drops it; a caller cannot observe the default or turn the kind off. Add the DTO field and both mappings, and require it alongside the other subscription keys.

## P1: Agent-job producer conformance permits missing required `agentid`

`packages/server/src/Mohist.Server/Infrastructure/Events/ProducerConformance.cs:98` calls `Optional` for `EventCatalog.Lineage.AgentId`, and `AgentJobLineage.BuildExtensions` at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobLineage.cs:43` may omit the key. The requirement makes `agentid` mandatory on every agent-job failure event, including contextless jobs. Require it in the AgentJob conformance case and ensure all AgentJob launch paths retain or supply the failed agent identity before an event can be emitted.

## P1: Direct workflow-grain calls persist an untrimmed approval operator

`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:255` and `:267` call `ApprovalOperatorValidation.EnsureValid(decidedBy)`, which discards the normalized value, then pass the original string into the domain event and approval state. API and CLI callers happen to normalize first, but a valid Orleans grain caller passing `"  supervisor  "` records the whitespace, violating the required trim contract and producing inconsistent history. Normalize once at the grain boundary and use that result for `Approve`, `RequestChanges`, and logging; add grain-level coverage for the persisted/event value.

<promise>FAIL</promise>
