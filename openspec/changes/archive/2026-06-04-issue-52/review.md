# Review Report

## Result: PASS

The change delivers the strict acceptance criteria for all 10 issue body points, the four-card state router (reference / custom / loading / error), the `Customize profile` entry point, the DETAILS sidebar de-duplication, the `Active run YAML` re-label, the additive `templateSource` field on the workflow-profile read response, and a Web test suite that covers the four states plus the page-level integration. All 768 web tests pass (including the 18 `IssueWorkflowProfileEditor` and 33 `IssueDetailPage` cases), `tsc --noEmit` is clean, and `vite build` succeeds.

The previously blocking bug (item-4) is fixed: the `isDirty` definition now counts a freshly-typed draft in the customize-from-reference path as dirty, so the user can save the custom YAML they just entered. The fix is verified by two new regression tests in the reference-mode `describe` block that mount the editor with `referenceData()`, click `Customize profile`, fire a `change` on the resulting textarea, and assert that `Save` becomes enabled and that clicking it invokes the update mutation with the typed YAML. The user goal from the issue body ("I should not see ... a disabled save action unless I am actually editing issue-owned workflow YAML") is now satisfied end to end.

The remaining items are code-quality and UX nits. None of them is a correctness, contract, security, or data-safety problem, and none of them blocks acceptance.

## Repaired Items

- [ID: item-4] (was: blocking)
  Severity: blocking (now resolved)
  Scope: `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx:67`
  Evidence: The previous `isDirty = serverYaml !== null && draftYaml !== serverYaml` made `Save` permanently disabled after the customize entry, because `serverYaml` stays `null` until the first successful save and the customize entry never populates it (the data effect at lines 40-58 only runs when `data.yaml` is non-null). The new definition `draftYaml.trim() !== '' && (serverYaml === null || draftYaml !== serverYaml)` treats the customize → type transition as dirty without regressing the existing custom-mode flows: a blank draft stays clean, edits to an existing custom template stay dirty, and reverts to the server template clear dirty. The two new tests at `IssueWorkflowProfileEditor.test.tsx:225-250` exercise the exact previously-broken path and pass.
  Verification: `cd packages/web && npx vitest run tests/IssueWorkflowProfileEditor.test.tsx --reporter=verbose` reports `Tests 18 passed (18)`, including the two new customize-→-type-→-save cases at lines 225-250. The pre-existing custom-mode save/dirty/discard/validation/unsaved-changes tests at lines 81-169 still pass under the new definition. The full Web suite (`npx vitest run`) is `Tests 768 passed (768)`. `tsc --noEmit` exits 0. `vite build` produces a clean bundle.
  Status: resolved

- [ID: item-6] (was: follow-up)
  Severity: follow-up (now resolved)
  Scope: `packages/web/tests/IssueWorkflowProfileEditor.test.tsx:225-250`
  Evidence: The previous reference-mode `describe` block stopped at "Customize profile opens the editor" and never typed into the editor, which is why the customize-→-type-→-save regression slipped through T-005. Two new tests now drive the full transition: one asserts that `Save` becomes enabled after the user types custom YAML, and one asserts that clicking `Save` invokes `useUpdateIssueWorkflowProfileYaml` with `{ issueNumber: 1, yaml: <typed yaml> }`.
  Verification: The two new tests are visible in the verbose reporter output and are included in the 18/18 pass count above.
  Status: resolved

## Blocking Items

- None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx:247` (and call site at line 145)
  Evidence: `CustomEditorCard` declares `serverYaml: string | null` in its prop type and the parent passes the value, but the prop is never read in the rendered JSX or in any callback; the only consumption is the parent-side `isDirty` flag, which is computed before the prop is passed. The signature still compiles because TypeScript does not require destructured-but-unused props to be marked (the component is a local helper, not a forwardRef).
  SuggestedAction: Remove `serverYaml` from the `CustomEditorCard` prop type and from the call site at line 145. The change is local to one file and does not change any rendered behavior.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx:43`
  Evidence: `const nextServerYaml = data.yaml ?? ''` is guarded by the outer `if (data?.yaml !== undefined && data.yaml !== null)` check at line 41, so the `?? ''` fallback is unreachable. The TypeScript compiler accepts both forms and the runtime behavior is identical.
  SuggestedAction: Drop the `?? ''` so the expression is `const nextServerYaml = data.yaml`. Behaviorally a no-op; makes the invariant obvious.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx:42-56`
  Evidence: The data effect calls `setDraftYaml` from inside the `setServerYaml` updater. This is a fragile pattern: it nests a side effect under another setState's updater callback. React 19's automatic batching happens to produce the intended order, but reading the code requires the reader to know that. The effect's "preserve unsaved draft unless draft === previous server" logic is also subtle and worth lifting out of the updater for clarity.
  SuggestedAction: Run the body as straight `useEffect` with explicit dependencies on `data?.yaml` and the existing `serverYaml`/`draftYaml` snapshot (e.g. via refs), so the preservation rule is one `if` rather than an updater callback. This is a refactor, not a behavior change; bundle it with item-1 in a follow-up PR.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx:281` (customize-from-reference path)
  Evidence: The `Textarea` in `CustomEditorCard` is rendered with `placeholder=""` whenever the user lands there from a reference-mode customize click. The user is given no hint about what to type and no example of the workflow YAML shape, so the customize entry is harder to use than the other entry points in this codebase. The previous review flagged this; the new test at line 225-250 confirms the path is functional but the UX hint is still missing.
  SuggestedAction: Add a short placeholder or a one-line helper text (for example `id: my-workflow\nstages:\n  - stage: plan\n    tasks: []\n    checks: []\n`) to the textarea in the customize-from-reference path, or render a small skeleton template the user can edit. Ensure the placeholder is not the `Loading workflow profile...` string that the spec explicitly forbids.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Api/IssueRoutes.cs:602-606`
  Evidence: The `templateSource` computation repeats `state.HasCustomTemplate || template is not null`, but `template` is `DeserializeDefinition(row.TemplateJson)` (see `IssueWorkflowProfileManager.GetStateAsync` at `IssueWorkflowProfileManager.cs:50-65`), and `state.HasCustomTemplate = !string.IsNullOrWhiteSpace(row.TemplateJson)`. The two predicates are therefore equivalent. The behavior is correct; the disjunction is just a code-quality nit. The integration spec at `IssueWorkflowProfileApiSpecs.cs:60-110` covers all three states and would catch a regression.
  SuggestedAction: Simplify to `state.HasCustomTemplate ? "custom" : !string.IsNullOrWhiteSpace(state.SourceTemplateId) ? "project" : "system"`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-8]
  Severity: info
  Scope: `packages/runner/src/server/connection.ts:43`
  Evidence: This commit (`0756c64959 fix(runner): use workflow-runs task batch endpoint`) is in the base of `mo/issue-52` but is not part of the issue-52 worktree. It rewires the runner's `addTasks` call to the `workflow-runs/.../tasks/batch` endpoint. The change is small, internally consistent with the recent workflow-runs API alignment, and was not produced by the issue-52 build, so the review does not assess it.
  SuggestedAction: None for issue 52.
  Status: pre-existing

- [ID: item-9]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Workflow/Infrastructure/IssueWorkflowProfileManager.cs:74-115`
  Evidence: `UpdateTemplateAsync` always creates a row in the database even when both `ProjectTemplateId` and `Template` are null (i.e. the user is reverting from a clean state). The DELETE endpoint at `IssueRoutes.cs:64-72` therefore writes an empty row for issues that were never customised. The behavior is not introduced by this change and is independent of the Web-side UI fix.
  SuggestedAction: Consider treating a null/null request as a no-op when no existing row exists, or add a `DeleteRow` path for the DELETE endpoint so it removes the row instead of upserting an empty one. Out of scope for issue 52.
  Status: pre-existing

## Spec Compliance

| Issue Body AC | Evidence | Status |
|---|---|---|
| 1. Reference mode does not render a textarea | `IssueWorkflowProfileEditor.tsx:127-139` routes reference to `ReferenceSummaryCard`; tests at `IssueWorkflowProfileEditor.test.tsx:252-259` assert `queryByRole('textbox')` returns null. | met |
| 2. Reference mode shows a compact summary with profile id and inherited mode | `ReferenceSummaryCard` renders Profile, Mode, Template, Overrides; tests at lines 184-201. | met |
| 3. `Loading workflow profile...` only while data is loading | `placeholder=""` at `IssueWorkflowProfileEditor.tsx:281`; loading uses `LoadingCard` skeleton; tests at lines 252-259, 343-354, 387-397. | met |
| 4. Save button hidden/unavailable when no custom YAML is being edited | `CustomEditorCard` is only mounted when `!isReference || mode === 'editing'`; tests at lines 252-259. | met |
| 5. Custom profile still supports view/edit with save/error behavior | `CustomEditorCard` retains the original editor + save/dirty/validation logic; tests at lines 81-169 still pass. | met |
| 6. Custom editor labels issue-owned workflow profile YAML | `IssueWorkflowProfileEditor.tsx:271-273` prints "Editing this issue's own workflow profile YAML (not the active run YAML)."; test at lines 274-287. | met |
| 7. Duplicate `Workflow Profile` row removed from DETAILS sidebar | `IssueDetailPage.tsx:730-738` (T-004 commit) removed the row; test at `IssueDetailPage.test.tsx:545-565`. | met |
| 8. Existing Coder Model and per-stage overrides remain in ACTIONS | `IssueDetailPage.tsx:1071-1073` keeps the `IssueModelSelector`; test at `IssueDetailPage.test.tsx:610-630`. | met |
| 9. Web tests cover reference, custom, loading, error states | `IssueWorkflowProfileEditor.test.tsx` lines 184-259 (reference), 262-329 (custom), 331-355 (loading), 357-398 (error). | met |
| 10. No backend schema or workflow runtime behavior change unless a small read-model field is needed | Only `templateSource` was added to the existing read response and Web type; integration spec at `IssueWorkflowProfileApiSpecs.cs:60-110` covers the three values. | met |

<promise>PASS</promise>
