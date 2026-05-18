## Context

The config-driven StageRunner now models Plan artifact generation as five user-visible tasks: `proposal`, `specs`, `design`, `tasks`, and `self-review`. Each task still executes through the generic `agent-session` task handler, which currently creates and closes a new `AgentSession` for every task invocation. That preserves task progress, but it fragments what users experience as one planning conversation into several coder session transcripts.

The workflow domain already separates task completion from session observability: `TaskRun` records status, attempts, duration, artifacts, and output, while `AgentSession` owns transcript streaming, tool calls, cancellation, and close behavior. The missing implementation concept is a stage-attempt-scoped resolver that lets an agent-session task reference a logical session name without depending on task order or previous task state.

The change should be implemented in the config-driven workflow path, because that is where task execution policies, dispatch task creation, checkpoint restore, and per-task completion are coordinated. Existing Build and Check behavior must remain task-local unless their policies opt into a named reference.

## Goals / Non-Goals

**Goals:**

- Add optional `agentSessionRef` support to agent-session task policies and task inputs.
- Resolve the same `agentSessionRef` to one real `AgentSession` within the current workflow stage attempt.
- Preserve current task-local session behavior when `agentSessionRef` is omitted.
- Configure the default Plan artifact tasks to share a named `plan-artifacts` session.
- Keep restored or skipped tasks from forcing session creation, while later tasks still resolve their configured reference deterministically.
- Close named sessions at the stage attempt lifecycle boundary, not after each task.
- Preserve independent task results and artifact validation for each Plan task.
- Allow future stages to define multiple named refs and assign different task subsets to each.

**Non-Goals:**

- Do not infer session reuse from the previous task.
- Do not collapse task state into session state.
- Do not redesign the transcript UI or session APIs beyond preserving coherent transcript grouping.
- Do not change service-call, repair-task, rebase-task, or Ralph task execution semantics.
- Do not introduce a new workflow configuration DSL.
- Do not require new persistence tables for the first implementation unless implementation discovers an existing projection cannot identify the real coder session from existing session data.

## Decisions

### D1: Put `agentSessionRef` on task execution policy and agent-session task input

Extend `TaskExecutionPolicy` with `agentSessionRef?: string`, and carry that value into `AgentSessionTaskInput`. `agentSessionRef` is only meaningful for `kind: 'agent-session'`; non-agent task kinds ignore it.

The dispatch factory should resolve the task's execution policy once and pass the policy into `TaskDispatchFactoryInput`, or pass the selected `agentSessionRef` directly. This avoids each dispatch factory repeating policy lookup logic and keeps the source of truth in the stage definition.

**Alternatives considered:** A separate session mapping table on `StageDefinition` was more explicit, but it would split one task execution concern across two config surfaces. A boolean like `reusePreviousTaskSession` was rejected because skip, restore, retry, and dynamic task insertion make "previous" ambiguous.

### D2: Default Plan policies share `plan-artifacts`

Update the default Plan task execution policies so the five artifact tasks use `agentSessionRef: 'plan-artifacts'`:

- `proposal`
- `specs`
- `design`
- `tasks`
- `self-review`

Repair and rebase runtime tasks should not receive this ref by default. They are separate operational interventions, not part of the coherent artifact-generation transcript.

**Alternatives considered:** Hard-coding Plan task IDs in `createPlanAgentSessionDispatchTask` would fix the immediate bug, but it would make future multi-ref stage designs require more special cases. Keeping the ref in stage policy makes Plan just the first consumer of a general mechanism.

### D3: Resolve named sessions through a stage-attempt-scoped registry

Add a small session registry to `StageContext`, for example:

```ts
agentSessionRegistry?: {
  getOrCreate(ref: string, options: AgentSessionOptions): Promise<AgentSession>;
  closeAll(): Promise<void>;
}
```

The `agent-session` task handler should use this rule:

- If `input.agentSessionRef` is absent, create a task-local session and close it in the handler's `finally` block, preserving current behavior.
- If `input.agentSessionRef` is present, call `ctx.agentSessionRegistry.getOrCreate(ref, options)`, execute the task prompt against the returned session, and do not close it in the task handler.
- If a named session execution fails, return a failed task result. The session can stay open for later close by the lifecycle boundary; task completion remains independent from session state.

The registry key should include the active workflow run and current stage-run attempt identity, not only issue number and stage. That ensures a retry, rerun, or rewind creates a fresh real session for the same logical ref rather than appending to an old completed transcript.

**Alternatives considered:** Storing the registry inside `AgentSessionTaskHandler` would work in a single long-lived process path but would hide lifecycle ownership in a low-level handler and make tests harder to reason about. Storing it on `StageContext` makes the lifecycle explicit and keeps the handler shallow.

### D4: Let `WorkflowEngine` own registry lifetime across requested work in one stage attempt

The aggregate workflow path builds a fresh `StageContext` for each requested task. If the registry lived only inside one context object, sequential Plan tasks would still get separate sessions. `WorkflowEngine` should therefore own a map of stage-attempt registries and attach the correct registry when building context.

The stage-attempt key should be derived from available workflow-run state, preferably `workflowRun.id`, `stageRun.id` or equivalent persisted stage-run identity, and `stage`. If the persisted shape does not expose a stage-run ID, use the strongest available attempt discriminator from the active run snapshot, and keep that derivation in one helper so it can be tightened later.

The engine should close and remove the registry when the owning stage attempt reaches a terminal boundary: passed, failed, awaiting approval, cancelled, or the pipeline completes. Closing at `awaiting approval` is acceptable for Plan because all stage work is done and the transcript should become a completed review artifact. A later stage retry will have a new stage-run attempt key and therefore a new real session.

**Alternatives considered:** Closing named sessions immediately after the last configured task would require the runner to know that no future task will use the ref, including runtime-inserted tasks. Stage-attempt boundary closing is simpler and matches the domain ownership rule.

### D5: Keep restored and skipped tasks as service-call results

The existing Plan dispatch factory converts checkpoint/disk-restored artifact tasks into `service-call` dispatch tasks. Keep that behavior. Restored tasks should still complete independently, but they should not create or touch an `AgentSession` merely because their policy has `agentSessionRef`.

Later non-restored tasks should still receive the same configured ref and resolve it normally. This satisfies the invariant that restore/skip of an intermediate task does not alter session ownership for subsequent tasks.

**Alternatives considered:** Creating a named session for restored tasks would produce misleading empty transcript entries and could create sessions for fully restored Plan runs where no agent work occurred.

### D6: Use the real `AgentSession` transcript to represent prompt blocks

No new transcript container is required. `AgentSession.execute(prompt, { kind: 'task', title })` already writes a Mohist prompt block before sending each prompt. Reusing the same real session for multiple tasks naturally produces one transcript with multiple prompt blocks.

Task output should continue to include the `acpSessionId` returned by each execution. For named refs, all participating tasks should report the same real session ID. Optionally include `agentSessionRef` in task output for easier debugging and tests, but projections must not infer task completion from session completion.

**Alternatives considered:** Adding synthetic transcript grouping in the UI would hide the fragmentation but leave the underlying runtime model wrong. Reusing the real session fixes the source of truth and keeps UI changes minimal.

## Risks / Trade-offs

- [Risk] Stage-attempt identity is not available in the current context shape → Mitigation: centralize key derivation in `WorkflowEngine` and prefer persisted workflow/stage-run IDs when available; add tests that retry Plan and assert a fresh session is created.
- [Risk] Named sessions are leaked if the pipeline exits through an error path → Mitigation: close registries in `finally` blocks around runner execution and on terminal workflow results; make `closeAll()` idempotent and best-effort.
- [Risk] Long-lived named sessions hold resources longer than task-local sessions → Mitigation: scope them only to one stage attempt and close at stage terminal boundaries, including approval pause.
- [Risk] A failed task leaves a named session in an uncertain state → Mitigation: treat the failed execution as task evidence, close the registry at failure boundary, and let retry/rerun create a fresh session instance.
- [Risk] Tests may accidentally assert session count through fragile UI projections → Mitigation: prefer runtime-level tests with injected `createSession` and transcript/log tests that assert repeated prompts share one `acpSessionId` while task results remain distinct.

## Migration Plan

1. Extend domain and task-runtime types with optional `agentSessionRef`.
2. Propagate the resolved ref from stage execution policy through `ConfigDrivenStageRunner` and `TaskDispatchFactoryRegistry` into `AgentSessionTaskInput`.
3. Add the stage-attempt session registry implementation and attach it from `WorkflowEngine.buildContext`.
4. Update `createAgentSessionTaskHandler` to use task-local sessions by default and registry-managed sessions when `agentSessionRef` is present.
5. Update default Plan task policies to use `agentSessionRef: 'plan-artifacts'`.
6. Ensure registry close is called on stage terminal boundaries and pipeline shutdown/error paths.
7. Add regression tests for shared Plan sessions, omitted-ref task-local behavior, restored Plan task behavior, multiple refs in one stage, and fresh sessions after Plan retry/rerun.

Rollback is low-risk: remove `agentSessionRef` from default Plan policies to return Plan to task-local sessions while leaving the optional runtime support dormant.

## Open Questions

- What exact persisted field should be used as the stage-attempt identity in the registry key: a stage-run row ID, an attempt number, or another existing workflow-run snapshot field?
- Should task output include `agentSessionRef` for observability, or should tests rely only on shared `acpSessionId` and transcript logs?
