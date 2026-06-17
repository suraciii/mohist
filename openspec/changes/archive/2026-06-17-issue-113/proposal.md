## Why

The Coder Agent model selection dropdown in Settings and Issue Detail is completely unusable with mouse interaction. Base UI Popover's dismissable layer fires before React synthetic `onClick` events, swallowing mouse clicks on model options inside the popover. This P0 bug has broken the core model selection feature since May 2026, affecting the Settings → Coder Agent tab (Default Coder Agent Model + 4 Stage Model Overrides) and Issue Detail model overrides.

## What Changes

- Fix `ModelSelect.tsx` popover item click handling so real mouse clicks trigger model selection and the `onChange` callback
- Keyboard navigation (Arrow keys, Enter), search filtering, and the X clear button remain functional after the fix
- Add tests to verify that selection triggers the expected callback (API call) on both synthetic and real click events
- Existing `ModelSelect.test.tsx` continues to pass

## Capabilities

### New Capabilities

None. This is a pure bug fix — the existing `web-ui` spec already defines the expected model selection behavior.

### Modified Capabilities

None. No requirement-level behavior changes; the fix restores the component to its already-specified contract.

## Impact

- **Affected code**: `packages/web/src/shared/ui/ModelSelect.tsx` (popover item `onClick` handler at line 213)
- **Dependencies**: `@base-ui/react/popover` (already in use, no version change)
- **Consumers**: `AiSettingsSection.tsx` (Settings → Coder Agent), `IssueModelSelector.tsx` (Issue Detail), and any other component using `ModelSelect`
- **Tests**: New or updated tests in `ModelSelect.test.tsx`, regression check on `SettingsPage.test.tsx`
