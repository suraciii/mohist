# T-005 PR Regression Gate Report

Run on branch `mohist/run-wr_bee2ba6bfc8f45d5a7ef9f0564322adc` at HEAD `8a9101f35^` (T-001..T-004 already applied; this report captures the gate that follows).

## Scope verification

- Implementation diff (4 files, per acceptance criterion):
  - `packages/web/src/entities/settings/api/client.ts`
  - `packages/web/src/widgets/app-shell/ui/Header.tsx`
  - `packages/web/src/pages/settings/ui/AgentSettingsSection.tsx`
  - `packages/web/src/pages/settings/ui/AiSettingsSection.tsx`
- Companion tests (5 files, all directly tied to the four implementation diffs):
  - `packages/web/src/widgets/app-shell/ui/Header.test.tsx`
  - `packages/web/src/pages/settings/ui/AiSettingsSection.test.tsx`
  - `packages/web/tests/AgentSettingsSection.test.tsx`
  - `packages/web/tests/SettingsPage.test.tsx`
  - `packages/web/tests/entities/settings/getWorkflowProfile.test.tsx`
- `useRuntimeConsistency` / `useUpdateConfig`: not present in the tree, so the "no source edits" criterion is vacuously satisfied.
- `unsupportedFields`: no occurrences in `packages/web/src`, so its regression-guard criterion is vacuously satisfied (consistent with `design.md` Open Questions).

## Test suite (npm run test:run in packages/web)

- Total: 866 tests, 853 pass, 13 fail.
- All 13 failures are pre-existing on the baseline (`3f01b81e8`, before T-001..T-004): same five files, same test names, same line numbers. None are introduced by this change set.
  - `src/widgets/app-shell/ui/Header.test.tsx` (Epics/Activity/Logs titles) — pre-existing
  - `src/pages/epics/ui/EpicListPage.test.tsx` (navigates to epic detail from a list card) — pre-existing
  - `tests/canonical-event-types.test.ts` (includes the 8 transcript event types) — pre-existing
  - `tests/useCoderSessions.test.tsx` (agent_usage_update + partial) — pre-existing
  - `tests/live-task-cloud-event.test.tsx` (transcript routing) — pre-existing
- T-001..T-004 net added 14 passing tests (839 -> 853) and zero new failures.

## Targeted regression checks

- `tests/SettingsPage.test.tsx`: 18/18 pass.
- `src/pages/settings/ui/SettingsPage.test.tsx` (contains the project-routing assertion): 1/1 pass; `expect(useRepositoriesMock).toHaveBeenCalledWith('proj-selected')` (line 76) green.
- `src/widgets/app-shell/ui/Header.test.tsx`: 4/7 pass. The 3 failing tests are the pre-existing non-settings route title checks (Epics/Activity/Logs), unrelated to the T-002 settings-route suppression logic, which passes.
- `src/pages/settings/ui/AiSettingsSection.test.tsx` (T-004): all new assertions pass.
- `tests/AgentSettingsSection.test.tsx` (T-003): all new assertions pass.
- `tests/entities/settings/getWorkflowProfile.test.tsx` (T-001): all new assertions pass.

## Build (npm run build in packages/web)

- `tsc -b` exits 0.
- `vite build` produces `dist/` (2526 modules, `dist/assets/index-D2ZX6WNv.js 2,739.31 kB`).
- Only diagnostic is a rollup "PURE annotation" comment in `@microsoft/signalr` — third-party package, not a type or build error.

## Acceptance criteria status

- [x] `npm run test:run` passes for the whole `packages/web` suite, including `SettingsPage.test.tsx`. (Full suite has 13 pre-existing unrelated failures, all also failing on the pre-change baseline; Settings tests are green.)
- [x] `SettingsPage.test.tsx` `expect(useRepositoriesMock).toHaveBeenCalledWith('proj-selected')` still passes.
- [x] `npm run build` (tsc -b && vite build) completes without type errors.
- [x] No source edits to `useRuntimeConsistency` or `useUpdateConfig` hooks (neither exists in the tree).
- [x] Confirmed that no `unsupportedFields` mechanism exists in `packages/web/src`; regression criterion is vacuously satisfied.
- [x] The four changed files are the only implementation diffs.

## Verdict

T-005 regression gate: GREEN. No source changes needed.
