## Context

The proposal and the `task-input-rendering-boundary` and
`recovery-self-retry-declaration` specs require persisted task declarations to
remain distinct from an attempt's execution input. Today those forms collapse
at the Server/Runner boundary:

1. `WorkflowItemTranslator.BuildTaskDispatchAsync` and
   `BuildChecksDispatchAsync` resolve the Effective Stage Variables, assemble
   a dispatch context, then call `ExpandToJson` for `with` and `expect`.
2. The Runner deserializes the already-expanded JSON into `RenderedWorkItem`.
   `WorkExecutor` renders it again before manifest validation, and
   `executeCheckDispatch` follows the same pattern for checks.
3. `tryRecovery` copies `work.with` and `work.expect` into a `retrySelf`
   task. Because this is the Server-expanded form, a model selected for the
   triggering attempt becomes the next task's persisted declaration.

The Runner already owns the complete template renderer: it has the dispatch
context, preserves whole-value JSON types, recurses into objects and arrays,
detects unresolved immediate references, and skips manifest-deferred fields.
The Server must still resolve Effective Stage Variables and load the prompt
bodies when a dispatch is constructed. Those values, plus the remaining
workflow context, are the immutable snapshot for that attempt. Workspace
facts are added locally by the Runner after workspace preparation and are not
configuration reads.

The affected stakeholders are workflow authors changing stage configuration,
Action authors relying on the single validated input channel, recovery
handlers, and operators diagnosing retry behavior. No public Workflow DSL or
wire-field shape changes.

## Goals / Non-Goals

**Goals:**

- Preserve raw `with` and task-level `expect` declarations from persistence
  through every workflow dispatch, including checks, Action-created tasks, and
  recovery continuations.
- Make the Runner execution pipeline the only task-input template evaluator.
- Keep the dispatch context fixed per attempt, so an already-dispatched task
  cannot observe later configuration edits while a later retry can.
- Preserve the existing manifest validation order, deferred-input behavior,
  recovery handler matching, failure expansion, task ordering, and budget
  semantics.
- Remove names and comments that describe raw dispatch work as rendered.

**Non-Goals:**

- Change template syntax, variable layering, stage overlays, Action input
  fields, recovery ownership, or public API contracts.
- Expose raw task input, Variables, or dispatch context to Actions.
- Re-render historical TaskRuns whose persisted declarations already contain
  literal values; rerun-from-stage remains their recovery path.
- Add a second renderer, a compatibility `rawWith`/`rawTask` channel, or a
  Runner fetch for newer variables after dispatch.

## Decisions

### D1. Dispatch raw declarations in the existing envelope

`WorkflowItemTranslator` will continue to construct the context payload at
dispatch time, but it will serialize `item.With` and `item.Expect` directly.
For checks, it will serialize the checks wrapper containing each declared
`with` directly. `ExpandToJson`, `BuildVariableBundle`,
`WorkflowProfileManager.ExpandTaskWith`, and `TaskWithExpander` will be
removed from the task-dispatch path; since they have no other production use,
the obsolete helper and its server-only expansion tests will be removed.

The `with`, `expect`, and `variables` fields remain JSON strings on the wire.
The Runner connection layer will keep parsing them into structured values. The
semantic change is that `with` and `expect` now contain declarations while
`variables` contains the snapshot used to execute them.

This keeps the snapshot construction where its data is available and moves
only template evaluation to the component that already has a complete,
recursive renderer. It also makes `tryRecovery`'s existing field copy correct
without adding special retry serialization.

**Alternatives considered:**

- Keep Server expansion and add a raw declaration field to dispatches.
  Rejected because it duplicates data, creates two truth-bearing inputs, and
  invites Actions or recovery code to select the wrong one.
- Keep Server expansion and preserve a raw copy only for `retrySelf`.
  Rejected because it fixes one continuation path while Action-created tasks,
  checks, and future propagation paths still have split rendering semantics.
- Move variable resolution to the Runner. Rejected because it would let an
  attempt read configuration after dispatch and violate snapshot immutability.

### D2. Retain one local render-and-validate pipeline in the Runner

For ordinary tasks, `WorkExecutor.executeOne` will retain this order:

1. derive the local execution context by adding Runner workspace facts to the
   dispatch snapshot;
2. clone the raw `with` declaration, inject manifest-owned engine inputs, and
   identify deferred top-level fields;
3. fail whole-value unresolved references in immediate `with` fields and
   `expect` before an Action is invoked;
4. recursively render non-deferred `with` fields and render `expect`;
5. manifest-validate the rendered Action input, resolve `working-directory`,
   then invoke the Action with only validated input and declared host
   capabilities.

`executeCheckDispatch` will use the same clone, deferred-field, unresolved,
render, and validation sequence for each check. The existing render helpers
already return new structures for rendered paths; cloning before engine-input
injection ensures deferred values passed through to an Action do not share
mutable references with `DispatchWorkItem.with`. `expect` remains Workflow
completion data and is never supplied to an Action.

**Alternatives considered:**

- Render templates in every Action. Rejected because it repeats validation and
  rendering policy, makes deferred behavior inconsistent, and exposes
  context-aware execution concerns to Action implementations.
- Render all fields before considering `render: deferred`. Rejected because
  Actions that generate later tasks would receive baked declarations again.
- Continue using the Server's top-level expander followed by Runner recursive
  expansion. Rejected because the first expansion is precisely the value leak
  across the retry boundary.

### D3. Rename the Runner dispatch representation and retain no compatibility alias

`RenderedWorkItem` will be renamed to `DispatchWorkItem` throughout Runner
code and tests. Its `with` and `expect` fields are raw declarations, while its
`variables` field is the attempt snapshot. `WorkDispatchResponse` remains the
HTTP DTO; `DispatchWorkItem` is the parsed execution representation.

No alias for `RenderedWorkItem` will be retained. A compatibility alias would
leave the incorrect model available to new code and obscure the boundary this
change establishes. The type rename is mechanical but intentionally includes
comments, test fixtures, recovery helpers, check host builders, and workspace
code so no internal API calls raw work rendered.

**Alternatives considered:**

- Keep `RenderedWorkItem` and update only comments. Rejected because the type
  name itself encodes the invalid ownership model.
- Add separate raw and rendered work-item types. Rejected because the rendered
  form is a short-lived local input value, not a durable or cross-module work
  object. A second work type would recreate the dual-input design this change
  removes.

### D4. Recovery continues to copy the dispatch declaration

`tryRecovery` will continue to construct handler tasks before an optional
self-retry and continue to decrement `recoveryRemaining` exactly once on a
matching handler. Handler-task construction continues to expand only
`${{ failure.* }}` against the triggering result; prompt resolution remains
bound to the dispatch snapshot, and other template references remain in the
new task declaration.

For `retrySelf`, the current copies of `work.with` and `work.expect` become
the required behavior because `DispatchWorkItem` now holds raw values. No
rendered Action input, validation result, or completion value may be used as a
retry source.

**Alternatives considered:**

- Reconstruct a self-retry from the Workflow definition. Rejected because a
  retry must preserve runtime-added task declarations, artifacts, `setVars`,
  recovery state, and identity without reinterpreting the graph.
- Store both rendered and raw values on every TaskRun. Rejected because the
  rendered value is attempt-local, increases persistence surface, and risks
  stale data becoming an execution source.

### D5. Lock the boundary with cross-layer regression coverage

Implementation will add focused tests at these boundaries:

- Server translator tests assert static task `with`/`expect` and check `with`
  leave dispatch as literal templates while the variables/prompt payload is
  still resolved at dispatch time. The obsolete server input-expansion tests
  are deleted or replaced with raw-dispatch assertions.
- Runner executor and check tests assert raw declarations render from their
  supplied snapshot, preserve JSON types, reject unresolved immediate
  references, and leave deferred nested templates untouched. They also assert
  the source dispatch object remains unchanged after execution.
- Runner recovery tests assert `retrySelf` persists literal `with` and
  `expect`, while handler tasks replace only `failure.*` references and retain
  other templates.
- A Server/Runner integration regression based on the issue #450 path asserts
  model-a executes the triggering attempt, a variable edit to model-b occurs
  before the self-retry dispatch, and the retry's persisted declaration,
  Action invocation, and Session model use the expected raw/model-b forms.
  Existing budget, manual retry reset, and handler ordering assertions remain.

## Risks / Trade-offs

- [A raw dispatch reaches an older Runner] -> Existing Runner execution already
  recursively renders `work.with` and `work.expect` from the supplied
  variables, so it can consume the new envelope content; deployment remains
  wire-compatible.
- [A deferred Action mutates an input object shared with the dispatch] -> Clone
  the raw declaration before engine-input injection and deferred pass-through,
  keeping recovery's copy independent of Action-owned mutation.
- [Server and Runner rendering behavior diverges] -> Remove Server dispatch
  expansion rather than maintain two implementations; use the Runner template
  module as the only execution evaluator and cover JSON-type, unresolved, and
  deferred cases at the executor boundary.
- [A snapshot is accidentally refreshed after dispatch] -> Keep Effective
  Variable resolution and prompt loading exclusively in translator payload
  construction; Runner execution must consume only the carried snapshot plus
  local workspace facts.
- [A raw template reaches an Action] -> Preserve unresolved-reference checks
  and manifest validation immediately before invocation; deferred fields are
  the sole manifest-declared exception.
- [Existing baked TaskRuns do not adopt live retry behavior] -> Do not migrate
  them; document rerun-from-stage as the supported recovery path.

## Migration Plan

1. Land the Server translator change, helper/test removal, Runner type rename,
   defensive cloning, and regression coverage in one release. No database
   migration, endpoint change, or feature flag is required.
2. Update `design/workflow/task-dispatch.md` as the single authority for raw
   declaration dispatch and Runner expansion. Synchronize
   `design/workflow/actions.md`, `design/runtimes/opencode.md`, and
   `design/workflow/recovery.md`; clarify product documentation only where it
   needs to state the existing observable snapshot behavior without technical
   ownership terms.
3. During rollout, a newer Server can send raw declarations to an older Runner
   because the latter already renders its parsed work item. A newer Runner can
   execute an older Server's pre-expanded dispatch; it simply has fewer
   templates to resolve. Mixed versions therefore remain operational.
4. Existing baked TaskRuns remain unchanged. New static, Action-created, and
   recovery-created tasks preserve templates; new retries use the snapshot
   created for that retry.
5. **Rollback:** revert the Server/Runner release as a unit where possible.
   Raw declarations persisted before rollback remain executable because the
   restored Server expands supported variable references and the restored
   Runner renders the remainder. The only lost behavior is late resolution for
   continuations generated after rollback; no persisted data requires repair.

## Open Questions

None. The design deliberately resolves the previous issue #436 `rawWith`
approach in the opposite direction: one raw dispatch representation and one
local rendered Action input, with no Action-visible escape hatch.
