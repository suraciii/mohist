## Why

The config-driven StageRunner migration made Plan artifact tasks user-visible as separate task rows, but it also split one planning conversation into separate coder sessions for proposal, specs, design, tasks, and self-review. Mohist needs to preserve independent task progress while keeping the agent transcript coherent, because users review Plan as one continuous planning effort with multiple produced artifacts.

## What Changes

- Add an optional `agentSessionRef` field to agent-session task execution policy/input so a task can reference a named logical session within the current stage attempt.
- Keep existing task-local session behavior for agent-session tasks that omit `agentSessionRef`.
- Resolve repeated uses of the same `agentSessionRef` to the same real `AgentSession` instance for the active WorkflowRun/StageRun attempt, while preserving separate TaskRun status, attempts, duration, artifacts, and outputs.
- Configure default Plan artifact tasks (`proposal`, `specs`, `design`, `tasks`, `self-review`) to use one named Plan session reference such as `plan-artifacts`.
- Allow one stage definition to declare multiple named agent session references so different subsets of tasks can share different transcripts.
- Close named stage sessions when their owning stage attempt is finished, and create fresh real sessions for rerun/retry/rewind attempts rather than appending to old completed transcripts.
- Preserve skip/restore behavior so restored artifact tasks do not create unnecessary sessions and later tasks still resolve their configured session reference deterministically.
- Add regression coverage proving Plan tasks can share one coder session while remaining separate user-visible tasks.

## Capabilities

### New Capabilities



### Modified Capabilities

- workflow-definition
- workflow-run
- workflow-agent
- coder-session-tracking
- session-timeline-ui

## Impact

- `packages/cli/src/workflow/domain/index.ts`: extend task execution policy/domain definitions and default Plan task policies with `agentSessionRef`.
- `packages/cli/src/workflow/task-runtime/types.ts`: carry the optional session reference through agent-session task input.
- `packages/cli/src/workflow/task-runtime/task-dispatch-factory-registry.ts`: propagate configured references when building Plan and future agent-session dispatch tasks.
- `packages/cli/src/workflow/task-runtime/agent-session-task-handler.ts`: resolve and retain named sessions per stage attempt instead of always creating and closing a task-local session.
- `packages/cli/src/workflow/config-driven-stage-runner.ts` and `packages/cli/src/workflow/stage-context.ts`: provide a stage-attempt-scoped session registry/lifecycle boundary for shared agent sessions.
- Session persistence and transcript projection (`coder_session`, `session_stream_log`, workflow log observers, issue session APIs) must continue to show separate task results while grouping prompts that used the same real session.
- Tests under `packages/cli/tests/workflow/` and `packages/cli/tests/workflow/task-runtime/` need coverage for shared Plan sessions, default task-local behavior, restore/skip stability, multiple refs in one stage, and fresh sessions on stage retry/rerun.
- No breaking CLI/API behavior is intended; existing Build and Check task session behavior remains unchanged unless a task explicitly declares an agent session reference.
