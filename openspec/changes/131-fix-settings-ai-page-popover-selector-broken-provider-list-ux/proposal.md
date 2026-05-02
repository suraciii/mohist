## Why

The Settings AI page is functionally broken — all model selector popovers fail to open because `Popover.Panel` is wrapped in a `Transition` that lacks the `show={open}` prop required by Headless UI v2. Additionally, the provider list has no visual hierarchy, burying model selection beneath 80+ flat-listed providers.

## What Changes

- Fix `ModelSelect` component: remove `Transition` wrapper or pass `show={open}` so `Popover.Panel` renders correctly under Headless UI v2 (`@headlessui/react` 2.2.10)
- Restructure the AI settings page layout so Model Selection appears above or alongside the provider list instead of being buried at the bottom
- Add visual grouping to the provider list (connected providers prominently at top, unconfigured collapsed or secondary)

## Capabilities

### New Capabilities

### Modified Capabilities

- `web-ui` — Model selector popover rendering and AI settings page layout change

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — `ModelSelect` component (lines 173–327), specifically the `Transition` wrapper at lines 257–265; page layout reordering
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes needed (AI section rendered via `AiSettingsSection`)
- Dependency: `@headlessui/react` v2.2.10 (already installed) — the fix aligns usage with v2 API
