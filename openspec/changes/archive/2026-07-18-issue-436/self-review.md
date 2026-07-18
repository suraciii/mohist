# Self-Review — issue-436 (runtime-task-late-expansion)

Reviewer hat: this pass checks the plan artifacts (`proposal.md`, `specs/runtime-task-late-expansion/spec.md`, `design.md`, `tasks.json`) against the issue body and the actual codebase. Every cited line number, every architectural claim, and every test-feasibility assertion was traced to source.

## Verified correct

**Bake diagnosis is accurate.** Traced end-to-end:
- Server `TaskWithExpander.Expand` (`packages/server/src/Mohist.Server/Workflow/Services/TaskWithExpander.cs:13-44`) walks **top-level keys only**; `TryResolveWholeTemplate` returns false for object values (line 49: `value.ValueKind != JsonValueKind.String`), so nested `task.with.options` survives server-side.
- Runner `WorkExecutor.executeOne` calls `renderTemplate(work.with, variables)` at `packages/runner/src/runtime/executor.ts:128`. `renderValue`/`renderObject` (`packages/runner/src/core/template.ts:36-48`) **do** descend into nested objects, replacing whole-string placeholders with resolved values.
- `openspecTasksAction` reads `taskDefaults = objectInput(context.with, "task")` at `packages/runner/src/actions/openspec.ts:104` and propagates via `mergeTaskWith` at line 113. By the time the action sees it, the placeholder is gone.

**Proposed fix is sound and minimal.** `ActionContext.rawWith = work.with` (the server-expanded form) gives the action access to the placeholder. The action switches its `task` read from `with` to `rawWith ?? with`. `path`/`items` stay on the rendered `with`. No other action needs to change.

**Server-side no-change claim holds.** `RetryFailedTask` (`packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Stage.cs:144-154`) calls `TaskRun.MakeTask(stage.Tasks, failedTask.ToDefinition())`; `ToDefinition()` returns `task.WithInput` verbatim (`TaskRun.cs:217-225`), and the next dispatch re-runs `ExpandToJson(bundle, item.With)` on whatever is persisted. Once the runner stops baking, every dispatch path resolves the placeholder.

**Other runtime paths already conform (verified, not just claimed).**
- Recovery: `packages/runner/src/runtime/recovery.ts` does NOT import or call `renderTemplate` — `work.recovery` is never rendered. `readAddTasks` (line 92) copies handler tasks verbatim. Confirmed.
- Approval feedback: `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Approval.cs:74` constructs `["options"] = JSON.SerializeToElement("${{ vars.agent }}")` — literal placeholder. Confirmed.
- Rebase recovery: `packages/server/src/Mohist.Server/Api/IssueRoutes.Helpers.cs:152` — same literal. Confirmed.

**All cited line numbers accurate.** Programmatic check of 8 cross-referenced lines (WorkflowItemTranslator.cs:94, TaskRun.cs:202, openspec.ts:104/113/408/433, executor.ts:128-129, template.ts:36, connection.ts:271, model-resolution.ts:25, WorkflowRun.Stage.cs:144-154, WorkflowRun.Approval.cs:74) — all match.

**Test-feasibility claims hold.**
- T-001 executor-level test: `packages/runner/tests/executor-recovery.spec.ts:27-90` already uses `new WorkExecutor(...)` + `executor.execute(work(...))` with fake actions/connection — the pattern T-001 needs.
- T-002 server spec: `packages/server/tests/Mohist.Server.SpecTests/Specs/Workflow/Grain/DispatchAndLoadingSpecs.cs:198-231` (`StageWithDynamicAgentVariables_LoadedDynamicTasksInheritStageAgent`) already calls `AddTasksAsync` with a `${{ vars.agent }}` placeholder and asserts dispatch resolves it via the stage agent. `StageAgentVariableUpdate_DispatchedTaskInheritsLatestIssueAgentModel` (line 342) already patches issue variables and asserts the next dispatch picks up the new model. The retry-specific assertion T-002 adds is new, but the setup helpers exist.

**Tasks graph is a valid DAG.** T-001 priority 1 no deps; T-002 priority 2 depends on T-001 only. JSON parses, every required field populated, every task has acceptance criteria.

**Spec↔tasks↔design alignment.** Proposal names exactly one capability (`runtime-task-late-expansion`); spec file lives at the matching path; tasks reference real spec anchors (`#Runtime-generated task inputs…`, `#Dispatch-time resolution…` — both exist as `### Requirement:` headings). All four spec requirements are addressed by at least one task or by an explicit "already conforms" determination in the design.

## Observations (non-blocking, advisory for the implementer)

**O1. Decoupled test coverage, no single end-to-end test.** T-002's server spec exercises the server-side half (runtime-added task with placeholder → dispatch resolves → variable change → retry re-resolves) by calling `AddTasksAsync` directly, bypassing the runner. The runner-side half (openspec-tasks now writes placeholders via `addTasks`) is covered only by the runner unit test. No single test proves runner-produces-placeholder → server-dispatches → retry-re-resolves. This matches the project testing style (`design/testing.md`: no real network/DB, unit + spec tracks), so it is acceptable — but the implementer should be aware that the two halves are proven independently.

**O2. Spec scenarios for recovery/approval/rebase preservation have no new tests in the plan.** The spec lists "Recovery handler tasks preserve placeholders declared in YAML" and "Approval feedback and rebase recovery tasks preserve placeholders" as required scenarios. The design argues (and I verified) these paths already conform structurally — `recovery.ts` does not render `work.recovery`; approval/rebase construct literal placeholder strings. No code in this change touches those paths, so regression risk is near-zero. The plan is delivering the delta (openspec-tasks fix), not re-testing every unchanged path. Acceptable, but worth noting that these spec scenarios ride on existing structure rather than active test coverage.

**O3. T-002 acceptance criterion 4 is weakly verifiable as written.** It says: *"Pre-existing baked `TaskRun.WithInput` rows … verified by the legacy-baked-task scenario in the spec."* The spec scenario "Legacy baked task on retry still uses baked value" describes the behavior but T-002 does not add a test for it. The criterion is true-by-construction (T-002 adds no migration code, so nothing could rewrite baked rows), but the wording implies a test that doesn't exist. Suggested rewording for the implementer: *"No migration code is introduced; code review confirms no path rewrites pre-existing baked `TaskRun.WithInput` rows."* Alternatively, add a one-line regression test that dispatches a task with a baked literal `WithInput` and asserts retry reuses it.

**O4. T-001 is infrastructure-only (no consumer until T-002).** After T-001, `rawWith` exists and is populated but no action reads it. The "feature module usable after each task" principle is borderline. Precedent: issue-435 T-001 (equality helper) → T-002 (timer wiring) used the same split and was accepted. Not a blocker.

**O5. Design Open Question 1 (`rawWith ?? with` fallback removal) is deferred.** Acceptable as an open question. The fallback keeps the ~20 hand-built `ActionContext` test constructions green; removing it is a follow-up.

## Severity assessment

No blocking issues. The core fix is technically correct, minimally invasive, and reuses existing dispatch infrastructure. Every claim that could be verified against source was verified. The observations above are coverage/wording refinements the implementer can address inline or defer — none of them must be fixed before building.

<promise>PASS</promise>
