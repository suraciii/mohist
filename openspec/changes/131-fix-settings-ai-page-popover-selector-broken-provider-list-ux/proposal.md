## Why

Model selection popovers on the Settings AI page are completely non-functional — the `Transition` wrapper uses Headless UI v1 patterns with v2 (`^2.2.10`), causing `Popover.Panel` to never render. Additionally, 80+ providers are shown in a flat list with no visual hierarchy, burying the Model Selection section at the bottom.

## What Changes

- Remove `Transition` wrapper from `ModelSelect` component; use `Popover.Panel` directly (Headless UI v2 pattern)
- Restructure AI settings page layout to surface Model Selection above the provider list
- Add visual grouping to the provider list (connected providers separated from available providers with collapsible sections)
- Ensure all `Popover.Panel` instances (Mohist Model, Coder Model, Stage Model Overrides) render correctly

## Capabilities

### New Capabilities

- `ai-settings-provider-ux` — visual grouping and collapsible sections for the provider list

### Modified Capabilities

- `web-ui` — ModelSelect popover must function (Popover.Panel renders and is interactive); AI settings page layout restructured

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — `ModelSelect` component (lines 173–327), specifically the `Transition` block (lines 257–265); provider list rendering and section ordering
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes expected, but AI section content reordering may affect perceived layout
- Dependency: `@headlessui/react ^2.2.10` — no version change needed, just migrating from v1 to v2 API usage
