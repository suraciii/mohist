# Review Report

## Result: PASS

Post-repair candidate snapshot reviewed against issue #281, `openspec/changes/issue-281/proposal.md`, `design.md`, `tasks.json`, `specs/epic-create-flow/spec.md`, and all changed product files under `packages/web/src`. The reviewed product deliverable is the Web UI change outside the workflow artifact directory.

Acceptance evidence:

- Guided Create description scaffold exists in `packages/web/src/shared/lib/epic-description-template.ts:12` with `## Goal`, `## Background`, `## Non-goals`, and `## Scope`; Create initializes with it in `packages/web/src/features/create-epic/ui/EpicCreateDialog.tsx:38`.
- Create can submit only markdown with no new API fields: `EpicCreateDialog.tsx:54` sends `{ title, description, priority }`, and `packages/web/src/entities/epic/api/client.ts:12` strips `projectId` before posting the same body.
- Create success stays in-dialog and offers both paths: idle wording is rendered in `EpicCreateDialog.tsx:107`, `Stay` in `EpicCreateDialog.tsx:197`, and `Open Epic` navigates to `toProjectPath(/epics/${createdEpic.id})` in `EpicCreateDialog.tsx:79`.
- Edit preserves existing markdown by loading `epic.description` directly in `packages/web/src/features/edit-epic/ui/EditEpicDialog.tsx:33` and resetting from `epic.description` on open in `EditEpicDialog.tsx:40`; template insertion is opt-in through `EpicDescriptionField` at `EditEpicDialog.tsx:104`.
- Mobile-oriented structure is present: dialog content is width-limited and internally scrollable in `EpicCreateDialog.tsx:91` and `EditEpicDialog.tsx:68`, with footer actions outside the scroll region in `EpicCreateDialog.tsx:191` and `EditEpicDialog.tsx:144`.
- Regression tests cover create template/submit/success/navigation/mobile structure in `packages/web/src/features/create-epic/ui/EpicCreateDialog.test.tsx`, edit verbatim/load/save/insert/mobile structure in `packages/web/src/features/edit-epic/ui/EditEpicDialog.test.tsx`, shared field behavior in `packages/web/src/shared/ui/epic-description-field/EpicDescriptionField.test.tsx`, and detector edge cases in `packages/web/src/shared/lib/epic-description-template.test.ts`.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: missing-obvious-guards
  Evidence: `hasEpicDescriptionStructure` used substring matching, so text like `Mention ## Goal, ## Background, ## Non-goals, and ## Scope` or `### Goal` headings could be misclassified as already structured. Repaired `packages/web/src/shared/lib/epic-description-template.ts:28` to require standalone `##` header lines with optional trailing whitespace, and added regression tests in `packages/web/src/shared/lib/epic-description-template.test.ts:64`, `:111`, and `:116`.
  Verification: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web` passed with 193 test files, 2871 tests passed, 1 skipped.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: formatting
  Evidence: `packages/web/src/features/edit-epic/ui/EditEpicDialog.tsx` was missing a final newline. Repaired the file ending.
  Verification: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web` passed with 193 test files, 2871 tests passed, 1 skipped.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: formatting
  Evidence: The shared `DialogFooter` default includes negative margins intended for padded dialog content, while the updated Create/Edit dialogs set `DialogContent` to `p-0`. Repaired the two local footer class lists with `mx-0 mb-0` in `packages/web/src/features/create-epic/ui/EpicCreateDialog.tsx:192` and `packages/web/src/features/edit-epic/ui/EditEpicDialog.tsx:145`, preventing footer bleed outside the dialog box.
  Verification: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web` passed with 193 test files, 2871 tests passed, 1 skipped.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: mobile layout verification
  Evidence: Mobile tests assert structural classes/footer placement and stub `documentElement.scrollWidth`/`clientWidth`, so they do not execute a real browser layout or a real soft-keyboard viewport. The implementation uses constrained widths, internal scroll regions, and footer actions, so this is residual verification risk rather than a discovered product defect.
  SuggestedAction: Add a Playwright/mobile viewport smoke test for Create/Edit dialogs if this flow becomes layout-sensitive again, especially around 320px width and visual viewport resizing.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: packages/web test configuration
  Evidence: `npm run test:run -w packages/web` prints a Vitest 4 deprecation warning: `test.poolOptions` was removed and previous `poolOptions` are now top-level options. This is unrelated to issue #281 and does not affect the passing test result.
  SuggestedAction: Update the Vitest configuration in a separate cleanup change.
  Status: pre-existing

<promise>PASS</promise>
