# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test naming
  Evidence: `packages/runner/tests/publish.spec.ts:394` had a misleading test name `PushRejectedAsNonFastForward_GitPorcelainShape_ClassifiesAsBaseMoved` but the mocked push output is the standard `! [rejected] ... (non-fast-forward)` text, not `git push --porcelain` output. Renamed to `PushRejectedAsNonFastForward_StandardGitShape_ClassifiesAsBaseMoved`.
  Verification: `grep -n "PushRejectedAsNonFastForward" packages/runner/tests/publish.spec.ts` — only the renamed occurrence remains. `cd packages/runner && npx vitest run publish.spec.ts` → 14/14 pass.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: design.md — Decision 2 wording
  Evidence: Decision 2 says "publish operates on `project.path` (the main repo, where the base branch is default and the remote is configured)". The implementation also reads `target` from `with.target` first, then falls back to `project.baseBranch` / `project.defaultBranch`; the design doesn't mention the input-override path. Not a behavior issue, just under-spec'd.
  SuggestedAction: Note in design.md Decision 2 that publish's target can be overridden via `with.target`, and that the variable fallbacks are used only when the input is unset.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: design.md — Decision 4 wording
  Evidence: Decision 4 says prepare "rebases onto the fetched base". The implementation also records `preparedBaseSha` after the fetch, but the design doesn't explicitly state that this is the SHA recorded in the task output for downstream freeze/facts. The new web-ui spec scenario "Prepare records reconciliation facts" closes this gap at the spec level, but the design could explicitly note the recording.
  SuggestedAction: Add a one-liner to design.md Decision 4 noting that the fetched base SHA is the value persisted to the task output and that later publish uses it to detect base-moved (via squash conflict or non-FF push).
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: workflow-run spec wording
  Evidence: `openspec/changes/issue-141/specs/workflow-run/spec.md:36-37` still has "ordered tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge`" in the main spec, and the delta at `specs/workflow-run/spec.md` updates this to use `integrate:prepare` and `integrate:publish`. The post-merge state of the main spec will be correct, but the design now has two references (`integrate:merge` is the historical name, `integrate:prepare`/`integrate:publish` are the new names) that future readers may confuse. The spec-sync section in design.md mitigates this.
  SuggestedAction: No change needed. The design's Spec-sync timing section is the authoritative cross-reference.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `commitRebasePendingChanges` wrapper
  Evidence: `packages/runner/src/actions/rebase.ts:178-180` re-exports `commitRebasePendingChanges` as a thin pass-through wrapper around the local `commitPendingChanges` helper. The wrapper is consistent with the design and harmless. The pre-existing review marked it as a follow-up that does not require a change.
  SuggestedAction: Leave as-is.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `failureKind: null` schema
  Evidence: Both `prepareOutput` and `publishOutput` always include `failureKind: null` on success. The downstream renderers guard against null and the closed set is documented in the new comments. No change needed.
  SuggestedAction: Leave as-is.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: info
  Scope: pre-existing test failures — `packages/web/tests/useCoderSessions.test.tsx`, `packages/web/tests/canonical-event-types.test.ts`, `packages/web/src/widgets/app-shell/ui/Header.test.tsx`, `packages/web/src/pages/epics/ui/EpicListPage.test.tsx`, `packages/web/src/pages/issue-details/ui/TaskProgressPanel.test.tsx`
  Evidence: Confirmed pre-existing on commit `2c4889bc` in the prior review; unrelated to this change.
  Status: pre-existing

- [ID: item-8]
  Severity: info
  Scope: pre-existing test failures — `packages/runner/tests/acp-agent.spec.ts` (39 of 41 fail with `TypeError: context.serverConnection.openWorkflowAgentSession is not a function`)
  Evidence: Pre-existing; unrelated to the prepare/publish split.
  Status: pre-existing

- [ID: item-9]
  Severity: info
  Scope: pre-existing test failures — `packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/StageLockSpecs.cs` (grain fixture raises `PendingModelChangesWarning` during EF migration)
  Evidence: Pre-existing; the fixture initializes an in-memory Sqlite + EF Migrate, which trips a warning that the test infrastructure treats as a hard error. Unrelated to this change. A unit-style test against the static `MohistWorkflow.Definition` was added in `MohistDefaultWorkflowProfileSpecs.DefaultWorkflowDefinition_LoadsFromYaml` instead, asserting the same default-yaml ordering contract.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: archive — historical `integrate:merge` references
  Evidence: Many `openspec/changes/archive/**` files reference `integrate:merge`. These are correctly archived historical artifacts.
  Status: pre-existing

- [ID: item-11]
  Severity: info
  Scope: main spec lag (workflow-definition, workflow-run, web-ui)
  Evidence: `openspec/specs/{workflow-definition,workflow-run,web-ui}/spec.md` still hardcode `integrate:merge` / `post-merge-health-failed` because the delta is meant to be applied during Integrate's `integrate:spec-sync` step. Documented by design.md's *Spec-sync timing* section. Once the workflow's spec-sync runs, the main spec is updated.
  Status: out-of-scope (resolved by Integrate pipeline)

## Review Summary

- **Acceptance criteria from T-001 (runner) — fully met:**
  - `prepareAction` and `publishAction` are registered in `createDefaultRegistry()`; `mergeAction` and `mohist/merge` are removed; `merge.spec.ts` is replaced by `prepare.spec.ts` (8 tests) and `publish.spec.ts` (14 tests) — all pass.
  - `prepareAction` records `preparedBaseSha` + `preparedHeadSha` and emits `failureKind: conflict | retry-safe`.
  - `publishAction` lands as one commit, pushes to the remote, and emits `failureKind: base-moved | retry-safe`.
  - Only publish runs `git push`; only prepare runs the agent conflict-resolution loop.
  - Failure cleanup always leaves a clean workspace (`rebase --abort` for prepare; `merge --abort` + `reset --hard` for publish); the stale-rebase-marker cleanup bug from the previous review is fixed and locked down with two new test cases (`CheckoutFailsWithStaleRebaseMerge_AbortsRebaseNotMerge`, `CheckoutFailsWithPendingMerge_AbortsMergeAndRestoresBase`).
  - The non-FF classifier is tightened (matches git's standard `! [rejected] ... (stale info|stale|fetch first|non-fast-forward|behind*)` shape) and tested with two new cases (`...StandardGitShape_ClassifiesAsBaseMoved`, `PushRejectedTransientAuthError_DoesNotMisclassifyAsBaseMoved`).
  - The dirty-working-tree failure now lists the discarded files in the message and explains the destructive `reset --hard` (test: `DirtyWorkingTree_ReportsRetrySafeAndIncludesDiscardedFilesInOutput`).
  - A new end-to-end test (`delivery-shared-ref.spec.ts`) uses a real tmp git repo + linked worktree + bare remote to verify the shared-ref assumption (the rebased `mo/issue-N` is visible from the project repo, publish squash-merges and pushes, and the remote receives the same commit SHA).

- **Acceptance criteria from T-002 (yaml) — fully met:**
  - `mohist-default.workflow.yaml` declares `integrate:prepare` then `integrate:publish` after `integrate:archive-change`, with `mohist/prepare` and `mohist/publish` respectively. `lockBehavior: sequential` and `project-integration` resource are unchanged.
  - `MohistDefaultWorkflowProfileSpecs.DefaultWorkflowDefinition_LoadsFromYaml` (extended) now asserts the full four-task order (`spec-sync` → `archive-change` → `prepare` → `publish`), the absence of `integrate:merge` and `mohist/merge`, and the lock policy. Test passes.

- **Acceptance criteria from T-003 (CLI/Web) — fully met:**
  - CLI `DeliveryFailureGuidance` and web `delivery-failure.ts` map the closed failure-kind set to label + next action. 27 xunit cases + 18 vitest cases cover all three kinds in both surfaces.
  - A new `web-ui` MODIFIED delta (`openspec/changes/issue-141/specs/web-ui/spec.md`) updates `REQ-WUI-005 Integrate progress is visible in Issue Detail` to enumerate the new task order and add scenarios for prepare/publish delivery facts and delivery-failure-kind rendering.

- **Verification (post-repair):**
  - `cd packages/runner && npx vitest run prepare.spec.ts publish.spec.ts delivery-shared-ref.spec.ts` → 23/23 pass.
  - `cd packages/runner && npx tsc -p tsconfig.json --noEmit` → 0 errors.
  - `cd packages/web && npx vitest run tests/delivery-failure.test.tsx` → 18/18 pass.
  - `cd packages/web && npx tsc -p tsconfig.json --noEmit` → 0 errors.
  - `dotnet test ... --filter "FullyQualifiedName~MohistDefaultWorkflowProfileSpecs.DefaultWorkflowDefinition_LoadsFromYaml|FullyQualifiedName~IssueCliTableRendererSpecs"` → 28/28 pass.
  - `dotnet build` server + tests + CLI → 0 errors.

- **Decision:** PASS. All blocking items from the prior review are resolved. The change cleanly splits the opaque `integrate:merge` into `integrate:prepare` (rebase + first-class conflict resolution) and `integrate:publish` (land-as-one-commit + push), with classified recoverable failure kinds and clean-workspace guarantees, well-covered by 41+ tests across the runner, CLI, Web, and server.

<promise>PASS</promise>
