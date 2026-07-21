# Review Findings

## Findings

### 1. [blocker] OpenSpec task loading drops the default prompt

`packages/runner/src/actions/openspec.ts:96-107` computes `itemsPath` but never uses it, and `mergeTaskWith` no longer creates the default `mohist/openspec-task-prompt` loader spec when a source task has no `with.prompt`. As a result, the normal `tasks.json` entry that only contains task metadata produces a follow-up task whose `uses` defaults to `mohist/opencode` but whose `with` has no `prompt`; the generated task then fails OpenCode input validation instead of executing the task described in the OpenSpec file. This changes the required external behavior and violates the issue's requirement to preserve generated task content and OpenSpec behavior. Restore the default deferred prompt-loader input, including the configured task file/items path and any existing build-prompt context, while retaining the new result-effect channel.

### 2. [major] The forbidden broad ActionContext remains an exported Action-facing type

`packages/runner/src/core/types.ts:226-288` still exports `ActionContext` with workflow identity, variables, recovery, parent issue context, server connection, OpenCode runtime, runtime-event outbox, and imperative `writeVars`. The new `ActionDefinition.run` no longer consumes it, but test adapters and other in-repository callers still import and construct it, and the old capability surface remains available as a supported type for Action implementations. This contradicts the design's atomic migration requirement to remove broad Action context exports and leaves the forbidden server/runtime/variable operations in the Action-facing API. Remove the legacy type and migrate remaining adapters/helpers to the narrow `(inputs, host)` contract so the old surface cannot be used to bypass the manifest boundary.

<promise>FAIL</promise>
