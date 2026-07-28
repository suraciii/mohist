# Self Review: Issue 512 Plan

## Findings

### F1. Existing launch-plan replay is incorrectly gated by mutable launch validation

**Severity: must fix**

`design.md:28` requires the route to run existing validation before it creates a launch plan, and `tasks.json:11,17` explicitly require validation-before-plan. The current route resolves the Agent and rejects an archived Agent before it reaches `IAgentLauncher` (`packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:66-79`). Therefore, after a first request has persisted the coordinator plan but its response is lost, a later Agent archive, rename, context deletion, or runtime-override change can make an identical retry fail validation before it looks up the `(ProjectId, Idempotency-Key)` plan.

That contradicts `specs/agent-launch-idempotency/spec.md:15,27`: successful retries must return the original four identities, and the accepted Agent, context, and resolved execution definition must remain canonical. It also contradicts the product rule that an AgentJob retains its launch-time execution snapshot while later Agent edits affect only later Jobs (`docs/agents.md:104-109`). A response-loss retry is specifically the primary recovery path for this change, so this is not a corner case.

The design must distinguish a new launch from an existing-plan replay. It needs to look up the idempotency plan before mutable Agent/context/runtime validation; when a plan exists, compare the replay against the canonical original request and return or resume that plan without re-resolving mutable resources. New identities still take the current validation path and must leave no plan on rejection. Add server specs for retry after Agent archive/rename and after referenced-context mutation, and adjust T-001's validation-before-plan criterion accordingly.

<promise>FAIL</promise>
