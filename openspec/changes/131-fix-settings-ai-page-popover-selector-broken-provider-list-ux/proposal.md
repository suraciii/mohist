## Why

The AI Settings page is partially broken and partially unusable: all model selector popovers fail to open due to a Headless UI v1→v2 API mismatch, and the 80+ provider list is an unstructured wall of "Connect" buttons that buries the important Model Selection section at the page bottom.

## What Changes

- Fix ModelSelect popover: remove the `Transition` wrapper around `Popover.Panel` (or add `show={open}`) to restore compatibility with `@headlessui/react` v2.2.10, where `Transition` no longer auto-detects `Popover` open state
- Restructure the AI Settings layout: move Model Selection above the provider list so the most-used controls are immediately visible
- Add visual grouping to the provider list: separate connected/configured providers from unconfigured ones with clear section headers, and collapse unconfigured providers into an expandable "Available Providers" section

## Capabilities

### New Capabilities

- `settings-ai-page-ux` — visual grouping and layout of the AI settings section (provider list organization, Model Selection prominence)

### Modified Capabilities

- `web-ui` — ModelSelect component must render a functional popover panel under Headless UI v2

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — ModelSelect component (Transition/Popover fix), layout reordering, provider list grouping
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes expected; section layout driven by AiSettingsSection
- Dependency: `@headlessui/react` v2.2.10 (already installed, no version change needed)
