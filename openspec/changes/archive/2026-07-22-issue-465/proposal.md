## Why

Recovery `retrySelf` bakes the already-expanded per-attempt value into the persisted task input instead of preserving the original `${{ vars.* }}` declaration. The Server's `WorkflowItemTranslator.BuildTaskDispatchAsync` expands `with`/`expect` templates (`WorkflowItemTranslator.cs:94`) before dispatch, so the Runner's `RenderedWorkItem.with` already contains the resolved value (e.g. model-a). When recovery fires `retrySelf`, `recovery.ts:76` copies that rendered `work.with` as the next attempt's input, so `${{ vars.agent }}` is gone. After the user switches the stage variable to model-b, retries still execute model-a — only `rerun-from-stage` (which rebuilds the task from the Workflow declaration) recovers. Issue #436 closed the same defect for Action-generated `addTasks`, but the recovery self-retry path still copies rendered values, violating the live-adjustment invariant in `design/workflow/variables.md` and the recovery declaration-preservation contract in `design/workflow/recovery.md`.

## What Changes

- **Server dispatches the raw task declaration, not the expanded value.** `WorkflowItemTranslator` stops calling `ExpandToJson` on `item.With`/`item.Expect`; it persists and sends the original `${{ vars.* }}` placeholders verbatim, alongside the attempt's Effective Stage Variables, prompts, runtime and failure context as an immutable snapshot.
- **Runner becomes the single execution-boundary renderer.** The executor expands the raw `with`/`expect` from the dispatch-carried snapshot immediately before manifest validation and Action invocation, without mutating the dispatch work. Rendering must not leak into the persisted task definition, Action `addTasks`, or retry sources.
- **Recovery `retrySelf` preserves the original dispatch declaration.** Because the dispatch work now carries raw placeholders, `retrySelf`'s copy of `work.with`/`work.expect` naturally retains `${{ vars.agent }}`; the next attempt renders against the then-current snapshot. Handler-task `${{ failure.* }}` expansion stays bound to the triggering attempt.
- **Action input contract is unchanged.** Actions still receive exactly one rendered, manifest-validated input channel; no `rawWith`, `rawTask`, Variables resource, or full dispatch context is exposed. `render: deferred` inputs keep internal templates for Actions that propagate them; non-deferred objects/arrays recurse normally.
- **Recovery matching, failure-reference expansion, budget decrement, follow-up ordering, and manual-retry budget reset behavior are preserved.** Only the source of the self-retry input changes (raw declaration instead of rendered value).
- **No public DSL, variable layering, stage overlay, or product-observable semantics change.** Static tasks, Action-generated tasks, and recovery continuations become observably consistent on "edit variable, then retry uses new value".
- Historical TaskRuns already baked to literal values are not migrated; they continue to require `rerun-from-stage`.

## Capabilities

- `task-input-rendering-boundary`: Server dispatches the raw task `with`/`expect` declaration (placeholders intact) together with an immutable attempt context snapshot (Effective Stage Variables, prompts, runtime context, failure context). The Runner is the single execution boundary that expands templates from that snapshot before manifest validation and Action invocation, without mutating the dispatch work. Covers: raw dispatch wire shape, Runner-side expansion of `with`/`expect`, manifest validation ordering, `render: deferred` propagation, Action single-rendered-input contract, and immutability of an already-dispatched attempt's snapshot under later config edits.
- `recovery-self-retry-declaration`: Recovery `retrySelf` copies the original dispatch declaration (with `${{ vars.* }}` placeholders) and the remaining budget, never the rendered per-attempt values; the triggering attempt having expanded a reference does not bake that value into the next attempt. Covers the model-a → model-b retry scenario and placeholder survival across self-retry, while preserving handler-task `${{ failure.* }}` expansion, matching, budget decrement, and manual-retry budget reset.

## Impact

- **Server** (`packages/server/src/Mohist.Server/`):
  - `Runner/Services/WorkflowItemTranslator.cs:94-95` (`ExpandToJson` on `item.With`/`item.Expect`) — stops expanding; sends raw declaration.
  - `Workflow/Services/TaskWithExpander.cs` / `WorkflowProfileManager.ExpandTaskWith` — Server-side task-input expansion is removed from the dispatch path; the expander may be retired or repurposed for non-dispatch surfaces (e.g. profile preview).
  - `Workflow/Domain/Run/WorkItem.cs`, `TaskRun.cs` — already persist raw `WithInput`; no schema change, but the dispatch envelope now carries it unexpanded.
  - `Runner/Services/DispatchService.cs` — dispatch path unchanged in shape; only the rendered payload content differs.
- **Runner** (`packages/runner/src/`):
  - `runtime/executor.ts:130-148` (`injectEngineInputs`, `renderWithDeferred`, `renderTemplate(work.expect, ...)`) — becomes the authoritative expansion site; must render from raw `work.with`/`work.expect` against the dispatch snapshot without mutating `work`.
  - `runtime/recovery.ts:72-82` (`retrySelf` addTask) — copies raw `work.with`/`work.expect` (now placeholders); no logic change required once dispatch carries raw declarations, but the invariant must be asserted.
  - `runtime/check-execution.ts` — check `with` rendering moves to the same Runner boundary.
  - `server/connection.ts` — `WorkDispatchResponse.with`/`expect` parsing unchanged in shape; content is now raw.
- **Design docs** (single-authority updates per issue):
  - `design/workflow/task-dispatch.md` — becomes the sole authority for template evaluation timing: Server provides raw declaration + snapshot, Runner expands.
  - `design/workflow/actions.md` — sync Runner expansion, manifest validation, `render: deferred` ordering.
  - `design/runtimes/opencode.md` — remove the claim that `with` is expanded before dispatch.
  - `design/workflow/recovery.md` — record the `retrySelf` raw-declaration invariant.
- **Product docs** (`docs/workflow-definition.md`, `docs/workflow-profiles.md`): no semantic change; only clarifications if existing text cannot express the observable behavior, without introducing Server/Runner technical terms.
- **Tests**: regression locked first via the #450 model-a → model-b scenario; spec coverage for raw dispatch wire, Runner expansion, `retrySelf` placeholder survival, deferred input propagation, and dispatched-attempt immutability under config edits.
- **Risk**: high — the change moves the template-evaluation boundary across the Server/Runner split, touching every dispatch and execution path. Mitigated by fail-fast on unresolvable references (already the Runner behavior) and by keeping the Action input contract identical. No breaking public API/DSL change.
