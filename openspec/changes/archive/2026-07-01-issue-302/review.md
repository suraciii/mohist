# Review Report

## Result: FAIL

## Repaired Items

- None.

## Acceptance Criteria Evidence

- Global settings are routed outside the project subtree via `/settings` and `/settings/:section` before the `ProjectGuard` route in `packages/web/src/app/App.tsx:60-62`; zero-project reachability is covered by `packages/web/src/app/App.test.tsx:234-241` and `packages/web/src/widgets/app-shell/ui/ProjectGuard.test.tsx:63-85`.
- URL scope is mostly implemented: legacy global sections redirect from `/:projectName/settings/<global-section>` to `/settings/<section>` in `packages/web/src/app/App.tsx:119-124`, and project sections remain under `/:projectName/settings/:section` in `packages/web/src/app/App.tsx:77-79`.
- Navigation is grouped visually and semantically in `packages/web/src/pages/settings/ui/SettingsSubNav.tsx:158-204`, with Application and Project groups derived from `SETTINGS_SECTIONS` in `packages/web/src/pages/settings/lib/sections.tsx:47-93`.
- `OnboardingBanner.tsx` is deleted, and production source has no `OnboardingBanner`, `ONBOARDING_DISMISSED_KEY`, `showOnboarding`, or `dismissOnboarding` references. Remaining matches are openspec evidence and negative tests only.
- Section headings are sourced from `getSectionMeta` in the section components, and static consistency coverage exists in `packages/web/src/pages/settings/ui/settings-consistency.test.tsx:73-124`.
- Keyboard roving tabindex and `aria-current="page"` are implemented in `packages/web/src/pages/settings/lib/useRovingTabindex.ts:18-76` and wired in `packages/web/src/pages/settings/ui/SettingsSubNav.tsx:43-55`, with unit coverage in `packages/web/src/pages/settings/lib/useRovingTabindex.test.tsx:62-219` and page coverage in `packages/web/src/pages/settings/ui/SettingsPage.test.tsx:161-278`.
- Settings Search now uses `useSettingsSectionPath` in `packages/web/src/features/settings-search/SettingsSearch.tsx:168-180`, with scope navigation coverage in `packages/web/src/features/settings-search/SettingsSearch.test.tsx:637-739`.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/app/App.tsx`, `packages/web/src/widgets/app-shell/ui/ProjectGuard.tsx`, project routing
  Evidence: The candidate adds top-level `/settings` and `/settings/:section` routes before the dynamic `/:projectName` route (`packages/web/src/app/App.tsx:60-64`). Project names are still allowed if they are DNS labels, with no reserved-name rejection for `settings` (`packages/server/src/Mohist.Server/Project/Domain/ProjectName.cs:19-30`). That makes `settings` a valid project name, but its root path now redirects to global settings (`packages/web/src/app/App.tsx:60`) and `/settings/issues` is interpreted as an application settings section, then redirected to `/settings/ai` by the invalid-section fallback (`packages/web/src/pages/settings/ui/SettingsPage.tsx:62-63`). `ProjectGuard` also bypasses any pathname under `/settings/` (`packages/web/src/widgets/app-shell/ui/ProjectGuard.tsx:7-24`), so longer routes for a project named `settings` no longer get the normal project-existence gate. [disallowed:product behavior and public routing contract change]
  SuggestedAction: Either reserve `settings` and other top-level application route segments at project creation/migration time, or make the application settings route match only known settings section keys and fall through for non-settings project routes. Add regression coverage for a project named `settings` or for rejecting that name.
  Verification: Add a web routing test that creates/loads a project named `settings` and verifies `/settings`, `/settings/issues`, and `/settings/settings/repositories` behavior, or add server specs proving `settings` is rejected as a project name. Then run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and relevant server tests if name validation changes.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: route collision coverage
  Evidence: The new routing tests cover normal project names such as `demo` (`packages/web/src/app/App.test.tsx:189-289`) but do not cover collision with the newly introduced top-level `/settings/*` route. The server-side project-name tests and validation do not document `settings` as reserved, so the regression in item-1 passed the suite. [disallowed:requires product decision on reserved names vs route matching]
  SuggestedAction: Add explicit route-collision regression tests alongside the implementation fix, either in `App.test.tsx` for allowed project-name behavior or in server project-name specs for a reserved-name contract.
  Verification: The new test should fail on this candidate and pass after the route/name contract is fixed; then run `npm run test:run -w packages/web` and any affected server test target.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: issue prose / openspec traceability
  Evidence: The issue body says there are 8 settings tabs and lists 4 project-level tabs, but the actual code and candidate artifacts correctly include 9 sections with `Inbox` as project-scoped (`packages/web/src/pages/settings/lib/sections.tsx:35-45`, `openspec/changes/issue-302/specs/settings-shell/spec.md:19-27`). This did not break the candidate because the implementation includes Inbox.
  SuggestedAction: Optionally update the issue prose later so future readers do not reintroduce the undercount.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: `packages/web/tests/live-task-cloud-event.test.tsx`
  Evidence: The full web test run still reports 1 skipped test, located at `packages/web/tests/live-task-cloud-event.test.tsx:311` (`it.skip('shows approval toast for legacy approval_requested events', ...)`). This file is not part of the issue-302 diff and did not affect the settings IA candidate.
  SuggestedAction: Track separately if the skipped legacy approval toast behavior still matters.
  Status: pre-existing

## Verification

- `npm run typecheck -w packages/web`: passed.
- `npm run test:run -w packages/web`: passed, 227 test files passed, 3476 tests passed, 1 skipped.

<promise>FAIL</promise>
