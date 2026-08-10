# Self-Review: issue-557

## Must-Fix Findings

### 1. P1 - Post-admission temporary failures have no retryable state

The compatibility plan handles the case where no eligible Runner is known before
dispatch, but it does not define what happens when a valid frozen tuple becomes
temporarily unavailable after admission. `design.md:191-206` only specifies
pre-admission catalog selection and waiting. `tasks.json:29-35` assigns that
behavior to T-002, while T-003 and T-004 only promise snapshot retention and
diagnostics (`tasks.json:49-55`, `tasks.json:69-75`). No task assigns a
post-admission state transition, retry trigger, or distinction between a
temporary runtime/model/effort failure and a terminal incompatibility.

This is not an abstract concern in the current state machine. Every non-success
Runner result is terminalized by `AgentJobGrain.ReportResultAsync`
(`packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:339-367`), and
Runner loss is currently reported through the same failed-result path
(`packages/server/src/Mohist.Server/Runner/Grains/RunnerGrain.cs:546-553`). The
existing `Unknown` state is explicitly non-dispatchable and does not auto-replay
(`packages/server/src/Mohist.Server/Agent/Grains/IAgentJobGrain.cs:98-110`,
`:317-333`).

This violates `specs/agent-execution-compatibility/spec.md:55-64` and
`specs/agent-job-execution-snapshot/spec.md:65-78`: a valid tuple that is
temporarily unavailable must wait or retry with the same runtime, model, effort,
and variant, while a known incompatible tuple must fail before dispatch. Extend
the task graph with an explicit owner for the retryable outcome/state-machine
transition, recovery signal, exact-snapshot re-admission, and tests for
post-admission `runtime-unavailable` and temporary model/effort failures.

### 2. P1 - The required frozen execution projection has no complete public-surface owner

The design requires one projection on Agent list/detail, readiness, accepted
launch, launch observation, AgentSession facts, Job views, and terminal results
(`design.md:104-118`). The snapshot spec repeats that requirement for accepted
launch responses, Job observations, AgentSession facts, and terminal results
(`specs/agent-job-execution-snapshot/spec.md:49-63`). The task graph does not
explicitly assign the server DTO mappings and contract tests for all of those
surfaces.

The current read shapes demonstrate the gap: `AgentJobViewDto` exposes the raw
`AgentExecutionDefinition` rather than the common projection
(`packages/server/src/Mohist.Server/Api/AgentJobReadRoutes.cs:155-163`,
`:214-222`); `AgentLaunchObservationDto` has no execution tuple at all
(`packages/server/src/Mohist.Server/Agent/Services/AgentLaunchObservationAssembler.cs:22-39`);
and `AgentSessionInfo`, `AgentSessionListItemDto`, and
`GenericAgentSessionSummaryDto` expose model/runtime summaries without effort
or variant (`packages/server/src/Mohist.Server/Sessions/Grains/IAgentSessionGrain.cs:312-337`,
`packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs:212-281`).
The durable Session settings already contain the definition, but
`AgentSessionGrain.ToInfoAsync` does not map it (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:2435-2464`).

T-003 covers durable AgentSession state and accepted launch replay, but its
acceptance criteria do not require public Session or Job DTO projection
(`tasks.json:49-55`). T-005 mentions launch observation and terminal results,
but does not name AgentSession or the generic AgentJob read surface
(`tasks.json:89-95`). Without an explicit owner and tests sourced from the
frozen snapshot, the implementation can satisfy Agent list/CLI output while
still returning no `reasoningEffort`, no `variant`, or a mutable/raw definition
on observation and Session/Job reads. Add the missing DTO mappings, field/state
semantics, and immutable read-back tests to T-003/T-005.

### 3. P1 - Slack Connection launch and readiness bypass the new Agent contract

The issue applies to saved-Agent execution entry points, but the plan never
names the Agent Connection path. `AgentLauncher.LaunchConnectionAsync` currently
reads only runtime, model, and variant directly from raw `AgentConfig`
(`packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:410-471`),
so it cannot resolve or freeze reasoning effort through the proposed reader.
Slack launch routing still gates the call with the legacy
`AgentReadinessDeriver` and then invokes that method
(`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1288-1294`,
`:1364-1381`). Connection list/detail readiness also uses that legacy deriver
(`packages/server/src/Mohist.Server/Agent/Services/AgentConnectionStore.cs:43-68`).

This bypasses the central-reader rule in `design.md:91-118`, the complete-tuple
readiness rule in `specs/agent-execution-compatibility/spec.md:17-43`, and the
Agent-authoritative launch rule in
`specs/agent-job-execution-snapshot/spec.md:1-15`. A configured effort could
therefore be reported as setup-complete or launched without effort, while
`variant` continues to follow the old interpretation on the Connection path.
T-003's phrase "routed preparation" is not an explicit owner for this distinct
`LaunchConnectionAsync` path, and no task acceptance criterion covers it. Add
Connection launch, Connection readiness/list/detail, and their regression tests
to the relevant configuration, compatibility, and snapshot tasks, or explicitly
change the spec scope before implementation.

### 4. P1 - Runtime-specific capability publication is not assigned or verified

The compatibility requirement is not satisfied by adding nullable fields to the
Server catalog record. Each registered runtime must publish truthful
model-to-effort facts, keep variants separate, and mark unknown completeness
(`specs/agent-execution-compatibility/spec.md:1-15`). The design gives concrete
runtime obligations: Pi must map SDK `thinkingLevels` to `ReasoningEfforts`, and
OpenCode must either publish an independent effort source or remain incomplete
(`design.md:124-155`).

The current Runner still maps Pi thinking levels into `Variants`
(`packages/runner/src/runtime/host.ts:109-116`), and the Runner catalog type has
only `models` and `variants` (`packages/runner/src/core/types.ts:379-392`). T-002
requires the new fields to be retained by registration but does not require
runtime adapters to populate them (`tasks.json:29-35`). T-004 discusses native
execution inputs and tests dispatch behavior, but does not require registration
payload tests for Pi mapping or OpenCode incomplete-catalog behavior
(`tasks.json:69-75`).

Without an explicit owner and acceptance criteria, the evaluator can be fully
implemented while real Runners publish empty/incomplete effort facts, leaving
all configured Jobs permanently unconfirmed and failing the runtime capability
goal. Assign Pi catalog projection and the OpenCode explicit-source/incomplete
rule to a task, and test registration, heartbeat, per-Runner selection, and
separation from `Variants`.

## Review Coverage

- Issue baseline: checked. `mo issue view 557 --project proj_f6c141d63b6243bfbb481737b2243b87` confirms the P1 planning issue, but its body is empty; proposal goals, design goals/non-goals, and all three capability specs were therefore used as the acceptance baseline.
- Coverage: FAIL. The four findings above leave the temporary-failure state machine, several required read surfaces, Connection entry points, and concrete capability publication insufficiently tasked.
- Correctness: FAIL. The stated no-fallback behavior is correct for pre-admission catalog rejection, but the plan does not prevent post-admission temporary failure from becoming terminal or prevent Connection launches from bypassing the tuple resolver.
- Codebase consistency: FAIL. Existing direct raw-config parsing, duplicate readiness paths, raw Job read definitions, and Runner variant-based Pi catalog mapping are not all assigned explicit migrations.
- Task breakdown: FAIL. T-002 claims exact tuple waiting before T-003 owns the durable effort field, and the runtime-specific catalog work is split between T-002 and T-004 without a clear owner or acceptance test. The dependency graph itself is acyclic and points only to lower-priority tasks.
- Verifiability: FAIL. Existing task tests cover schema, compatibility, snapshots, Runner dispatch, and Web/CLI broadly, but no acceptance criterion proves post-admission retry, Connection launch/readiness, public AgentSession/AgentJob projection, or actual Pi/OpenCode registration facts.

## Observations

- `design.md:145-148` and `:372-379` leave the exact OpenCode native effort source as an open question. The incomplete-catalog fallback is specified, so this is non-blocking only if the implementation and tests make that fallback the default until an explicit source exists.
- The rollout audit and Runner restart requirement appear only in `design.md:357-362` and the T-005 notes (`tasks.json:103`), not in acceptance criteria. Existing Agents can consequently remain setup-required without a concrete rollout verification step.
- T-002's "exact-tuple preservation during temporary waiting" criterion (`tasks.json:35`) should be moved to T-003 or defined as a pre-snapshot evaluator assertion, because T-003 owns the durable tuple fields (`tasks.json:49-61`).
- The plan correctly keeps `reasoningEffort` out of inline Workflow options and keeps Workflow `options.variant` unchanged (`design.md:39-49`, `:280-302`, `tasks.json:14`, `:75`, `:94`).
- The existing `AgentExecutionDefinition` to AgentJob/WorkDispatch boundary is the right durability seam (`design.md:10-15`); the review findings require explicit ownership around that seam rather than a second execution configuration path.

## Verdict

The plan is not ready to build until the four P1 findings are assigned concrete implementation owners and acceptance criteria. The dependency graph and canonical effort vocabulary are otherwise coherent, and no unrelated scope expansion was identified.

<promise>FAIL</promise>
