## Why

The AI Settings page is partially broken: all model selector popovers (Mohist Model, Coder Model, Stage Overrides) fail to open because `Transition` wraps `Popover.Panel` without passing `show={open}` — a Headless UI v1 pattern that doesn't work with the project's v2.2.10. Additionally, 80+ providers render as a flat ungrouped list with no visual hierarchy, burying Model Selection at the page bottom.

## What Changes

- Fix `ModelSelect` component: remove `Transition` wrapper from `Popover.Panel` (or add `show={open}`), making popovers functional under Headless UI v2
- Restructure provider list: separate connected providers from unconfigured ones with visual grouping or collapsible sections
- Move Model Selection section higher in the page layout so it's accessible without scrolling past the full provider list

## Capabilities

### New Capabilities

### Modified Capabilities

- `web-ui` — Model selector popover interaction and provider list layout within AI Settings section

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — `ModelSelect` component (lines 173–327): remove `Transition` wrapper at lines 257–265; provider list rendering and layout reordering
- `packages/cli/web/src/components/SettingsPage.tsx` — minor if section ordering changes
- Dependency: `@headlessui/react` v2.2.10 (already installed, no version change needed)
