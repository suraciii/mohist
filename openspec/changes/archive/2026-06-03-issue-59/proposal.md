## Why

Build-stage task loading currently mixes two separate responsibilities: expanding `tasks.json` into runnable agent tasks and composing each agent prompt from OpenSpec task data. This makes prompt composition hard to reuse, forces richer prompt logic into loader actions, and risks template-rendering task JSON content that should remain opaque.

## What Changes

- Allow `mohist/acp-agent` task `with.prompt` to accept a unified prompt spec: literal strings, structured objects, or loader-backed objects with `uses`.
- Add a runner-side prompt loader registry so prompt resolution can happen lazily when an agent task runs, without changing workflow runtime or wire protocol shape.
- Add a stable default structured prompt renderer that converts simple JSON objects into LLM-friendly XML-like text.
- Add built-in `mohist/openspec-task-prompt` prompt loading for selecting a task from `tasks.json` by `taskId` or `index` and composing it with a base prompt.
- Reduce `mohist/openspec-tasks` to a thin runtime task loader that injects prompt-loader specs only when callers have not provided an explicit prompt.
- Update the default OpenSpec build workflow shape so build prompts are composed by the prompt loader instead of by the task loader.
- Preserve backward compatibility for existing string prompts and tasks that omit `with.prompt`, including the legacy fallback prompt path.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `workflow-agent`: Agent task prompt resolution changes from string-only input to a unified prompt spec with structured rendering and loader-backed resolution.
- `workflow-definition`: The default OpenSpec build-stage task loading contract changes so generated `mohist/acp-agent` tasks can delegate prompt composition to `mohist/openspec-task-prompt` while preserving caller prompt overrides.

## Impact

- Affected runner code includes `packages/runner/src/actions/acp-agent.ts`, OpenSpec task loading in `packages/runner/src/actions/openspec.ts`, new prompt core/registry modules, and a new built-in OpenSpec task prompt loader.
- Affected workflow configuration includes the built-in `mohist-default.workflow.yaml` build stage prompt shape.
- Existing `WorkItem.with` payloads remain wire-compatible because prompt specs are still ordinary JSON.
- Tests in `packages/runner/tests/` need coverage for structured prompt rendering, prompt loader resolution, OpenSpec task prompt selection, and updated OpenSpec task loader output.
