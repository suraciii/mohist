## Why

The AI Settings page has a critical bug: all model selection popovers are completely broken because `ModelSelect` uses Headless UI v1's `Transition` wrapper pattern without `show={open}`, but the project runs v2 where `Transition` no longer auto-detects Popover open state. Additionally, the provider list presents 80+ items in a flat, unstructured layout with no visual hierarchy, burying the important Model Selection section at the bottom.

## What Changes

- Fix `ModelSelect` component to work with `@headlessui/react` v2 by removing the `Transition` wrapper from `Popover.Panel` (or adding `show={open}`)
- Restructure the AI Settings page layout so Model Selection appears before the provider list, making the most-used controls immediately accessible
- Group providers into visual sections: Connected (expanded by default), Available (collapsible), reducing the flat-list information overload

## Capabilities

### New Capabilities

- `settings-ai-model-select` — Model selection popover component with search, keyboard navigation, and grouping by provider; compatible with Headless UI v2

### Modified Capabilities

- `web-ui` — AI Settings page layout restructured: Model Selection section promoted above provider list; provider list split into Connected / Available groups with collapsible sections

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — `ModelSelect` component (lines 173–327): remove `Transition` wrapper; restructure `AiSettingsSection` layout to reorder sections
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes needed (renders `AiSettingsSection` as-is)
- No API, dependency, or backend changes required
