## Why

The AI Settings page is currently broken — all model selection popovers (Mohist Model, Coder Model, Stage Model Overrides) fail to open because `ModelSelect` wraps `Popover.Panel` in a Headless UI v1 `Transition` without `show={open}`, and the installed v2 `Transition` defaults to closed. Separately, 80+ unconfigured providers render as a flat list, burying the Model Selection section at the bottom with no visual hierarchy.

## What Changes

- Fix `ModelSelect` component: remove `Transition` wrapper from `Popover.Panel` (line 257–265) to use Headless UI v2 native popover animation, restoring all model selectors
- Restructure the Provider list: group providers into "Connected" and "Available" sections, collapse unconfigured providers by default
- Reorder AI Settings sections: move Model Selection above the full Provider list so it's immediately accessible

## Capabilities

### New Capabilities

- `model-select-popover` — functional model selection popover component with search, keyboard navigation, and grouped results

### Modified Capabilities

- `web-ui` — AI Settings page layout and provider list UX changes

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — `ModelSelect` component (Transition removal), provider list restructuring, section reordering
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes expected
- Dependency: `@headlessui/react` v2 API (already installed)
