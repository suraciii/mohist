## Context

The Settings page (`packages/web/src/pages/settings/ui/`) exposes six tabs — Coder Agent, Runtime, Repositories, Workflows, Templates, System — but offers no guidance for first-time users. The current state, verified against the code:

- **Workflows**: `WorkflowProfilesSection.tsx` already renders a top-level concept paragraph ("Workflow profiles define how issues move through stages…", lines 160-165), so that part of the change is already satisfied. However, `ProfileCard` shows only `displayName` / `description` / `id` — no stage preview. The list type `WorkflowProfileInfo` (`entities/settings/model/types.ts:114`) does **not** include `stages`; only `WorkflowProfileDetail` (fetched per-profile via `useWorkflowProfile`) does.
- **Repositories**: `RepositoriesSection.tsx` always renders the inline "Add Repository" form (lines 120-169), even in the empty state where `SectionState variant="empty"` is shown above it. `SectionState` already accepts a `children` slot for empty-state content.
- **Onboarding**: No first-visit guidance exists. `SettingsPage.tsx` keys tabs off `section` (`ai`, `agent`, `repositories`, `workflows`, `templates`, `system`); the "Coder Agent" tab is section `ai`.
- **Runtime fields**: `AgentSettingsSection.tsx` defines a `FIELDS` array with a `description` field that is already populated (lines 101-150), but the `InputField` component (lines 170-209) never renders it — it only takes `label`/`unit`/`value`/`error`. There is also pre-existing label/unit drift: `FIELDS` declares `maxConcurrent` unit as `"sessions"` while the render site hardcodes `"agents"` (line 417). `Retry Budget` / `grace periods` is the field to rename.
- **Settings nav**: `AppSidebar.tsx` `configureNav` (line 48-51) always includes Settings. `ProjectGuard.tsx` (line 19) special-cases `/settings` to bypass the project requirement on direct URL access.
- **No Tooltip primitive**: `shared/ui/components/` has no `tooltip.tsx`, and the web package has no `@radix-ui/react-tooltip` dependency (only `popover.tsx`).
- **Persistence convention**: raw `localStorage.getItem/setItem` with `mohist:` namespaced keys (see `ProjectContext.tsx`, `IssueModelSelector.tsx`).

**Constraint**: The proposal explicitly scopes this to "No backend / API changes — all changes are presentation-layer; no data model, endpoint, or dependency impact." This rules out extending the workflow-profile list endpoint to include stages.

Stakeholders: end users configuring Mohist for the first time; the Settings page is the primary configuration surface. Related future work: Issue C (Settings UX consistency cleanup) and Issue D (visual consistency rebuild) — this change should establish a reusable pattern they can extend, not hardcode one-offs.

## Goals / Non-Goals

**Goals:**
- Make the Workflows, Repositories, Coder Agent, and Runtime tabs self-explanatory for a first-time user without requiring click-in to discover structure.
- Establish a single reusable empty-state + onboarding + field-description pattern that pays down the per-section style fragmentation flagged in the issue's Tech Debt section.
- Keep all changes in the presentation layer (`packages/web/src/`) with no backend, API, or data-model changes.

**Non-Goals:**
- Redesigning Settings visual language (deferred to Issue D).
- Unifying all six sections' description styles end-to-end (deferred to Issue C; this issue only touches the four sections in scope).
- Adding new backend endpoints or extending the workflow-profile list payload to include stages.
- Changing workflow definition semantics or runtime config behavior — only how they are *displayed*.
- Gating direct-URL access to `/settings` (the `ProjectGuard` bypass stays; only the *nav entry* visibility changes).

## Decisions

### Decision 1: Workflow stage chips come from a shared constant, not per-profile fetching

`ProfileCard` needs 4 stage chips (`plan → build → check → integrate`). The list endpoint does not return stages, and the proposal forbids backend changes.

- **Alternative A**: Extend `WorkflowProfileInfo` + backend list endpoint to include stage names. Rejected — violates the no-backend-change constraint.
- **Alternative B**: Fetch `WorkflowProfileDetail` for each profile in the list (N parallel queries via `useWorkflowProfile`). Rejected — wasteful (3+ extra requests on tab open) and the data is constant today.
- **Alternative C (chosen)**: Introduce a `DEFAULT_WORKFLOW_STAGES = ['plan', 'build', 'check', 'integrate']` constant and render chips from it.

**Rationale**: The existing code comment at `WorkflowProfilesSection.tsx:102` states "quick-fix and experiment reuse these stages from mohist/default; only the metadata above differs" — all current profiles share the same stages, so a constant is factually correct. It also keeps the change purely client-side. The constant lives alongside `WorkflowProfilesSection` (or in `entities/settings/model`) so Issue C/D can later swap it for real per-profile data when the backend is extended.

### Decision 2: Workflow description clamp uses CSS line-clamp + a "Read more" toggle driven by measured overflow

For descriptions exceeding 2 lines:

- **Alternative A**: Always show full description. Rejected — long descriptions dominate the card and push the stage chips below the fold.
- **Alternative B (chosen)**: CSS `line-clamp-2` by default; a `useRef` + `useEffect` measures `scrollHeight > clientHeight` to decide whether to render the "Read more" toggle; expanding removes the clamp.

**Rationale**: Render-time overflow measurement avoids hardcoding a character/line count and correctly handles responsive widths. The toggle only appears when actually needed (spec scenario: "Short description renders without toggle").

### Decision 3: Repository CTA reveals the form and focuses the Name input via local state + ref

- **Alternative A**: Always render the form but visually hide it in empty state; CTA just focuses. Rejected — the issue explicitly says "移除空状态下方的 Add Repository 表单 (与 CTA 二选一, 避免重复)".
- **Alternative B (chosen)**: Add `showForm` local state (default `false`). In empty state, render the `SectionState` with a prominent CTA passed via its `children` slot; the form is not rendered while `!showForm && repositories.length === 0`. CTA click sets `showForm = true` and a `useEffect` on `showForm` calls `nameInputRef.current?.focus()`. Once at least one repository exists, the form is always rendered (`showForm` ignored) per the spec scenario "Non-empty state renders the inline form".

**Rationale**: Reuses the existing `SectionState` `children` slot (no new primitive). The focus handoff is a one-line effect and is directly testable.

### Decision 4: Onboarding banner lives in `SettingsPage`, scoped to `section === 'ai'`, persisted via `localStorage`

- **Alternative A**: Render the banner inside `AiSettingsSection`. Rejected — couples onboarding to the settings-editing component and makes it harder for Issue C/D to generalize the pattern.
- **Alternative B (chosen)**: Add a small `OnboardingBanner` component rendered in `SettingsPage` above the `TabsContent` for the `ai` section. Dismissal state is read/written to `localStorage` under `mohist:settings-onboarding-dismissed` (matching the established `mohist:` namespacing in `ProjectContext`).

**Rationale**: Keeps `AiSettingsSection` focused; the banner is cross-cutting onboarding, not a Coder Agent setting. Using `localStorage` directly (not a context/store) matches the IssueModelSelector precedent and is the lightest mechanism that satisfies "persists across sessions" and "clearing localStorage re-triggers".

### Decision 5: Add a lightweight dependency-free `Tooltip` to `shared/ui/components/tooltip.tsx` and render `FieldDef.description` through it

Runtime field descriptions must appear on **both hover and focus** (acceptance: "字段 tooltip 在 hover/focus 时显示"). No tooltip primitive or radix dep exists today.

- **Alternative A**: Native `title` attribute. Rejected — hover-only; does not fire on keyboard focus, failing the acceptance criterion, and cannot be styled.
- **Alternative B**: Reuse `popover.tsx`. Rejected — popover is click-triggered and visually heavy for a one-line hint.
- **Alternative C (chosen)**: Add a minimal dependency-free `Tooltip` (CSS + tiny state machine, or `aria-describedby` + visible-on-hover/focus) to `shared/ui/components/`. Extend `InputField` to accept `description` and wrap its label in the tooltip. Drive the render from the existing `FIELDS` array (including label/unit) to eliminate the current `maxConcurrent` "sessions" vs "agents" drift.

**Rationale**: A reusable Tooltip also serves the issue's Tech Debt goal ("缺少空状态/引导的统一模式" — establishes a shared primitive). Keeping it dependency-free respects the no-new-deps spirit of the proposal. Updating the render to consume `FIELDS` directly prevents the drift class of bug from recurring.

**Retry rename**: Update the `maxGracePeriods` `FieldDef` to `label: 'Retry attempts'`, `unit: 'times'`, and refine descriptions for `maxConcurrent` ("upper bound is constrained by runner capacity shown in the sidebar (active/max); excess tasks queue") and `pollInterval` ("shorter = more realtime but higher CPU/network"). The internal field key `maxGracePeriods` is unchanged — only the user-facing label/unit change, so no API/config-key impact.

### Decision 6: Hide the Settings nav entry when `currentProject` is null

- **Alternative A**: Keep visible but disabled with a "Select a project first" tooltip. Rejected — adds a disabled-state affordance that needs the new Tooltip primitive and still lets users see an entry they cannot use.
- **Alternative B (chosen)**: Filter the Settings item out of `configureNav` when `useProject().currentProject` is null. Direct-URL access to `/settings` still works via the existing `ProjectGuard` bypass (line 19) — that edge case is out of scope.

**Rationale**: Simplest, matches the existing philosophy that Settings is project-scoped (the Repositories tab already degrades to "No project selected"), and avoids surfacing a dead control. The `Logs` entry stays unchanged.

## Risks / Trade-offs

- **[Stage chips become stale if a future workflow profile diverges]** -> Mitigation: the constant is isolated and documented; Issue C/D can swap it for per-profile data once the backend list endpoint is extended. Acceptable given all current profiles share stages.
- **[CSS line-clamp measurement can flash on first paint]** -> Mitigation: default to clamped state (no layout shift when the toggle appears); measure in `useLayoutEffect`. Worst case the toggle appears a frame late — purely cosmetic.
- **[localStorage is per-browser and unversioned]** -> Mitigation: use a dedicated key (`mohist:settings-onboarding-dismissed`) rather than overloading an existing one; clearing browser storage intentionally re-triggers onboarding, which the spec treats as desired behavior. If we later need per-project dismissal, the key can be namespaced `mohist:settings-onboarding-dismissed:<projectId>` without a migration.
- **[Adding a Tooltip primitive risks scope creep]** -> Mitigation: keep it minimal (hover + focus, no animation library, <100 LOC); it is a prerequisite for the hover/focus acceptance criterion, not gold-plating.
- **[Driving Runtime render from `FIELDS` changes existing labels/units the user may have grown used to]** -> Mitigation: only the explicitly-approved rename (`Retry Budget` → `Retry attempts`) is a user-visible label change; the `maxConcurrent` unit normalization (`sessions` vs `agents`) is a pre-existing bug fix. No acceptance test asserts the old strings.
- **[Hiding the Settings nav entry may surprise users who remember it being there]** -> Mitigation: it only hides when no project is selected (a transient state); once a project is chosen it reappears. The `ProjectSwitcher` already steers users toward selecting/creating a project first.

## Migration Plan

This is a presentation-only change with no data, API, or persistence migrations.

**Roll out (single frontend PR):**
1. Add `Tooltip` primitive to `shared/ui/components/tooltip.tsx`.
2. Update `WorkflowProfilesSection` (stage chip constant, description clamp + Read more; concept paragraph already present — verify only).
3. Update `RepositoriesSection` (empty-state CTA in `SectionState` children, conditional form rendering, focus handoff).
4. Add `OnboardingBanner`; wire into `SettingsPage` for `section === 'ai'` with `localStorage` persistence.
5. Update `AgentSettingsSection` (`InputField` consumes `description` via Tooltip; drive render from `FIELDS`; rename Retry field; refresh descriptions).
6. Update `AppSidebar` to hide Settings when `currentProject` is null.
7. Update/add tests (see below).

**Test gates:**
- `SettingsPage.test.tsx` must continue to pass.
- New tests: empty-state CTA render + focus handoff; onboarding banner first-visit render, dismiss persistence, re-trigger after `localStorage.clear()`; stage chips present on profile cards; "Read more" toggle appears only for overflowing descriptions; Runtime field labels/units (`Retry attempts` / `times`); Tooltip shows description on hover/focus.

**Rollback:** Revert the single PR. No data cleanup required — the `localStorage` key is benign if left behind (worst case: a user who dismissed onboarding sees it again after rollback).

## Open Questions

- **Stage chip source (Decision 1)**: Is hardcoding `DEFAULT_WORKFLOW_STAGES` acceptable for now, or should we relax the no-backend constraint to add `stages: string[]` to the list endpoint? Default assumption: hardcode (per proposal constraint); confirm with Issue C/D owners if they plan to extend the endpoint.
- **Onboarding scope**: Should the banner be dismissed globally (`mohist:settings-onboarding-dismissed`) or per-project? Default assumption: global — onboarding is a one-time learning cue, not project-specific state.
- **Tooltip primitive scope**: Should this Tooltip be promoted to the shared design-system layer for Issue C/D reuse, or stay local to Settings for now? Default assumption: place in `shared/ui/components/` (the natural home) but keep the API minimal so it can be extended later.
