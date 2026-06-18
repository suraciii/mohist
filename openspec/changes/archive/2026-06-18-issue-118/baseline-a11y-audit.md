# Settings Accessibility Baseline Audit

Captured for task `T-001` before applying any Settings application-source fixes. This file is the confirmed-defect source of truth for follow-up tasks.

## Scope

- Routes: `/settings/ai`, `/settings/agent`, `/settings/repositories`, `/settings/workflows`, `/settings/templates`, `/settings/system`
- Structural harness: `packages/web/tests/settings-a11y.test.tsx` with `vitest-axe` under jsdom
- Browser harness: `packages/web/tests/a11y/settings.a11y.spec.ts` with Playwright and `@axe-core/playwright`

## Confirmed Defects

- `RepositoriesSection` renders non-default repository `Set default` and `Remove` buttons with `text-xs h-7`, producing a 28px-tall target below the 44x44px target-size requirement.
- `TemplateEditor` renders its inline close icon button with `size="icon-xs"`, below the 44x44px target-size requirement.
- `AiSettingsSection` Stage Model Overrides disclosure does not expose `aria-expanded` or `aria-controls`, so assistive technology cannot read the collapsed/expanded state.
- `AiSettingsSection` model labels are orphan labels: the visible `Default Coder Agent Model` label and per-stage labels are not programmatically associated with their `ModelSelect` trigger controls.
- `AgentSettingsSection` runtime number inputs are not programmatically associated with their visible labels; `vitest-axe` reports a critical `label` violation.
- `SystemSettingsSection` log-level select trigger has no accessible name in the rendered baseline; `vitest-axe` reports a critical `button-name` violation.
- Settings has no page-level `<h1>` landmark; section headings currently start below the required page heading hierarchy.
- `TemplateEditor` is an inline `CardSection` editor, not a modal dialog. Dialog focus-trap and `aria-modal` checks are reclassified as not applicable to this component unless a future task changes it into a modal.

## Disproved Assumptions

- Settings tabs intentionally use horizontal overflow on narrow screens; this is expected for the tab list and is not a defect.
- Repository Git URL rows already use `min-w-0` and `truncate`; no URL overflow defect is confirmed.
- Repository rows are already single-column; the only narrow-form risk is the Add Repository input grid.
- Sonner toasts already provide status/live-region semantics in the installed implementation, so Settings mutation feedback does not require a shared-toast refactor.

## Baseline Target

- `npm run test:run` remains the existing vitest suite and is not changed by this harness.
- `npm run test:a11y` is the new accessibility target. It is expected to fail until follow-up tasks fix the confirmed defects, then pass with zero critical or serious violations across all 6 Settings tabs.

## T-005 Contrast Follow-up

- `SettingsSection` description token changed from `text-muted-foreground` to `text-foreground/85` so shared section descriptions no longer rely on the muted token when rendered over muted/tinted descendants. Runtime assertion target: >= 4.5:1; measured after patch on `/settings/system`: >= 4.5:1.
- Error-state text on tinted red backgrounds was standardized from `text-red-600` to `text-red-700` in Settings-specific error/action states. Token ratio changed from red-600 on red-50 `3.76:1` to red-700 on red-50 `5.17:1`.
- Sonner mutation feedback remains verify-only: settings mutation success/error text is asserted inside the rendered notifications `aria-live="polite"` region; no shared toast internals were modified.
