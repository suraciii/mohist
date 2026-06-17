# Review Report

## Result: PASS

## Repaired Items

_None required._

## Blocking Items

_None found._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/web/src/widgets/app-shell/ui/Header.tsx:31-34` (`useIsSettingsRoute`)
  Evidence: The settings-route detection uses `segments.includes('settings')`, which matches any path segment containing the literal string `"settings"`. If a future route like `/some-settings-page` were introduced, the header would incorrectly suppress its title and New Issue button. The current routing structure (`/settings/*` and project-prefixed variants) does not trigger this, so it is not a bug today.
  SuggestedAction: Consider matching on `/^\/settings/` or `startsWith` semantics if route diversity grows.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx:260-266` (`handleSave` `changed` computation)
  Evidence: `formToConfig(localValues)` is called inside the loop body for each changed key, computing the full config object N times unnecessarily. This is pre-existing and functionally correct (the `changed[key]` extracted is always the right value); the waste is purely performance.
  SuggestedAction: Hoist `const config = formToConfig(localValues)` above the loop so it runs once.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx:275,295` (empty `catch {}` blocks)
  Evidence: The `handleSave` and `confirmReset` catch blocks are now empty, swallowing errors. The hook's `onError` (in `useSetAgentRuntime`) still emits `toast.error(...)`, so the user-facing feedback is intact. The empty catch prevents unhandled promise rejections. This is intentional per `design.md` Decision 3: rely on hook toasts, remove local state. No error-telemetry/recovery path exists, so no functional regression.
  SuggestedAction: If an error-tracking system (Sentry, etc.) is added later, re-throw or report in these catches.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `openspec/changes/issue-115/specs/web-ui/spec.md:92-105` (`Runtime form unsupportedFields mechanism preserved`)
  Evidence: The spec carries a regression-guard requirement for `unsupportedFields`, but no such mechanism exists in the current codebase (confirmed by repo-wide search and `design.md` Decision 5). T-005 treats this as vacuously satisfied. No protective code was added and no code path can be broken.
  SuggestedAction: Reconcile with the issue author whether `unsupportedFields` is a #19-planned feature or a stale reference. If #19 will introduce it, the spec serves as forward-looking documentation. No change needed for this issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: 13 pre-existing test failures across 5 files
  Evidence: The regression gate (`t-005-regression-gate.md`) reports 866 total tests, 853 pass, 13 fail — all 13 failures reproduced on the pre-change baseline commit `3f01b81e8`. Affected files: `Header.test.tsx` (Epics/Activity/Logs title assertions), `EpicListPage.test.tsx` (epic detail navigation), `canonical-event-types.test.ts`, `useCoderSessions.test.tsx`, `live-task-cloud-event.test.tsx`. None are introduced by this change set.
  SuggestedAction: Address in their own issues. Not blocking for #115.
  Status: pre-existing

- [ID: item-6]
  Severity: info
  Scope: All assertion in `SettingsPage.test.tsx` (`packages/web/tests/`) — "should display opencode model count" updated from `screen.getAllByText('2')[0]` to `screen.getAllByText(/2 models available/i)[0]`
  Evidence: The test previously relied on a lone `"2"` text node matching the Models count in the now-removed 3-column block. The updated assertion targets the new `"2 models available"` hint, correctly tracking T-004. This is a necessary test update, not a regression.
  Status: out-of-scope

- [ID: item-7]
  Severity: info
  Scope: `packages/web/src/entities/settings/api/client.ts:14` (`updateConfig` retains `encodeURIComponent(key)`)
  Evidence: The `updateConfig` function encodes its key parameter, but the fix in `getWorkflowProfile` removes encoding. These are different endpoints: `/config/${key}` expects a single config-key segment that benefits from encoding, while `/workflow-templates/system/{*id}` is a catch-all that must match literal `/`. No inconsistency — each is correct for its own contract.
  SuggestedAction: None.
  Status: out-of-scope

## Acceptance Criteria Status

### Bug 3 (Workflows detail)
- [x] `getWorkflowProfile('mohist/default')` issues GET to `/api/workflow-templates/system/mohist/default` (literal `/`, no `%2F`) — `client.ts:112`, verified by `getWorkflowProfile.test.tsx:80-84`
- [x] Test verifies unencoded path and profile detail payload — `getWorkflowProfile.test.tsx:72-106`
- [x] "All profiles" back button preserved — `WorkflowProfilesSection.tsx:67` (untouched by this change)
- [x] `SettingsPage.test.tsx` green

### Duplicate header
- [x] Settings routes hide `<h1>` title — `Header.tsx:50-52` (`!isSettingsRoute` guard)
- [x] Settings routes hide `New Issue` button — `Header.tsx:64` (`!isMobile && !isSettingsRoute` guard)
- [x] `SidebarTrigger` preserved on all routes — `Header.tsx:45` (no conditional)
- [x] Non-settings routes render title + New Issue unchanged — `Header.test.tsx:80-85`
- [x] Settings route suppression tested — `Header.test.tsx:64-70` (`/settings/ai`), `Header.test.tsx:72-78` (`/audit-test-1/settings/ai`)
- [x] Sidebar unchanged — `AppSidebar.tsx` and `SettingsPage.tsx` untouched

### Feedback unification
- [x] `AgentSettingsSection` no longer has `saveError`/`saveSuccess` state — diff shows removed lines
- [x] Inline success/error mutation banners removed — diff shows deleted JSX (was `AgentSettingsSection.tsx:416-426`, now absent)
- [x] Field-level validation errors remain inline — `AgentSettingsSection.tsx:197` (`text-xs text-red-600`) unchanged
- [x] Toast fires on success via existing hook — `queries.ts:205` (`toast.success('Coder agent runtime updated')`)
- [x] Toast fires on error via existing hook — `queries.ts:208` (`toast.error(err.message || 'Request failed')`)
- [x] All mutation hooks confirmed to have toast calls: `useSetAgentRuntime`, `useSetLogLevel`, `useUpdateOpencodeModel`, `useSetStageModels`, `useAddRepository`, `useRemoveRepository`, `useSetDefaultRepository`, `useSaveProjectTemplate`, `useDeleteProjectTemplateOverride`
- [x] Component tests cover banner removal, toast invocation, inline field errors, reset failure — `AgentSettingsSection.test.tsx:66-144`
- [x] `SettingsPage.test.tsx` green

### Coder Agent cleanup
- [x] Runtime/Command/Models 3-column block removed — diff shows deleted bordered container, grid, and provider note from `AiSettingsSection.tsx`
- [x] "N models available" hint added — `AiSettingsSection.tsx:71` (`{coderModels.length} models available`)
- [x] ModelSelect preserved — `AiSettingsSection.tsx:74-79`
- [x] Stage Model Overrides preserved — `AiSettingsSection.tsx:85-115`
- [x] No unused-variable error (runtime destructure dropped) — `AiSettingsSection.tsx:12`
- [x] Component tests cover block absence, model count hint, ModelSelect presence, Stage Overrides presence — `AiSettingsSection.test.tsx:57-83`
- [x] `SettingsPage.test.tsx` green

### Regression
- [x] `SettingsPage.test.tsx` (`packages/web/tests/`) 18/18 pass
- [x] `SettingsPage.test.tsx` (`packages/web/src/`) `useRepositoriesMock('proj-selected')` assertion passes — line 76 unchanged
- [x] `useRuntimeConsistency` / `useUpdateConfig` untouched (neither exists in source tree; vacuously satisfied)
- [x] `unsupportedFields` mechanism not present in current code, not altered (vacuously satisfied)
- [x] 4 implementation files only: `client.ts`, `Header.tsx`, `AgentSettingsSection.tsx`, `AiSettingsSection.tsx`
- [x] `tsc -b` exits 0, `vite build` succeeds

<promise>PASS</promise>
