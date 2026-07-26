## Context

The Server already forwards workspace reads and cleanup requests through `IRunnerWorkspaceClient`, but it also registers an unconsumed `IGitService` that can run git and access runner workspaces locally. Its test fixtures replace that dead registration with `FakeGitService`, preserving an obsolete execution surface.

`SystemInfo` and system update components legitimately execute local commands to inspect and manage the Mohist daemon. The architecture table does not currently distinguish this from user-project execution, which belongs to Runner. The architecture test also excludes `Epic` from its cross-domain dependency check despite direct dependencies on Issue types. Durable CloudEvent handlers are discovered by scanning the Server assembly but are currently concentrated in `Events/Subscriptions`, even when they coordinate one domain's state. The dispatcher persists a handler's runtime full type name in each dead-letter row and uses that exact name to select a handler for manual redelivery, so a namespace move also changes a persisted operational identity.

This design implements the `server-architecture-alignment` capability defined by the proposal and specification. The change is internal: API, CLI, persisted state, workflow definitions, and Web behavior remain unchanged.

## Goals / Non-Goals

**Goals:**

- Remove the unused Server-local git and workspace execution surface and its test support.
- State the daemon self-management exception and runner fact/report boundary unambiguously in `design/architecture.md`.
- Enforce Epic dependency direction in the existing architecture test while preserving legitimate Issue/Epic collaboration.
- Place durable state-coordinating handlers with their owner domain without changing the assembly-discovery, subscription, dispatch, or dead-letter redelivery contract.
- Preserve workspace API forwarding, daemon self-management, event delivery, and retry behavior.

**Non-Goals:**

- Change SystemInfo or system update execution, move it to Runner, or alter its API.
- Rename failure classifications or change the workflow recovery DSL.
- Change handler business logic, event schemas, persistence, public routes, CLI commands, or Web components.
- Refactor unrelated `Events`, `Infrastructure`, AgentOps, or notification code.

## Decisions

### Delete the unused local Git service

Remove `Infrastructure/Workspace/GitService.cs`, `IGitService` registration, its runner-root configuration helper if it becomes unused, `FakeGitService`, and every fixture override that registers the fake. Keep `IRunnerWorkspaceClient`, `RunnerWorkspaceClient`, and `WorkspaceRoutes` unchanged.

The Server has no production consumer of `IGitService`; retaining it contradicts the runner execution boundary and forces test composition roots to model a dependency that no product path uses. Retaining the interface for a possible future Server operation was rejected because it would preserve an invalid authority and duplicate Runner behavior.

### Make daemon self-management an explicit Server exception

Add a `daemon self-management` ownership row to `design/architecture.md`: Server owns process execution needed to inspect, update, install, restart, and determine the status of Mohist and its managed services. Clarify that this exception excludes user-project workspaces, project git operations, shell commands, and agent execution.

No SystemInfo or update code moves. Moving the work to Runner was rejected because Runner availability is itself part of the daemon state that the Server must inspect and recover; it would also give project execution infrastructure responsibility for control-plane lifecycle management.

### Extend the dependency guard to Epic with explicit same-context directions

Add `Epic` to `DomainNamespaces` in `ArchitectureRules`. Record the existing `Epic` to `Issue` and `Issue` to `Epic` dependencies as explicit allowed directions, because the two aggregates collaborate within the Issue bounded context. All other Epic-to-domain directions remain subject to the existing deny-by-default matrix.

Leaving Epic excluded was rejected because it permits unrelated dependencies without review. Treating Epic and Issue as one untested namespace was rejected because it would retain the blind spot and make future dependency expansion invisible.

### Relocate domain reactions without changing the event bus contract

Extend `SubscriptionAttribute` and `Subscription` with an optional explicit handler identity. Registration uses the explicit identity when present and otherwise retains the current full-type-name default. The dispatcher writes and matches this subscription identity rather than resolving the runtime type name at dispatch time. Every moved handler declares its pre-relocation `Mohist.Server.Events.Subscriptions.*` full name as its explicit identity, so both historical dead-letter rows and rows written after relocation resolve to the same handler without a schema/data migration.

Move the subscription handlers and adjacent domain-specific helpers from `Events/Subscriptions` according to this inventory:

| Owner | Handlers and helpers |
|---|---|
| Issue | `IssueWorkflowCompletionHandler`, `IssueWorkflowStartHandler`, `IssueEpicChangedHandler`, all `IssueComposite*Handler` types, and `ParentCompositeRecomputeDispatcher` |
| Epic | all `Epic*Handler` types plus `EpicProgressRecomputeDispatcher` and `EpicEventRecomputeDispatcher` |
| Workflow | `WorkflowStageLockReleaseHandler` |
| Runner | `RunnerWorkflowTerminalStatusHandler` |
| Agent | `RoutingDispatchHandler`, `MentionDispatchHandler`, `RoutedAgentLaunchContextResolver`, `ResponsePromptRenderer`, and `MentionTokenParser` |
| Inbox | `InboxProjectionHandler` |
| Notifications | `HermesIssueNotificationHandler` |

Keep shared event contracts, persistence, matching, and dispatch infrastructure under `Infrastructure/Events`. `AddCloudEventHandlersFromAssembly` continues scanning the Server assembly, so no per-module registration mechanism is introduced. Preserve each handler's `[Subscription]` pattern, filter, constructor dependencies, and invocation behavior. Inbox projection failures continue to propagate to the durable dispatcher; Hermes remains asynchronous best-effort delivery, where post-enqueue failures are logged rather than retried or dead-lettered by the CloudEvent dispatcher. A central subscriptions folder was rejected because it separates a domain reaction from the state transition it coordinates and makes ownership harder to audit.

### Clarify classifications as facts, not commands

Update the architecture facts-and-decisions section to state that a Runner may report a failure classification, including `retry-safe`; it does not authorize or cause a retry. Keep `WorkflowReportService` forwarding the result to `WorkflowGrain`, and keep WorkflowRun recovery and retry logic as the single decision authority.

Renaming `retry-safe` or removing it from runner and Web payloads was rejected because it changes a stable failure vocabulary without improving ownership. Having Runner auto-retry work based on the classification was rejected because it bypasses workflow definition, recovery budget, ownership, and approval state.

## Risks / Trade-offs

- [Handler relocation invalidates historical dead-letter redelivery] -> Preserve each moved handler's pre-relocation full name as its explicit subscription identity and test redelivery of a legacy row after relocation.
- [Handler relocation misses a registration or changes a subscription pattern] -> Preserve subscription attributes, rely on unchanged assembly discovery, add a source-level ownership inventory assertion, and retain focused handler and service-graph tests.
- [Handler namespace moves break test composition roots] -> Update only imports and explicit handler registrations; run affected handler specs and architecture tests.
- [Epic dependency guard reports existing legitimate Issue collaboration] -> Add only the observed Issue/Epic directions to the allowlist and let all other directions fail.
- [Git-service deletion leaves a hidden consumer] -> Search production and test projects for `IGitService`, `GitService`, and `FakeGitService`; build the Server after removal.
- [Architecture wording is read as permission for project execution] -> Scope the daemon exception to Mohist-managed processes and state the excluded project operations in the same table row.

## Migration Plan

1. Delete the Git service, DI registration, obsolete runner-root helper, fake, and fixture registrations; confirm workspace routes still use `IRunnerWorkspaceClient`.
2. Add explicit subscription handler identity support and regression tests for historical dead-letter redelivery before relocating any handler.
3. Move every handler and helper in the ownership inventory with namespace/import updates; retain the listed legacy identity and update explicit test registrations.
4. Add Epic to the dependency matrix with the two observed Issue/Epic allowlist directions.
5. Update `design/architecture.md` with the daemon ownership and retry-classification wording.
6. Run Server build, all affected handler and architecture tests, the full Server test suite, and Web typecheck/tests because Web renders runner failure classifications.

No data migration or staged deployment is required because moved handlers retain their historical persisted identity. Rollback is a normal code rollback: restore the Git service registration and prior handler locations from the reverted commit if a composition or discovery failure is found. The old handler identities remain valid in either version.

## Open Questions

None. The handler ownership rule is limited to this change's existing durable state-coordinating handlers; broader Events/AgentOps/notification restructuring remains out of scope.
