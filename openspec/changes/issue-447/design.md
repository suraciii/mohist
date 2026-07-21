## Context

The proposal requires manifest declarations to become the real Action capability boundary. The completed manifest work in issue 444 defines Action names, inputs, outputs, and errors, but `ActionDefinition.run` still receives `ActionInvocationContext`, which exposes workflow identity, server and runtime handles, recovery data, and immediate variable writes. `mohist/openspec-tasks` consequently appends tasks through `ServerConnection` during execution, and `WorkExecutor` selects promise output projection from `PROMISE_PROJECTED_ACTIONS`.

This change affects the runner's Action contract and task-result pipeline. WorkflowRun remains the state authority: the runner executes Actions, applies its local executor responsibilities, and reports a result; the server decides how reported task additions change the run. Built-in workflow behavior, OpenCode's session lifecycle, public outputs, and error codes are compatibility constraints.

## Goals / Non-Goals

**Goals:**

- Make `agent-turn`, `issue-fields`, `workflow-checkpoint`, `add-tasks`, and `write-vars` explicit, validated manifest capabilities.
- Replace Action invocation context with validated inputs and a small host whose optional members are derived from declared capabilities.
- Carry task additions and variable writes as validated private result effects, and apply them once the task has passed executor-owned checks.
- Preserve built-ins that need Issue fields, workflow-scoped archive state, parent-Issue prompt context, or deferred generated-task templates without exposing raw dispatch context.
- Derive agent promise projection from the selected manifest's capabilities.
- Preserve existing built-in workflow behavior and the current server report contract.

**Non-Goals:**

- Process isolation, an authorization system, or protection from malicious code in trusted in-process Actions.
- External Action loading, versioned Actions, or changes to workflow `with`, public Action output, or business error contracts.
- Changing WorkflowRun recovery, completion, artifact, branch, worktree, or server-side task-addition semantics.
- Making checks a source of workflow-mutating effects.

## Decisions

### 1. Manifest capabilities are a closed, immutable set

Add `capabilities: readonly ActionCapability[]` to `ActionManifest`, where `ActionCapability` is the closed union `agent-turn | issue-fields | workflow-checkpoint | add-tasks | write-vars`. `defineAction` validates that the array has no duplicates or unknown values and freezes it with the rest of the manifest. Capabilities remain Runner-local executable contract data; the existing cross-language Action catalog does not change. Built-ins declare only what they use: `mohist/opencode` declares `agent-turn`; Issue-field consumers declare `issue-fields`; `mohist/archive-change` declares `workflow-checkpoint`; `mohist/openspec-tasks` declares `add-tasks`; Actions using deferred variable writes declare `write-vars`.

The manifest is the sole authority because it is already the executable contract source. A parallel registry of capabilities would recreate the registration/contract drift that issue 444 removed.

Alternative considered: infer capabilities from Action names or imported modules. This hides the execution boundary, cannot validate custom Actions, and repeats the existing name-based coupling.

### 2. Actions receive a host, not engine context

Replace `run(context)` with `run(inputs, host)`. `inputs` is the manifest-validated `with` object. `host` exposes only `workDir`, `signal`, `log`, and `exec` by default. The executor builds this host from its private dispatch context after workspace and input validation. Action modules no longer import or receive `ActionContext`, `ServerConnection`, `OpenCodeRuntime`, workflow metadata, Variables, or recovery declarations.

The host is extended structurally only for declared capabilities. `agent-turn` adds `host.agent.turn(request)`; `issue-fields` adds `host.issue.fields()`; `workflow-checkpoint` adds `host.checkpoint.token(scope)`; `add-tasks` and `write-vars` do not add imperative calls, because they authorize private fields on a successful Action result. Runtime/session metadata and parent-Issue prompt context needed by `agent-turn`, Issue identity needed by `issue-fields`, and WorkflowRun identity needed by `workflow-checkpoint` are captured by executor-owned capability implementation closures, never exposed to the Action.

Alternative considered: retain one broad context and omit selected fields. This leaves dispatch metadata and future engine additions visible by default, so the boundary would weaken over time. A distinct host type makes the allowed surface auditable and type-checkable.

### 3. Agent-turn owns the OpenCode adapter and projection fact

Extract the runtime/session work currently embedded in `opencodeAction` into the executor's `agent-turn` capability implementation. That implementation preserves prompt resolution, parent-Issue prompt composition, session binding, runtime readiness, event reporting, cancellation, terminal close, business errors, exit codes, and the private final-assistant-text fact. `mohist/opencode` remains a small Action adapter that validates its Action-specific input and invokes `host.agent.turn`.

`projectTaskOutput` receives the selected manifest or its capability set. It evaluates and projects the private turn fact only for an Action declaring `agent-turn`; all other successful Actions retain their output. Remove `PROMISE_PROJECTED_ACTIONS` and all other Action-name conditionals used solely for this behavior.

Alternative considered: let every Action return a `turnFact`. This allows ordinary Actions to opt into agent completion behavior without declaring the runtime capability and makes private runtime facts forgeable at the Action boundary.

### 4. Private context operations replace identity fields

`issue-fields` is the only capability that returns Issue content. Its implementation captures the dispatched `projectId`, `issueNumber`, `workDir`, and `signal`, then reuses the existing Issue-field lookup behavior. Rebase and GitHub PR Actions retain `messageFrom`, `titleFrom`, `bodyFrom`, and `subjectFrom` inputs, but invoke `host.issue.fields()` rather than reading identity fields or resolving them themselves.

`workflow-checkpoint` supplies `host.checkpoint.token(scope)`, an opaque stable token derived from the private WorkflowRun identity and a canonical Action-supplied scope. `mohist/archive-change` uses that token for its checkpoint key and persisted checkpoint validation rather than observing `workflowRunId`. This preserves retry isolation without adding an identity input or retaining a broad host field.

Alternative considered: add project, Issue, or WorkflowRun identifiers to `with`. This changes public Action input contracts and makes engine identity an Action-author concern. Alternative considered: keep those identifiers as hidden host fields. That recreates the broad context under different names and permits undeclared use.

### 5. Deferred inputs replace Action-name-specific raw dispatch injection

Add a rendering-timing field to `ActionInputDeclaration`, defaulting to immediate. The executor expands immediate fields, but copies declared deferred fields from the original `work.with` into the validated input object after top-level kind validation. `mohist/openspec-tasks.task` declares deferred rendering. It reads `inputs.task` directly and preserves its nested templates in the generated task `with`; those templates are expanded only when the generated task is later dispatched.

This replaces `OpenSpecTasksInvocationContext.rawTask` and the executor's `definition.manifest.name === "mohist/openspec-tasks"` branch. No Action receives raw dispatch context, and no Action name selects a special input path.

Alternative considered: defer rendering for every object input. Many objects, such as OpenCode options, intentionally resolve from current Variables at the parent task's dispatch; broad deferral changes their behavior. Per-input declaration makes the exception visible and local to the task-template contract.

### 6. Effects are normalized with the Action result and applied at the executor tail

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

### 7. Migrate built-ins and test support atomically

Migrate built-in Actions to `(inputs, host)` in one change, preserving their manifest input defaults, output shape, and declared business errors. Replace direct command helpers with the default host's command execution and log surface. Migrate Issue-field consumers to `issue-fields`, archive checkpointing to `workflow-checkpoint`, and OpenCode's parent-Issue/session behavior to `agent-turn`. Move the OpenSpec task loader's generated tasks into `effects.addTasks` and migrate its deferred `task` default to the manifest-declared input path. Delete direct `ServerConnection.addTasks`, raw dispatch injection, and immediate `writeVars` from Action-facing types.

Update unit tests to construct Action hosts and fake capability implementations rather than broad Action contexts. Cover manifest validation, absent capability members, opaque issue-field and checkpoint behavior, agent-turn projection for a non-OpenCode test Action, deferred OpenSpec task templates without raw Action context, effect authorization, deferred addition suppression after failed postconditions, one merged variable patch with `setVars` precedence, and patch failure. Keep existing built-in workflow specs as the compatibility suite, using fake runtime, connection, process, and filesystem seams as required by the test policy.

Alternative considered: retain compatibility overloads for `run(context)`. This would keep forbidden fields available and let new Actions bypass the host boundary, so the migration must be atomic.

## Risks / Trade-offs

- [Migrating every built-in Action changes a broad TypeScript call signature] -> Keep input parsing semantics in each Action, use a typed host factory, and run the complete runner typecheck plus built-in workflow specs.
- [Deferred effects change when a variable write becomes visible] -> Apply effects only after executor postconditions, document this completed-task boundary, and preserve same-task `setVars` precedence in the merged patch.
- [A failed variable patch can leave external Action work complete but task state failed] -> Preserve the existing task-failure behavior for variable persistence and do not report task additions after that failure.
- [Agent-turn extraction can alter session or runtime-event ordering or parent-Issue prompt composition] -> Move the existing OpenCode flow behind the capability without changing its sequencing or prompt construction; retain existing OpenCode turn and transcript specs.
- [Deferred task templates can expand at the wrong dispatch boundary] -> Declare `mohist/openspec-tasks.task` as deferred, test nested template preservation, and remove the name-gated raw input branch.
- [Custom Actions compiled against the old in-process TypeScript interface break] -> This is an intentional source-level refactor within the trusted runner extension point; no external plugin compatibility is supported.

## Migration Plan

1. Add capability declarations, input rendering timing, host/result-effect types, manifest validation, and focused unit tests.
2. Implement executor-owned agent-turn, issue-fields, and workflow-checkpoint capabilities, then replace name-based promise projection and raw-task injection with manifest-driven behavior.
3. Migrate all built-in Actions, including OpenCode, Issue-field consumers, archive checkpointing, and OpenSpec task loading; remove broad Action context exports and direct server mutations.
4. Route normalized variable effects through the existing `setVars` application point, then carry normalized task additions in the existing task report.
5. Run runner typecheck and tests, including built-in profile specs. No server deployment or data migration is required because the task report and variable patch APIs are unchanged.

Rollback is a code rollback. No persisted schema, Action catalog compatibility protocol, or WorkflowRun data migration is introduced; in-flight tasks retry with the Runner version selected by normal deployment recovery.

## Open Questions

None. The capability names, effect timing, check behavior, and `setVars` conflict precedence are fixed by this design.
