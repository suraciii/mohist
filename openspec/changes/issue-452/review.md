# Review Findings

## P1. Issue-level backend override is not applied to Mohist Agent launches

The issue acceptance criterion requires an issue-level override to change the backend used by that launch. The implementation only adds `runtime` to the generic manual launch request, and the Web launch page does not send that field (`packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:232-249`). More importantly, the event-driven Mohist Agent path resolves only the Agent definition: `RoutingDispatchHandler` calls `AgentLauncher.ResolveRuntime(agent.AgentConfig, launchOverride: null)` (`packages/server/src/Mohist.Server/Events/Subscriptions/RoutingDispatchHandler.cs:120-142`). The issue's `vars.agent.runtime` value edited by `IssueModelSelector` is used by the Workflow model-selection path, but is never read by this routed Mohist Agent launch.

As a result, changing the backend at issue level cannot affect the corresponding Mohist AgentJob; it always uses the Agent-configured backend. Add an issue-owned override to the routed launch input and resolve it before the durable `RoutedAgentLaunchPlan` snapshot, with a test proving override precedence end to end.

## P1. Empty runtime values are accepted and silently coerced to OpenCode

`AgentConfigSchema.ValidateRuntime(JsonElement)` returns success for an empty or whitespace string (`packages/server/src/Mohist.Server/Infrastructure/AgentConfigSchema.cs:89-100`). `AgentLauncher.ResolveRuntime` then defaults that value to `opencode` (`packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:303-315`). This violates the contract that any value other than `opencode` or `pi` is rejected rather than silently coerced. The added test codifies the wrong behavior in `AgentLauncherResolveRuntimeTests.cs:65-71`.

Reject empty and whitespace runtime strings at the API/schema boundary, and add regression coverage for both Agent CRUD and issue configuration writes.

## P2. Some Pi AgentJob failures are still labeled as OpenCode

`failureResult` always constructs diagnostic output through `buildAgentJobOutput(..., "opencode", ...)` (`packages/runner/src/runtime/agent-job-executor.ts:418-429`). The Pi path calls this helper for Pi session-creation failures (`agent-job-executor.ts:181-188`), so a failed Pi AgentJob can produce terminal output with `kind: "opencode"`, contrary to the runtime-labeling requirement and the plan's D4 decision. Pass the selected runtime through failure construction and assert the Pi label on a Pi failure with diagnostics.

Verification performed: runner typecheck and 1,354 runner tests passed; web typecheck and 5,109 web tests passed; .NET solution build and 1,299 server unit tests passed. The server spec test process exceeded the 120-second command timeout before reporting a result.

<promise>FAIL</promise>
