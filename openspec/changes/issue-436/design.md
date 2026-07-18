## Context

The proposal (`openspec/changes/issue-436/proposal.md`) and spec (`openspec/changes/issue-436/specs/runtime-task-late-expansion/spec.md`) establish the target behavior: tasks produced at runtime via `addTasks` carry their `${{ vars.* }}` whole-string references verbatim into `TaskRun.WithInput`, and the existing dispatch-time expander resolves them at every dispatch using then-current Effective Stage Variables.

The bake happens in exactly one place today. Tracing the flow:

1. **Server** `WorkflowItemTranslator.BuildTaskDispatchAsync` calls `ExpandToJson(bundle, item.With)` (`packages/server/src/Mohist.Server/Runner/Services/WorkflowItemTranslator.cs:94`). `TaskWithExpander.Expand` walks **top-level keys only** (`packages/server/src/Mohist.Server/Workflow/Services/TaskWithExpander.cs:13-44`): a top-level value that is a whole-string `${{ vars.* }}` is replaced; nested objects pass through unchanged. So for the parent `mohist/openspec-tasks` task, `with.task.with.options` arrives at the runner still carrying the `${{ vars.agent }}` placeholder.
2. **Runner** `WorkExecutor.executeOne` then calls `renderTemplate(work.with, variables)` (`packages/runner/src/runtime/executor.ts:128`). `renderTemplate` is **recursive** (`packages/runner/src/core/template.ts:36-48`): it descends into nested objects and replaces the placeholder with the resolved agent object. The fully-rendered object is handed to the action as `ActionContext.with`.
3. **Action** `openspecTasksAction` reads `taskDefaults = objectInput(context.with, "task")` (`packages/runner/src/actions/openspec.ts:104`) — by this point `taskDefaults.with.options` is the resolved agent object, not the placeholder. `mergeTaskWith` propagates that resolved object into each generated subtask's `with` and ships it back to the server via `addTasks`.
4. **Server** persists the baked object as `TaskRun.WithInput` (`packages/server/src/Mohist.Server/Workflow/Domain/Run/TaskRun.cs:202`). At subsequent dispatches, `TaskWithExpander.Expand` sees `options` is an object, not a whole-string placeholder, and leaves it alone. Retry / rerun therefore reuse the baked value forever.

The action itself is correct: the existing test `OpenSpecTaskWithOptionsTemplate_LoadsTaskWithTemplatePreservedForLateExpansion` (`packages/runner/tests/openspec-tasks.spec.ts:81-108`) proves that `mergeTaskWith` faithfully propagates whatever it receives. The bug is that the executor feeds it the pre-resolved form.

Other runtime task-creation paths already conform:
- **Recovery handlers** (`packages/runner/src/runtime/recovery.ts:36, 92-119`): copy `work.recovery.handlers[*].tasks[*].with` verbatim. `work.recovery` is never run through `renderTemplate` (only `work.with` and `work.expect` are — `executor.ts:128-129`), so YAML-declared placeholders survive into `addTasks`.
- **Approval feedback** (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Approval.cs:74`): constructs the default task with the literal `"${{ vars.agent }}"` string.
- **Rebase recovery** (`packages/server/src/Mohist.Server/Api/IssueRoutes.Helpers.cs:152`): same — literal placeholder string.

So the fix surface is the runner executor's handoff to the `openspec-tasks` action.

## Goals / Non-Goals

**Goals:**

- Make `openspec-tasks` subtask inputs (and any future action that propagates a sub-tree of its `with` to follow-up tasks) carry `${{ vars.* }}` placeholders through to persistence, so dispatch / retry / rerun-from-stage re-resolve them against then-current variables.
- Reuse the existing dispatch-time expander (`TaskWithExpander.Expand` on the server, `renderTemplate` on the runner) without changing its rules.
- Keep the fix surgical: no schema migration, no workflow YAML schema change, no public API change.

**Non-Goals** (per proposal):

- Migrate pre-existing baked `TaskRun.WithInput` data.
- Refactor `openspec-tasks` task-merging / default-injection logic.
- Fix `applyRequestedModel` warning-swallowing (`packages/runner/src/actions/acp/model-resolution.ts:25`); tracked separately.
- Introduce a frozen-task-snapshot mechanism.
- Change the layered variable merge or stage overlay semantics (already verified correct).

## Decisions

### D1. Expose the server-expanded `with` to actions as `ActionContext.rawWith`

Add an optional `rawWith?: JsonObject | null` field to `ActionContext` (`packages/runner/src/core/types.ts:209`). The executor populates it with `work.with` — the form received from the server, i.e. top-level-expanded but nested-preserved. The existing `with` field stays as today: the result of `renderTemplate(work.with, variables)`, recursive.

`openspec-tasks` switches from reading `task` out of `context.with` to reading it out of `context.rawWith` (`packages/runner/src/actions/openspec.ts:104`). Everything under `rawWith.task` — `uses`, `with.*`, any future propagated field — is then forwarded into subtask `with` verbatim, placeholders included. `path`, `items`, and other action-own scalars continue to come from the rendered `with`.

**Rationale.** The bug is structural: the executor collapses two distinct forms (server-top-level-expanded vs. runner-recursively-rendered) into one. Once collapsed, the action cannot recover the original placeholder text. `rawWith` restores the duality without changing any action's contract for the fields it actually consumes. It also generalizes: any future action that propagates a sub-tree to follow-up tasks reads from `rawWith` for that sub-tree and from `with` for its own scalars.

**Alternatives considered.**

- *A1: Make `renderTemplate` top-level-only (align with `TaskWithExpander.Expand`).* Rejected. Real workflows use embedded references in top-level scalars (`path: "${{ tasks.proposal.outputs.openspecName }}/specs"` — `packages/runner/tests/template.spec.ts:263-270`) and references inside nested objects (`expect.markers[*].path`, `artifacts.files[*].path`). Top-level-only rendering would regress both. The asymmetry between server (cheap, top-level) and runner (full, recursive) is intentional: the server pays for one expand per dispatch, the runner finishes the job at execution time.
- *A2: Render-on-demand inside actions; drop the executor's pre-render.* Rejected. Every existing action reads pre-rendered scalars from `context.with` (27 `stringInput(context.with, …)` call sites across `packages/runner/src/actions/`). Moving rendering into each action is invasive, repeats the same walk per action, and risks inconsistency. The single pre-render stays; `rawWith` is an opt-in escape hatch for the narrow propagation case.
- *A3: Action-specific metadata declaring "this sub-tree of `with` is a pass-through template; skip rendering there."* Rejected. Pushes action-specific knowledge into the executor, requires a new action-metadata schema, and still needs the runner to know each action's pass-through paths. `rawWith` puts the choice in the action's own code where the propagation happens.
- *A4: YAML-level marker (e.g. `!template` tag) for opaque sub-trees; both server and runner honor it.* Rejected as over-engineered for a single known consumer. If a second consumer appears with a different shape, reconsider a YAML marker; until then `rawWith` is enough.
- *A5: Move `task` out of `with` into a sibling action field.* Rejected. Workflow YAML schema change; breaks every existing `mohist/openspec-tasks` declaration.

### D2. No server-side change

`TaskWithExpander.Expand` already top-level-expands whole-string `${{ vars.* }}` references for every dispatch — fresh, retry (`RetryFailedTask` → `TaskRun.MakeTask(stage.Tasks, failedTask.ToDefinition())`, `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:144-154`), and rerun-from-stage. The translator already invokes it on `item.With` for the parent task (`WorkflowItemTranslator.cs:94`) and will invoke it on each subtask's `WithInput` when those subtasks dispatch. Once the runner stops baking the value into `addTasks`, the existing server pipeline resolves placeholders correctly at every subsequent dispatch.

`TaskRun.WithInput` persistence is already verbatim (`TaskRun.cs:202`); no schema change is required.

### D3. `rawWith` is optional on `ActionContext` and defaults absent on hand-built test contexts

Production code path (executor) always sets it. Existing tests that construct `ActionContext` by hand and don't care about propagation can leave it `undefined`. `openspec-tasks` reads `context.rawWith ?? context.with` for the `task` sub-tree, so tests that previously fed `task` through `with` (because there was no `rawWith`) keep working unchanged; the existing placeholder-preservation test (`openspec-tasks.spec.ts:81-108`) is upgraded to also assert via `rawWith`, but its assertions don't weaken.

**Rationale.** Backward compatibility for the ~20 hand-built `ActionContext` constructions in `packages/runner/tests/`. The fallback is removed in a follow-up once all test contexts are migrated; for this change it keeps the diff bounded.

### D4. Test strategy: three layers

1. **Runner unit (executor)** — extend an executor-level spec to assert `ActionContext.rawWith` is the server-expanded form and `ActionContext.with` is the recursively-rendered form. Ensures the duality is wired correctly without depending on `openspec-tasks`.
2. **Runner unit (openspec-tasks)** — add a scenario that drives the action through a context populated as the executor would (placeholder in `rawWith.task.with.options`, resolved object in `with.task.with.options`) and asserts the generated subtask's `with.options` equals the literal placeholder string. Augment the existing hand-fed test with the executor-style setup.
3. **Server spec (dispatch + retry)** — add a `DispatchAndLoadingSpecs`-style spec (`packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Grain/DispatchAndLoadingSpecs.cs:220-354` is the precedent) that:
   - Records a runtime-added task whose `WithInput["options"]` is the literal `${{ vars.agent }}` string.
   - Asserts that the first dispatch resolves it against `vars.agent = { model: "model-a" }`.
   - Mutates Project / Issue Variables so `vars.agent.model = "model-b"`.
   - Triggers `mo issue retry` and asserts the new dispatch carries `model-b`.
   This is the end-to-end proof of the spec's "Retry after variable change uses the new value" scenario.

The runner→server report path (`connection.ts:271 addTasks`) is wire-compatible; its existing tests (`server-connection-report.spec.ts`) don't change.

## Risks / Trade-offs

- **Two sources of truth for `with`.** An action that reads a propagated sub-tree from `with` (rendered) instead of `rawWith` will silently bake. -> Mitigation: one consumer today (`openspec-tasks`); code review + the new executor-level test (D4.1) make the contract explicit. The fallback `rawWith ?? with` (D3) keeps tests green but production always sets `rawWith`, so a missing `rawWith` in production is impossible by construction (executor is the only constructor).
- **Future actions repeating the mistake.** A new action that propagates parts of its `with` to `addTasks` may read from the wrong form. -> Mitigation: the design doc + a code comment at `ActionContext.rawWith` declaration naming the rule. Long-term: if a second consumer appears, encode the rule in an action-author checklist or lint.
- **Subtask `expect` parity.** The spec covers `expect` preservation generally. `openspec-tasks` reads subtask `expect` only from per-task JSON (`openspec.ts:433 mergeTaskExpect`), not from `context.with.task`, so no change is required today. If a future default-`expect`-for-subtasks feature reads from `context.with.task`, it must read from `rawWith.task` for the same reason.
- **Risk of regression in `openspec-tasks` merging.** `mergeTaskWith` (`openspec.ts:408-424`) also injects the prompt loader spec and merges per-task overrides. Switching the source of `defaultWith` from `with.task` to `rawWith.task` changes which form the merge starts from. -> Mitigation: existing `openspec-tasks` tests cover the merge shape; the new scenario (D4.2) adds the placeholder case explicitly.
- **No migration of baked runs.** A user observing the old behavior on a pre-fix run may file a follow-up. -> Mitigation: rerun-from-stage on a baked run produces placeholder-carrying tasks under the new rules (the generating action re-executes). Document this in the issue closure notes.

## Migration Plan

Single-sided deploy, no coordination:

1. Merge the runner change. New runtime-generated tasks (produced by `openspec-tasks` after the runner is updated) carry `${{ vars.agent }}` placeholders in `TaskRun.WithInput`. Their next dispatch — including retry and rerun-from-stage — resolves against then-current variables.
2. Server is unchanged; no schema migration, no DB column, no event format change. Pre-existing baked `TaskRun.WithInput` rows are untouched and continue to resolve to their baked literal.
3. **Rollback.** Revert the runner change. Tasks already carrying placeholders continue to work — `TaskWithExpander.Expand` resolves them at dispatch as before. New tasks created during the rolled-back window would bake again (regression returns). No data corruption either direction.

No feature flag. The change is symmetric: runners with the fix produce placeholder-carrying tasks; runners without it produce baked tasks. The server treats both correctly; only the live-adjustment-on-retry behavior differs.

## Open Questions

- Should the fallback `rawWith ?? with` (D3) be removed in this change, or left for a follow-up? Lean: leave for follow-up to keep the diff bounded and existing tests green; remove once the test context helper (`openspec-tasks.spec.ts:622 context()`) explicitly populates `rawWith`.
- Is the `openspec-tasks` action the only consumer that will ever need `rawWith`? If a second consumer appears with a meaningfully different shape (e.g., an action that propagates an `expect` template), the design holds — but it's worth re-evaluating a YAML-level `!template` marker at that point to push the opt-in up to authors.
