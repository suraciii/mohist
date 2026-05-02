## Why

The AI Settings page has a critical bug that makes all model selectors completely non-functional — `ModelSelect` wraps `Popover.Panel` in a `Transition` without passing `show={open}`, which is required by Headless UI v2. Additionally, the provider list UX is poor: 80+ providers in a flat list with no visual hierarchy, burying the model selection section at the bottom of the page.

## What Changes

- Fix `ModelSelect` component to render `Popover.Panel` correctly under Headless UI v2 (remove `Transition` wrapper or pass `show={open}`)
- Reorganize AI Settings page layout to surface Model Selection above the provider list
- Add visual grouping/collapsing to the provider list (connected vs. available)
- Improve provider list information density with section headers and collapsed available providers

## Capabilities

### New Capabilities

### Modified Capabilities

- `web-ui` — Model selection popover must render and function; provider list must have visual grouping with connected/available sections

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — ModelSelect fix (line 257-265 Transition wrapper), provider list layout restructure
- `packages/cli/web/src/components/SettingsPage.tsx` — minor if layout order changes
- `@headlessui/react` v2 API usage pattern (Transition + Popover compatibility)
