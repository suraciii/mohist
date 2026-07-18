## Why

Tasks generated at runtime by actions like `mohist/openspec-tasks` (via `addTasks`) bake the resolved workflow variables into their persisted `with` at the moment of subtask generation, instead of keeping the `${{ vars.* }}` placeholder for late resolution. After the user edits project- or stage-level variables (e.g., switches the `build` stage to a working model) and runs `mo issue retry`, the retry still uses the originally-baked value because the placeholder is gone — violating the live-adjustment invariant that statically-declared tasks already honor (`design/workflow/variables.md`: "尚未派发的 task 使用最新变量；retry 是新派发，也使用 retry 时的最新变量"). This blocks the documented recovery flow for failed subtasks and forces a full rerun-from-stage to pick up the change.

## What Changes

- Follow-up tasks created at runtime via `addTasks` carry `${{ vars.* }}` whole-string references through to persistence as literal placeholder strings, instead of the resolved value at subtask-generation time.
- The existing dispatch-time template expander (server `TaskWithExpander.Expand` and runner `renderTemplate`) remains the single point that resolves these references; it is applied at every dispatch, including retry and rerun-from-stage, using the then-current Effective Stage Variables.
- Statically-declared tasks (yaml `tasks[*].with`) and dynamically-expanded tasks (any action-produced follow-up) become indistinguishable in their "change a variable, then retry picks up the new value" behavior.
- Existing baked runs are not migrated; only tasks generated after this change benefit. Rerun-from-stage on a previously-baked run re-expands from the latest variables because a fresh stage re-runs the generating action.
- No change to the layered variable merge, stage overlay, `openspec-tasks` task-merging semantics, `mo issue retry` / rerun-from-stage entry points, or workflow YAML schema. The `applyRequestedModel` warning-swallowing behavior is explicitly out of scope.

## Capabilities

- `runtime-task-late-expansion`: The behavior that follow-up tasks produced at runtime by actions (via `addTasks`, recovery handlers, approval feedback, rebase recovery, and any other runtime task-creation path) carry their `${{ vars.* }}` whole-string references verbatim into the persisted `TaskRun.WithInput`, and that the existing dispatch-time expander resolves them at every dispatch — including retry and rerun-from-stage — using the then-current Effective Stage Variables. Statically-declared and dynamically-expanded tasks are observably indistinguishable from the variable-live-adjustment perspective.

## Impact

- **Runner** (`packages/runner/src/`):
  - `actions/openspec.ts:113` (`mergeTaskWith`) already propagates whatever it receives — verified by `OpenSpecTaskWithOptionsTemplate_LoadsTaskWithTemplatePreservedForLateExpansion` (`packages/runner/tests/openspec-tasks.spec.ts:81-108`). The fix is upstream so the subtask-template sub-tree of the action's `with` arrives un-expanded.
  - `runtime/executor.ts:128` (`renderTemplate(work.with, variables)`) and the surrounding pipeline that build `ActionContext.with` decide what the action sees; whatever mechanism lets the propagation sub-tree arrive un-expanded is in scope.
  - `runtime/recovery.ts` (`tryRecovery`/`addTasks`) already copies the handler-task template verbatim — only affected insofar as the template it copies must already carry placeholders, which it does when the workflow YAML does.
  - `server/connection.ts:271` (`addTasks`) wire shape is unchanged; the change is purely about the content the action puts into the `with` payload.
- **Server** (`packages/server/src/Mohist.Server/`):
  - `Runner/Services/WorkflowItemTranslator.cs:94` (`ExpandToJson(bundle, item.With)`) currently expands the parent task's `with` before sending it to the runner; the mechanism that lets propagation sub-trees reach the action un-expanded touches this boundary.
  - `Workflow/Services/TaskWithExpander.cs` already handles whole-string `${{ vars.* }}` resolution and is reused unchanged for the new placeholder-carrying runtime tasks; coverage of all dispatch paths (fresh, retry via `RetryFailedTask`, rerun-from-stage) is confirmed, not extended.
  - `Workflow/Domain/Run/WorkflowRun.Work.cs`, `TaskRun.cs` (`MakeTask`/`ToDefinition`) persist `WithInput` verbatim — no schema change.
  - `Runner/Services/DispatchService.cs` dispatch path is unchanged.
- **CLI / Web**: no change. `mo issue retry`, rerun-from-stage, and the run timeline see the same task shapes; the user-visible difference is only that retry now picks up the edited variable.
- **Tests**: extend the `openspec-tasks` spec to assert preservation through the real dispatch pipeline (not just an in-process action call where the placeholder is hand-fed); add a server-side dispatch spec asserting a `${{ vars.* }}` reference in a runtime-added task's `with` survives into the persisted `TaskRun.WithInput` and is resolved at the next dispatch using the then-current variables, including across a manual retry that changes the variable.
- **Risk**: medium — the change touches the boundary between server-side dispatch rendering and runner-side action input. The expander's existing "whole-string only" rule keeps embedded references safe; the chief risk is mis-routing un-expanded values into actions that genuinely need expanded input, which would surface as a hard dispatch failure (fail-fast) rather than silent misbehavior.
