# Visual And Accessibility Evidence

## Scope

- Issue: 116, Settings visual consistency refactor.
- Reviewed surface: `packages/web/src/pages/settings` and shared `ModelSelect` icon source.
- Evidence date: 2026-06-18.

## Static Acceptance Evidence

- `text-gray-` under `packages/web/src/pages/settings`: 0 product matches.
- `text-foreground/85`, `text-foreground/80`, `text-foreground/75` under `packages/web/src/pages/settings`: 0 product matches.
- `<svg` under `packages/web/src/pages/settings`: 0 product matches; remaining matches are inside `settings-consistency.test.ts`, where the pattern is asserted as forbidden.
- `ModelSelect.tsx` contains no local `SearchIcon`, `ChevronDownIcon`, or `XIcon` SVG definitions and uses `lucide-react` imports.
- Six Settings page section files no longer hand-write page-title-styled `<h3 className="text-sm font-medium text-foreground">`; page title styling is owned by `SettingsSection`.

## Automated Verification

- `npm run test:run -- SettingsPage.test.tsx AiSettingsSection.test.tsx ModelSelect.test.tsx settings-consistency.test.ts` passed from `packages/web` with 5 files and 51 tests.
- `npm run test:run -- settings-visual-accessibility-evidence.test.tsx` passed from `packages/web` and generated `openspec/changes/issue-116/visual-accessibility-artifacts/`.
- `npx tsc -b` passed from `packages/web`.

## Visual And Accessibility Evidence Status

- `openspec/changes/issue-116/visual-accessibility-artifacts/*-before.txt` documents the pre-refactor visual inconsistency for each Settings tab from the issue reproduction list.
- `openspec/changes/issue-116/visual-accessibility-artifacts/*-after.html` contains deterministic post-refactor rendered snapshots for the six Settings tabs.
- `openspec/changes/issue-116/visual-accessibility-artifacts/*-visual-diff.txt` records the Before/After visual contract comparison for every Settings tab.
- `openspec/changes/issue-116/visual-accessibility-artifacts/contrast-audit.json` records computed WCAG AA contrast ratios from project CSS color values for Settings body-text tokens; all checked nodes passed with no violations.
- The repository still has no Playwright or axe-core dependency; this evidence uses the committed Vitest/jsdom harness as the project-local equivalent visual and contrast audit.

## Reviewer Note

The committed code satisfies the grep-enforced visual contract, regression tests, Before/After rendered visual evidence, and a computed contrast-audit evidence pass. A future Playwright adoption can replace the jsdom HTML snapshots with pixel screenshots without changing the Settings contract.
