# Review Report

## Result: PASS

## Repaired Items

_None. No safe, local repairs were required._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/entities/settings/api/queries.ts:330-357
  Evidence: `useEffectiveDefaultWorkflowProfile` returns a concrete `effectiveTemplateId` even when `useProjectDefaultWorkflowProfile()` has not resolved (`projectProfile` is `undefined`). When `projectProfile` is `undefined`, `configuredTemplateId` falls to `null` (line 334), and the hook falls through to the system-default branch (line 344) or the `'none'` hardcoded fallback (line 353). If the actual project default is `mohist/github-pr` and the project query is still loading, the hook returns `mohist/default` until the query resolves. `CreateIssueDialog` consumes this provisional value as the submit payload (line 184) — if the user types a title and clicks Create before the project query resolves, the issue is created with the wrong default. In practice the window is narrow (title entry provides a natural delay, and the project query is a simple GET), but the hook cannot distinguish "unset" from "not yet loaded".
  SuggestedAction: Add `isLoading` and `isError` fields to `EffectiveDefaultWorkflowProfile`, exposing `useProjectDefaultWorkflowProfile`'s loading/error state. In `CreateIssueDialog`, either defer the default resolution until both queries are settled, or disable the Create button until the project default query is resolved (when a project is active). In `WorkflowProfileControl`, the select value and `data-default-profile` attribute should similarly wait for resolution rather than publishing a provisional value.
  Status: follow-up

- [ID: item-2]
  Severity: test-gap
  Scope: packages/web/src/entities/settings/api/queries.test.ts:218-272, packages/web/src/features/create-issue/ui/CreateIssueDialog.test.tsx:48-52, packages/web/src/widgets/issue-workflow/ui/WorkflowProfileControl.test.tsx:10-14
  Evidence: All effective-default tests drive the hook with already-resolved data. `queries.test.ts` passes concrete `projectDefault` values to `mockQueryData` (line 219-236) and never models a loading/absent query state. The create-issue and workflow-control tests mock `useEffectiveDefaultWorkflowProfile` as a final resolved value (lines 48-52, 10-14) rather than exercising the real hook's loading transition. This leaves item-1 invisible to the test suite.
  SuggestedAction: Add tests that model the project-workflow-profile query as loading (data `undefined`, `isLoading: true`) while the system catalog is resolved, then later resolving to a project-configured default. Assert the effective default transitions correctly and that `CreateIssueDialog` does not submit the intermediate fallback.
  Status: follow-up

- [ID: item-3]
  Severity: cleanup
  Scope: packages/web/src/pages/settings/ui/WorkflowProfilesSection.tsx:15-18
  Evidence: `WORKFLOW_DESCRIPTORS` remains an empty array (line 18). Settings → Workflows now has a configurable `Default workflow` select via `ProjectDefaultWorkflowControl`, but the settings search registry has no descriptor pointing to `project-default-workflow-select`. This means searching settings for "workflow" or "default" won't surface the new project default control.
  SuggestedAction: Add a `SettingsSearchEntry` to `WORKFLOW_DESCRIPTORS` that focuses `project-default-workflow-select` with relevant keywords (`workflow`, `default`, `project`, `template`). Update `packages/web/tests/settings-search-registry.test.tsx` line 297 accordingly.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: info
  Scope: packages/web/src/widgets/kanban-board/ui/IssueCard.tsx:32,245
  Evidence: `IssueCard` falls back to a hardcoded `SYSTEM_DEFAULT_WORKFLOW_PROFILE_ID = 'mohist/default'` (line 32) when an issue has no `workflowProfileId` (line 245). This does not use `useEffectiveDefaultWorkflowProfile` and will show `mohist/default` for issues missing a resolved profile, even when the project default has been changed. This file was not modified by this change (0 lines changed in the diff).
  SuggestedAction: Consider wiring `IssueCard` to the effective-default hook or to the backend-resolved `issue.workflowProfileId` in a future change. Not in scope for issue-269.
  Status: pre-existing

- [ID: item-5]
  Severity: info
  Scope: openspec/specs/web-ui/spec.md:55, openspec/specs/http-api/spec.md (multiple lines), openspec/specs/cli-interface/spec.md (multiple lines)
  Evidence: Multiple committed spec files reference `"mohist/pr"` as a workflow profile ID, but the actual system catalog uses `mohist/github-pr`. These are pre-existing specs from archived changes (issue-257) and the canonical spec files, not from the current change. The current implementation correctly uses `mohist/github-pr` everywhere. The issue body itself also mentions `mohist/pr` but the proposal/specs/tasks for issue-269 consistently use `mohist/github-pr`.
  SuggestedAction: Consider a follow-up sweep to align historical specs with the actual product catalog naming. Not blocking for issue-269.
  Status: pre-existing

## Acceptance Criteria Verification

| Criterion | Status | Evidence |
|---|---|---|
| Settings → Workflows displays current project default | ✅ PASS | `ProjectDefaultWorkflowControl.tsx:55-60` reads from `useProjectDefaultWorkflowProfile`, displays `configuredTemplateId` or inherit message |
| Selecting `mohist/github-pr` writes PUT, readback confirms | ✅ PASS | `ProjectDefaultWorkflowControl.tsx:100` calls `setDefault.mutate({ templateId: 'mohist/github-pr' })`, test at `ProjectDefaultWorkflowControl.test.tsx:116-134` verifies PUT body and readback |
| Clearing sends DELETE, UI explains inheriting system default | ✅ PASS | `ProjectDefaultWorkflowControl.tsx:102,120` calls `clearDefault.mutate()`, lines 63-72 show inherit message, test at lines 136-156 verifies DELETE and copy |
| System default badge visually separated from project default | ✅ PASS | `WorkflowProfilesSection.tsx:89-91,141-143` uses "System default" label with `bg-slate-50 text-slate-700`, test at `ProjectDefaultWorkflowControl.test.tsx:191-210` verifies distinct styling |
| Create-issue/profile selection honors project default | ✅ PASS | `CreateIssueDialog.tsx:179-184` uses `effectiveTemplateId` for select value and submit payload; `WorkflowProfileControl.tsx:25-30` uses `defaultProfileId` for effective resolution; tests at `CreateIssueDialog.test.tsx:481-502` and `WorkflowProfileControl.test.tsx:192-234` verify project-configured and fallback cases |
| Web tests cover all four flows | ✅ PASS | 2351 tests pass; `ProjectDefaultWorkflowControl.test.tsx` covers readback, PUT, DELETE, orphan warning, badge distinction; `CreateIssueDialog.test.tsx` covers project default submission; `WorkflowProfileControl.test.tsx` covers project default fallback and catalog absence |

## Post-Repair Verification

- `npm run typecheck -w packages/web` — **passed** (0 errors)
- `npm run test:run -w packages/web` — **passed** (162 files, 2351 tests, 1 skipped)

<promise>PASS</promise>
