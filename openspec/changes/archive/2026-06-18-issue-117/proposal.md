## Why

The `/audit-test-1/settings/*` page exposes 6 tabs but offers no guidance for first-time users: Workflow profiles show no stage preview or concept explanation, Repositories has a weak empty state with no call-to-action, there is no onboarding hint pointing users to the most important tab (Coder Agent), Runtime fields use opaque translated technical names with no business meaning, and the Settings nav entry is reachable even without a project context. These gaps make the Settings surface feel unguided and hard to navigate, undermining adoption of the core configuration workflows.

## What Changes

- **Workflows tab**: Workflow profile cards render a 4-chip stage preview (`plan → build → check → integrate`); long descriptions collapse to 2 lines with a "Read more" toggle; a top-level paragraph/tooltip explains "Workflow profiles define how issues move through stages".
- **Repositories tab**: Empty state gains a prominent "Add your first repository" CTA that auto-focuses the Name input on click; the inline "Add Repository" form is hidden in empty state (mutually exclusive with the CTA) and only renders once at least one repository exists.
- **Onboarding**: First visit to Settings (tracked via `localStorage`) shows a dismissable info banner on the Coder Agent tab ("Start here — select the coder agent model used for workflow tasks"); dismissed state persists across sessions.
- **Runtime field descriptions**: `AgentSettingsSection` fields get business-level descriptions — `Max Concurrent` ("upper bound is constrained by runner capacity (top-right 0/4); excess queues"), `Poll Interval` ("shorter = more realtime but higher CPU/network"), and `Retry Budget` is relabeled to `Retry attempts` with unit `times` (replacing the obscure `grace periods`); descriptions surface on hover/focus via tooltips.
- **Settings entry gating**: The Settings nav button is hidden (or shows a "Select a project first" tooltip) when no project context is active; normal flow is unaffected.
- Establishes a unified pattern for Settings section descriptions, empty states, and onboarding cues to pay down the per-section style fragmentation noted in the issue's Tech Debt section.

## Capabilities

### New Capabilities

- `settings-ux`: Unified UX patterns for the Settings page — workflow profile stage preview and concept explanation, repository empty-state CTA with focus handoff, first-visit dismissable onboarding banner persisted via `localStorage`, business-level Runtime field descriptions with hover/focus tooltips, and project-context gating of the Settings nav entry.

### Modified Capabilities

_None._ The existing `web-ui` and `settings-system-diagnostics` specs cover unrelated concerns (SSE-driven surfaces and System-section diagnostics respectively); no spec-level requirements change there. `workflow-config` is unaffected because workflow definition semantics are not changing — only how profiles are previewed in the UI.

## Impact

- **Frontend (`packages/web/src/pages/settings/ui/`)**:
  - `WorkflowProfilesSection.tsx` — stage chips, description clamp + "Read more", concept header
  - `RepositoriesSection.tsx` — empty-state CTA, conditional form rendering, focus handoff
  - `SettingsPage.tsx` — onboarding banner state + `localStorage` persistence, Coder Agent tab targeting
  - `AgentSettingsSection.tsx` — field description content, `Retry Budget` → `Retry attempts` label/unit, tooltip behavior
- **Layout / nav**: Settings nav button visibility/tooltip gated on project context
- **Tests**:
  - `SettingsPage.test.tsx` must continue to pass
  - New tests for empty-state rendering, CTA focus handoff, onboarding dismiss persistence, field labels/units
- **No backend / API changes** — all changes are presentation-layer; no data model, endpoint, or dependency impact
- **Browser storage**: New `localStorage` key for onboarding dismissal state (clearable to re-trigger)
