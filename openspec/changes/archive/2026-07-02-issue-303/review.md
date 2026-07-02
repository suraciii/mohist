# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting — five new source files missing trailing newlines
  Evidence: `NoProjectCard.tsx`, `SectionState.tsx`, `SettingsDirtyContext.tsx`, `SettingsDirtyContext.test.tsx`, and `SectionState.test.tsx` all ended without a final `\n` (last byte was `}` or `)`). Added trailing newline to each via `echo "" >>`.
  Verification: `tail -c 1` now reports `0a` for all five files. `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` remain green (235 files, 3615 tests).
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/features/settings-search/SettingsSearch.tsx:168-181` — Settings search navigates without dirty check
  Evidence: `SettingsSearch.handleSelect` calls `navigate(targetPath)` without consulting `useSettingsDirty`. A dirty Agent form can be silently discarded by opening the settings search dialog (Cmd+K), selecting a result in another settings tab, and navigating away. The spec and acceptance criteria explicitly scope the dirty guard to sub-navigation initiated switches, so this is not a spec violation — but it is a UX gap the user can encounter.
  SuggestedAction: Import `useSettingsDirty` and check `dirty` inside `handleSelect`; if the form is dirty and the target is a different tab, render the discard-confirmation `AlertDialog` before proceeding.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/app-shell/ui/AppSidebar.tsx:106` — hardcoded event name string
  Evidence: `AppSidebar` listens for `'mohist:sidebar:open-project-switcher'` with a hardcoded string literal, while `NoProjectCard.tsx` exports `REVEAL_PROJECT_SWITCHER_EVENT` with the same value. The listener silently breaks if the constant changes.
  SuggestedAction: Import `REVEAL_PROJECT_SWITCHER_EVENT` in `AppSidebar`; also import it in `SettingsPage.test.tsx` and `TemplatesSection.test.tsx` instead of hardcoding the string.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/tests/a11y/settings.a11y.spec.ts` — browser axe gaps
  Evidence: The structural Vitest a11y matrix (`tests/a11y/settings-a11y.test.tsx`) was extended to cover `label-catalog` and `inbox` (21 tests pass), but the browser Playwright axe spec (`tests/a11y/settings.a11y.spec.ts`) still audits only `ai`, `agent`, `repositories`, `workflows`, `templates`, `system`, and `preferences`. The new Label catalog search and empty-list states lack browser axe coverage.
  SuggestedAction: Extend the Playwright axe tab list to include `label-catalog` and `inbox` with matching API mocks once the pre-existing Playwright axe failures are stabilized.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/pages/settings/ui/ProjectDefaultWorkflowControl.test.tsx:148` — describe block name misalignment
  Evidence: The `describe('ProjectDefaultWorkflowControl')` block now renders `WorkflowProfilesSection` (via `renderWithoutProject`), and the test name references "when the WorkflowProfilesSection is mounted". This is a pre-existing naming quirk made more apparent by the T-006 additions.
  SuggestedAction: Rename the describe block to `describe('WorkflowProfilesSection no-project state')` or extract the no-project test to a dedicated describe on `WorkflowProfilesSection`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: `packages/web/tests/a11y/settings.a11y.spec.ts` — pre-existing axe failures
  Evidence: The Playwright browser axe run has pre-existing color-contrast violations on Settings sub-nav group headings (`text-primary/80`, contrast 3.76) and Preferences theme copy, plus stale workflow API mocks that time out. These are baseline issues, not introduced by this change.
  SuggestedAction: Track separately and fix the browser a11y baseline, or quarantine known failures before requiring the full `npm run test:a11y` command as a green gate.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: destructive confirmation outside issue scope
  Evidence: `AppSidebar.tsx:216` project deletion still uses a hand-written `fixed inset-0` overlay; `ReviewReportModal`, `MarkdownReader`, and other bespoke overlays remain outside the AlertDialog migration. The design Non-Goals explicitly scoped these out.
  SuggestedAction: Migrate project deletion and remaining bespoke overlays to `AlertDialog` in a follow-up for full app consistency.
  Status: out-of-scope

---

## Acceptance Criteria Verification

| # | Acceptance Criterion | Evidence | Status |
|---|---|---|---|
| 1 | Shared `AlertDialog` primitive (focus trap, focus restore, Escape) | `alert-dialog.tsx:1-91` composes base-ui `dialog.tsx` (base-ui owns trap/restore/Escape). Unit tests at `alert-dialog.test.tsx:112-179` verify focus restore after cancel, confirm, and Escape. | PASS |
| 2 | Agent reset / label delete / repo remove / template delete / IssueDetail comment delete via AlertDialog; no `window.confirm` or hand-written modal remains | `AgentSettingsSection.tsx:488-502`, `LabelCatalogSection.tsx:568-585`, `RepositoriesSection.tsx:258-275`, `TemplatesSection.tsx:387-416`, `IssueDetailPage.tsx:787-807`. No `window.confirm`/`window.alert` in `packages/web/src`. No hand-written `fixed inset-0` confirm overlay in `packages/web/src/pages/settings`. | PASS |
| 3 | Agent save/reset failures surface inline; critical not toast-only | `AgentSettingsSection.tsx:341-343` sets `saveError` in `handleSave` catch; `:390-392` sets it in `confirmReset` catch. Renders as `role="alert"` with `aria-live="polite"` at `:477-486`. No additional toast calls added beyond existing `useSetAgentRuntime` toast. | PASS |
| 4 | Field errors via `aria-describedby` + `aria-invalid` | `AgentSettingsSection.tsx:228-229` wires `aria-invalid`/`aria-describedby` pointing at `FieldError` id. `LabelCatalogSection.tsx:163-164,178-179,466-467,479-480,493-494` for add/edit fields. Uses shared `FieldError` (`field-error.tsx:1-32` with `role="alert"`). | PASS |
| 5 | Dirty form warns before tab switch (sub-nav only) | `SettingsSubNav.tsx:123-133` intercepts sub-nav `<Link>` onClick; `AlertDialog` at `:230-240`. `SettingsDirtyContext.tsx:1-43`. `AgentSettingsSection.tsx:282-287` writes dirty + cleanup. Tests at `SettingsPage.test.tsx:442-604` verify dirty→prompt, clean→no prompt, confirm proceeds, cancel stays, re-click active tab no prompt. | PASS |
| 6 | System Log Path from `systemInfo.paths.logs`; amber banner relocated inside Paths card | `SystemSettingsSection.tsx:267` reads `systemInfo?.paths.logs ?? '—'`. Banner at `:433-438` inside Paths `CardSection` with `data-testid="system-edit-config-note"` and canonical `text-amber-800` tokens. Tests verify exact value, fallback to `—`, card containment, orphan absence, and token consistency (`SystemSettingsSection.test.tsx:97-184`). | PASS |
| 7 | Typography baseline (`text-balance`/`text-pretty`/`tabular-nums`, no new motion) | `SettingsSection.tsx:16` (h2 `text-balance`), `:17` (p `text-pretty`). `SystemSettingsSection.tsx:45` (InfoRow `tabular-nums`). `AgentSettingsSection.tsx:227,231,184` (Input, unit, pre `tabular-nums`). Contract tests verify no per-section drift and no motion tokens on typography-pass lines. | PASS |
| 8 | No-project CTAs + Label/Templates empty-list next-step actions | `NoProjectCard.tsx:22-57` with Select/Create Project buttons. `SectionState.tsx` extended with `no-project` variant and `action` slot. `SettingsPage.tsx:24-58` uses `NoProjectCard` for inbox/repos/labels. `WorkflowProfilesSection.tsx:275-276` and `TemplatesSection.tsx:291-292` use it directly. Label catalog empty-list renders inline "New definition" CTA; Templates empty-list renders "New Template" CTA. Tests cover all project-scoped sections. | PASS |
| 9 | Label catalog search input | `LabelCatalogSection.tsx:441-451` renders search with `aria-label="Search label definitions"` (not placeholder-only). `matchesSearch` at `:95-100` filters by key/description/supportedValues. Tests verify rendering, filtering by all three dimensions, clear-restore, and "no matches" message without CTA. | PASS |

<promise>PASS</promise>
