## Why

Model selection in the AI Settings page is completely broken — all Popover selectors fail to open due to a Headless UI v1→v2 API mismatch in the `Transition` wrapper. Additionally, the provider list presents 80+ items in an unstructured flat list, burying the model selection section at the bottom and making it impractical to configure providers.

## What Changes

- Fix `ModelSelect` component by removing the `Transition` wrapper around `Popover.Panel` (or wiring `show={open}`), aligning with Headless UI v2 API
- Restructure the AI Settings page layout so Model Selection appears above the provider list
- Add visual grouping to the provider list (configured vs. unconfigured) with collapsible sections for unconfigured providers
- Add provider search/filter to reduce visual noise

## Capabilities

### New Capabilities

- `ai-settings-provider-list-ux` — categorized, collapsible provider list with search

### Modified Capabilities

- `web-ui` — ModelSelect popover now functional on the AI Settings page

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — `ModelSelect` component (Transition fix) and `AiSettingsSection` layout restructuring
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes expected
- Dependency: `@headlessui/react` v2 API usage (already installed, no version change needed)
