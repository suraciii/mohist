# Review Findings

## P1. Manual launches with issue context ignore the issue backend override

The issue acceptance criterion requires an issue-level override to change the backend used by that launch. `IssueModelSelector` persists the selected backend in the issue's `vars.agent.runtime`, and the event-driven path now reads it in `RoutingDispatchHandler`. However, the Web manual Mohist Agent launch path only builds `{ agentRef, prompt, context }` (`packages/web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx:232-249`), and its input/client types contain no `runtime` field (`packages/web/src/entities/agent/api/agent-sessions.ts:59-69`). The API's optional `AgentSessionLaunchRequest.Runtime` is therefore never supplied by the UI, even when the launch includes `context.issueNumber`.

Launching an Agent from an issue in the Web UI consequently uses the Agent's configured backend instead of the issue's selected override. Load the issue runtime for manual launches with issue context, or expose and send the resolved issue override in the launch request, then add a browser/component or API spec covering that flow.

## P2. Issue runtime overrides are not validated before they enter launch resolution

`ResolveIssueRuntimeOverrideAsync` returns any non-empty string found in `IssueReadModel.AgentConfig.runtime` (`packages/server/src/Mohist.Server/Events/Subscriptions/RoutingDispatchHandler.cs:167-186`). `AgentLauncher.ResolveRuntime` silently ignores unsupported override values and falls back to the Agent configuration (`packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:295-315`). The shared `AgentConfigSchema` validation protects Agent CRUD and issue `agentConfig` writes, but issue workflow-variable patches used by `IssueModelSelector` go through `IssueWorkflowProfileManager.SetVariablesAsync` without this validation (`packages/server/src/Mohist.Server/Workflow/Services/IssueWorkflowProfileManager.cs:125-145`).

An API caller can therefore persist `vars.agent.runtime: "unknown"`, receive no validation error, and get a launch on a different backend than the issue requested. Validate the issue runtime field at the workflow-variable boundary and return an actionable error rather than silently coercing it.

<promise>FAIL</promise>
