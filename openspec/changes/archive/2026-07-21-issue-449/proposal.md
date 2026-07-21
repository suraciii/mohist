## Why

A routing rule can report that it would trigger a Named Agent while the real dispatch omits the workspace required to execute that AgentJob, causing an immediate failure with no actionable explanation or link back to the triggering issue. Routing must produce executable work or an observable failure now that operators are expected to validate automation with `mo routing test` and rely on the same rules in production.

## What Changes

- Make a routing-rule hit launch its Named Agent with a real execution workspace when the triggering event identifies a workflow run or issue workspace, while preserving envelope-only rule matching and prompt rendering.
- Treat inability to establish the required execution workspace as an explicit, actionable launch or job failure rather than a silent missing-input failure.
- Surface an AgentJob's persisted failure reason in the generic AgentSession summary and in `mo agent session show`, alongside its failure category.
- Make a routing-triggered AgentJob failure discoverable from the triggering event and associated issue event feed, preserving the event and rule correlation through the resulting AgentSession.
- Keep manual Named Agent launch behavior and Inline Agent workspace handling unchanged.

## Capabilities

- `routing-dispatch`: Execution-ready Named Agent launches for routing hits, including workspace resolution after envelope-only matching, explicit workspace-resolution failure, idempotent trigger correlation, and traceability of the routed outcome from the triggering event or issue.
- `agent-session-visibility`: Generic AgentSession failure summaries across API and CLI, including the persisted AgentJob failure reason as well as its failure category.

## Impact

- **Server routing and Agent launch** (`packages/server/src/Mohist.Server/Events/Subscriptions`, `Agent/Services`, and related Workflow/Issue workspace reads): routed launches gain the physical workspace context required by the AgentJob without changing rule matching or prompt-rendering inputs.
- **Session and event read surfaces** (`packages/server/src/Mohist.Server/Sessions`, `AgentOps`, and issue/workflow event queries): generic session summaries expose the existing failure reason, and routed session outcomes retain enough trigger and issue lineage to appear from the originating context.
- **CLI** (`packages/cli/Mohist.Cli`): `mo agent session show` renders the failure reason returned by the generic session API; manual launch commands are unchanged.
- **Runner contract** (`packages/runner/src/runtime/agent-job-executor.ts`): the required AgentJob workspace input remains required; routed dispatch is brought into conformance rather than weakening runner validation. Inline Agent execution is unchanged.
- **Dependencies and compatibility**: no new external dependencies, data migration, or breaking API removal is expected. Focused server, runner-contract, and CLI coverage is required for routed execution, failure visibility, and trigger-to-issue traceability.
