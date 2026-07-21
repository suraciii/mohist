## Context

The proposal requires manifest declarations to become the real Action capability boundary. The completed manifest work in issue 444 defines Action names, inputs, outputs, and errors, but `ActionDefinition.run` still receives `ActionInvocationContext`, which exposes workflow identity, server and runtime handles, recovery data, and immediate variable writes. `mohist/openspec-tasks` consequently appends tasks through `ServerConnection` during execution, and `WorkExecutor` selects promise output projection from `PROMISE_PROJECTED_ACTIONS`.

This change affects the runner's Action contract and task-result pipeline. WorkflowRun remains the state authority: the runner executes Actions, applies its local executor responsibilities, and reports a result; the server decides how reported task additions change the run. Built-in workflow behavior, OpenCode's session lifecycle, public outputs, and error codes are compatibility constraints.

## Goals / Non-Goals

**Goals:**

- Make `agent-turn`, `add-tasks`, and `write-vars` explicit, validated manifest capabilities.
- Replace Action invocation context with validated inputs and a small host whose optional members are derived from declared capabilities.
- Carry task additions and variable writes as validated private result effects, and apply them once the task has passed executor-owned checks.
- Derive agent promise projection from the selected manifest's capabilities.
- Preserve existing built-in workflow behavior and the current server report contract.

**Non-Goals:**

- Process isolation, an authorization system, or protection from malicious code in trusted in-process Actions.
- External Action loading, versioned Actions, or changes to workflow `with`, public Action output, or business error contracts.
- Changing WorkflowRun recovery, completion, artifact, branch, worktree, or server-side task-addition semantics.
- Making checks a source of workflow-mutating effects.

## Decisions

### 1. Manifest capabilities are a closed, immutable set

Add `capabilities: readonly ActionCapability[]` to `ActionManifest`, where `ActionCapability` is the closed union `agent-turn | add-tasks | write-vars`. `defineAction` validates that the array has no duplicates or unknown values and freezes it with the rest of the manifest. Capabilities remain Runner-local executable contract data; the existing cross-language Action catalog does not change. Built-ins declare only what they use: `mohist/opencode` declares `agent-turn`; `mohist/openspec-tasks` declares `add-tasks`; Actions using deferred variable writes declare `write-vars`.

The manifest is the sole authority because it is already the executable contract source. A parallel registry of capabilities would recreate the registration/contract drift that issue 444 removed.

Alternative considered: infer capabilities from Action names or imported modules. This hides the execution boundary, cannot validate custom Actions, and repeats the existing name-based coupling.

### 2. Actions receive a host, not engine context

Replace `run(context)` with `run(inputs, host)`. `inputs` is the manifest-validated `with` object. `host` exposes only `workDir`, `signal`, `log`, and `exec` by default. The executor builds this host from its private dispatch context after workspace and input validation. Action modules no longer import or receive `ActionContext`, `ServerConnection`, `OpenCodeRuntime`, workflow metadata, Variables, or recovery declarations.

The host is extended structurally only for declared capabilities. `agent-turn` adds `host.agent.turn(request)`; `add-tasks` and `write-vars` do not add imperative calls, because they authorize private fields on a successful Action result. Runtime/session metadata needed by `agent-turn` is captured by the executor-owned capability implementation closure, never exposed to the Action.

Alternative considered: retain one broad context and omit selected fields. This leaves dispatch metadata and future engine additions visible by default, so the boundary would weaken over time. A distinct host type makes the allowed surface auditable and type-checkable.

### 3. Agent-turn owns the OpenCode adapter and projection fact

Extract the runtime/session work currently embedded in `opencodeAction` into the executor's `agent-turn` capability implementation. That implementation preserves prompt resolution, session binding, runtime readiness, event reporting, cancellation, terminal close, business errors, exit codes, and the private final-assistant-text fact. `mohist/opencode` remains a small Action adapter that validates its Action-specific input and invokes `host.agent.turn`.

`projectTaskOutput` receives the selected manifest or its capability set. It evaluates and projects the private turn fact only for an Action declaring `agent-turn`; all other successful Actions retain their output. Remove `PROMISE_PROJECTED_ACTIONS` and all other Action-name conditionals used solely for this behavior.

Alternative considered: let every Action return a `turnFact`. This allows ordinary Actions to opt into agent completion behavior without declaring the runtime capability and makes private runtime facts forgeable at the Action boundary.

### 4. Effects are normalized with the Action result and applied at the executor tail

Extend the internal successful Action result with a private `effects` object:

```ts
type ActionEffects = {
  addTasks?: AddTaskInput[]
  writeVars?: JsonObject
}
```

`normalizeActionResult` validates output, error ownership, effect shape, and capability authorization together. Effects on an error result, malformed effects, or an effect absent from the manifest's capabilities normalize to `unexpected-error`; no effect survives that failure. The normalizer returns effects separately from public output, and `ActionResult` facts/effects are never copied into `TaskRun.Output`, recovery matching, captured outputs, or artifacts.

After completion evaluation, recovery, end branch checks, worktree enforcement, artifact upload, and output capture succeed, the executor applies variable effects and attaches task additions to `WorkItemResult.addTasks`. It combines `effects.writeVars` with declarative `setVars` into one `patchRunVars` call. Declarative `setVars` is merged last and therefore keeps its current precedence when both write the same key. A failed extraction or patch returns a failed task and does not attach task additions. The existing report endpoint carries `addTasks`; the server remains responsible for appending them to the WorkflowRun.

Checks use the same manifest validation and host construction but have no effect delivery channel. A check result containing effects fails that check with `unexpected-error` rather than silently dropping a requested mutation.

Alternative considered: let Actions call `patchRunVars` and `addTasks` through injected imperative methods. This still permits mutations before task postconditions and splits effect ordering across Action implementations. Alternative considered: add a new server endpoint for Action effects. The existing task report already carries additions, while variable patches use the established Run Variables endpoint; a new endpoint adds protocol and ordering surface without new behavior.

### 5. Migrate built-ins and test support atomically

Migrate built-in Actions to `(inputs, host)` in one change, preserving their manifest input defaults, output shape, and declared business errors. Replace direct command helpers with the default host's command execution and log surface. Move the OpenSpec task loader's generated tasks into `effects.addTasks`, retaining its parsing and task construction. Delete the direct `ServerConnection.addTasks` dependency and the immediate `writeVars` member from Action-facing types.

Update unit tests to construct Action hosts and fake capability implementations rather than broad Action contexts. Cover manifest validation, absent capability members, agent-turn projection for a non-OpenCode test Action, effect authorization, deferred addition suppression after failed postconditions, one merged variable patch with `setVars` precedence, and patch failure. Keep existing built-in workflow specs as the compatibility suite, using fake runtime, connection, process, and filesystem seams as required by the test policy.

Alternative considered: retain compatibility overloads for `run(context)`. This would keep forbidden fields available and let new Actions bypass the host boundary, so the migration must be atomic.

## Risks / Trade-offs

- [Migrating every built-in Action changes a broad TypeScript call signature] -> Keep input parsing semantics in each Action, use a typed host factory, and run the complete runner typecheck plus built-in workflow specs.
- [Deferred effects change when a variable write becomes visible] -> Apply effects only after executor postconditions, document this completed-task boundary, and preserve same-task `setVars` precedence in the merged patch.
- [A failed variable patch can leave external Action work complete but task state failed] -> Preserve the existing task-failure behavior for variable persistence and do not report task additions after that failure.
- [Agent-turn extraction can alter session or runtime-event ordering] -> Move the existing OpenCode flow behind the capability without changing its sequencing; retain existing OpenCode turn and transcript specs.
- [Custom Actions compiled against the old in-process TypeScript interface break] -> This is an intentional source-level refactor within the trusted runner extension point; no external plugin compatibility is supported.

## Migration Plan

1. Add capability declarations, host/result-effect types, manifest validation, and focused unit tests.
2. Implement the executor-owned host factory and agent-turn capability, then replace name-based promise projection with a manifest capability check.
3. Migrate all built-in Actions, including OpenCode and OpenSpec task loading, and remove broad Action context exports and direct server mutations.
4. Route normalized variable effects through the existing `setVars` application point, then carry normalized task additions in the existing task report.
5. Run runner typecheck and tests, including built-in profile specs. No server deployment or data migration is required because the task report and variable patch APIs are unchanged.

Rollback is a code rollback. No persisted schema, Action catalog compatibility protocol, or WorkflowRun data migration is introduced; in-flight tasks retry with the Runner version selected by normal deployment recovery.

## Open Questions

None. The capability names, effect timing, check behavior, and `setVars` conflict precedence are fixed by this design.
