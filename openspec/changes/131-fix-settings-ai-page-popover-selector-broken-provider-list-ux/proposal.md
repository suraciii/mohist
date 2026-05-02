## Why

The Settings AI page has a critical bug that makes all model selection popovers completely non-functional — `Transition` wraps `Popover.Panel` without passing `show={open}`, which is required by Headless UI v2 (the project uses v2.2.10). This blocks users from selecting any model. Additionally, the provider list presents 80+ items in a flat, unstructured layout with no visual hierarchy, burying the Model Selection section at the page bottom.

## What Changes

- Fix `ModelSelect` component: remove `Transition` wrapper from `Popover.Panel` (or add `show={open}`), making popovers functional again in Headless UI v2
- Restructure provider list: separate connected providers from available providers with visual grouping and collapsible sections
- Reorder page sections: elevate Model Selection above the full provider list so it's immediately accessible

## Capabilities

### New Capabilities

- `ai-settings-provider-list` — structured, grouped provider list with visual hierarchy and collapsible sections

### Modified Capabilities

- `web-ui` — Model select popover must render correctly; page section ordering changes

## Impact

- `packages/cli/web/src/components/AiSettingsSection.tsx` — ModelSelect component (Transition fix), provider list restructuring, section reordering
- `packages/cli/web/src/components/SettingsPage.tsx` — no structural changes expected
- Dependency: `@headlessui/react` v2 API usage (already installed at ^2.2.10)
