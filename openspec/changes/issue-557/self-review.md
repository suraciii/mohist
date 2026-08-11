# Self-Review: issue-557

## Must-Fix Findings

None.

## Previous Finding Dispositions

### 1. Fixed - Post-admission temporary failures now have a retryable owner

The design now separates confirmed pre-execution temporary outcomes from
terminal incompatibility and inconclusive delivery. It sends
`runtime-unavailable`, `model-unavailable`, and `effort-unavailable` back to
`Pending`, clears only the Runner assignment, preserves the four-field
snapshot, and uses existing recovery/admission signals without polling
(`design.md:230-245`). The compatibility and snapshot specs make the same
behavior normative (`specs/agent-execution-compatibility/spec.md:73-89`,
`specs/agent-job-execution-snapshot/spec.md:71-85`).

T-003 explicitly owns the Job state transition, exact-snapshot re-admission,
Runner-loss reconciliation distinction, and regression tests; T-004 owns the
Runner outcome classification (`tasks.json:50-58`, `:72-79`). This directly
closes the previous gap against the temporary-unavailability and no-fallback
requirements.

### 2. Fixed - The frozen projection has complete server and consumer owners

One `AgentExecutionProjectionMapper` now maps mutable Agent reads separately
from frozen accepted-launch, AgentSession, AgentJob, launch-observation, and
terminal-result reads (`design.md:106-131`). The snapshot spec explicitly
requires AgentSession info/list/summary, Job views/observations, and terminal
results to use that projection and accepted snapshot
(`specs/agent-job-execution-snapshot/spec.md:49-69`).

T-003 owns the server DTO mappings and immutable read-back tests, while T-005
owns Web/CLI consumption and cross-surface contract/end-to-end coverage
(`tasks.json:50-58`, `:93-99`). The implementation can no longer satisfy only
Agent list output while omitting effort or variant from Session, Job, or
observation surfaces.

### 3. Fixed - Agent Connection and Slack paths use the Agent contract

The proposal and reasoning-effort spec now include Agent Connection readiness
and launch (`proposal.md:9-13`, `specs/agent-reasoning-effort/spec.md:45-59`).
The design requires `AgentConnectionStore`, Slack readiness, and
`LaunchConnectionAsync` to use the shared reader/evaluator and frozen
definition instead of raw `AgentConfig` or `AgentReadinessDeriver`
(`design.md:226-228`, `:273-276`).

T-002 owns Connection list/detail readiness and removal of the legacy deriver;
T-003 owns Connection/Slack launch resolution and snapshot tests; T-005 owns
the user-facing readiness views and end-to-end consistency checks
(`tasks.json:29-36`, `:50-58`, `:93-99`). These assignments cover the distinct
current Connection entry points identified by the previous review.

### 4. Fixed - Runtime-specific capability publication is assigned and tested

The compatibility spec now requires Pi `thinkingLevels` to become canonical
`ReasoningEfforts` without populating `Variants`, and requires OpenCode to stay
incomplete when it has no independent effort source
(`specs/agent-execution-compatibility/spec.md:17-27`). The design assigns the
same publication behavior to the runtime adapters and keeps completeness and
variant facts independent (`design.md:137-173`).

T-002 owns the Runner catalog publishers, registration/heartbeat retention,
Pi/OpenCode payload behavior, per-Runner selection, and their tests. T-004
consumes those facts at execution time and verifies that Pi no longer treats
variant as thinking level (`tasks.json:29-41`, `:72-79`). This closes the
previous risk that a complete evaluator would receive no truthful runtime
facts.

## Re-Review Coverage

- Issue baseline: checked. `mo issue view 557 --project proj_f6c141d63b6243bfbb481737b2243b87` still reports the P1 planning issue with an empty body, so the proposal goals and all three capability specs remain the acceptance baseline.
- Dispositions: PASS. All four previous must-fix findings are fixed by concrete design semantics, normative scenarios, task owners, and test criteria; no unsupported won't-fix disposition was used.
- Regression check: PASS. The fixes retain Agent authority, append-only snapshot evolution, no provider probing, no tuple fallback, and the saved-Agent-only Workflow boundary.
- Codebase consistency: PASS. The tasks now explicitly migrate the currently terminal result path, raw Job/Session read shapes, direct Connection config parsing and legacy readiness derivation, and Pi's variant-based catalog mapping.
- Task breakdown: PASS. `tasks.json` is valid JSON; all dependencies exist, point to lower-priority tasks, and form an acyclic graph. Each task spec anchor resolves.
- Verifiability: PASS. Acceptance criteria now cover post-admission retry and reconciliation, every frozen read projection, Connection/Slack readiness and launch, and Pi/OpenCode registration and heartbeat facts.

## Observations

- The exact OpenCode native effort source remains an open question (`design.md:423-425`). This is non-blocking because the spec and T-002 make incomplete-catalog behavior the required default until an independent source exists.
- The rollout audit and Runner restart remain migration instructions rather than a task acceptance criterion (`design.md:406-411`, `tasks.json:107`). This does not make the build plan incomplete for the issue: unset and overloaded Agents are already required to remain setup-required without mutation, and the repository explicitly does not require rolling-version compatibility during active development.
- `design.md:128-131` permits `unset` in the projection state set for a newly accepted snapshot even though `design.md:62-65` rejects unset effort at readiness. The stronger readiness and task criteria prevent such a launch, so the broader mapper domain is harmless rather than an acceptance gap.

## Verdict

PASS. Every previous must-fix finding has a concrete implementation owner and verifiable acceptance criteria, and the fixes introduce no must-fix regression. The plan is ready to build.

<promise>PASS</promise>
