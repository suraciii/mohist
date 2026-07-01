## Why

Every project sees the full system workflow catalog (`mohist/local`, `mohist/github-pr`, …) even when the operator never uses some profiles, so the `mohist-create-issue` agent and `mo workflow list --described` always offer the whole menu and the operator cannot curate what their project actually runs. At the same time, the Settings > Workflows tab currently shows inaccurate information (hardcoded stage chips, an unsearchable empty registry, and a native `<select>`), so the page that would govern this curation is not trustworthy today.

## What Changes

- Add a **per-project disabled-profile blacklist** (default empty = all enabled) on `ProjectWorkflowProfile`. The system default `mohist/local` is **not** specially protected — disabling it is the explicit way to exclude an unused system profile.
- The workflow **discovery surface** (`/api/workflow-templates/system`, `/api/workflow-profiles`, `mo workflow list --described`, and the agent-facing candidate list) SHALL filter out disabled profiles for the target project before returning.
- Enforce the **Option A invariant**: every project SHALL keep at least one enabled profile. Disabling the last enabled profile SHALL be rejected at the action boundary with a clear consequence message; if a project somehow has zero enabled profiles, issue creation SHALL be rejected with an actionable "enable a workflow first" error instead of silently falling back.
- The effective-profile resolution cascade (issue-custom → project-default → system-default) SHALL **skip disabled profiles**. `mohist/local` is no longer an unconditional fallback — the fallback is "the first enabled profile, or a creation-blocking error".
- Web UI Settings > Workflows tab:
  - Each profile card renders the profile's **real `profile.stages`** (no more hardcoded `['plan','build','check','integrate']`).
  - Each profile card has a base-ui **`Switch` toggle (with `aria-label`)** to enable/disable for the current project; attempting to disable the last enabled profile is blocked inline.
  - The project-default control uses the project's existing base-ui **`Select` primitive** (not a native `<select>`); disabled items get a clear visual distinction (greyed/disabled) and the dropdown surfaces an **amber warning** when the current default points at a disabled profile.
  - Workflow-related entries are registered in **Settings Search** so the tab is discoverable.
- The bundled skill `mohist-create-issue` SHALL update its "mohist/local is the unconditional fallback" wording to reflect the new fallback semantics ("first enabled profile, else fail with an actionable error").

## Capabilities

### New Capabilities

- `workflow-profile-discovery`: The per-project filtered view of the system workflow profile catalog. Covers the disabled-profile blacklist on `ProjectWorkflowProfile` (default empty, `mohist/local` not specially protected), project-scoped discovery that filters the system catalog before returning across the HTTP discovery endpoints (`/api/workflow-templates/system`, `/api/workflow-profiles`), the CLI `mo workflow list --described` (which backs the `mohist-create-issue` agent candidate list), and the "at least one enabled profile per project" invariant enforced both on the disable action and on the issue-creation path.

### Modified Capabilities

- `issue-workflow-profile`: The "Single source of truth for issue workflow profile" requirement's default-resolution rule changes — disabled profiles SHALL be skipped in the project-default → system-default cascade, and `mohist/local` is no longer an unconditional fallback. The "No issue-level selection inherits default" scenario gains the precondition that at least one enabled profile exists; when none exists, creation SHALL be rejected with an actionable error rather than resolving to a disabled default.
- `web-ui`: The Settings > Workflows tab gains requirements — profile cards SHALL render the profile's real `stages` (not a hardcoded set); each card SHALL expose a base-ui `Switch` with `aria-label` to toggle per-project enable/disable and SHALL block the last-enabled disable inline; the project-default control SHALL use the project's base-ui `Select` primitive (not native `<select>`), visually distinguish disabled items, and show an amber warning when the current default points at a disabled profile; workflow entries SHALL be registered in Settings Search.

## Impact

- **Server** (`packages/server/src/Mohist.Server/`):
  - `Infrastructure/Data/Workflow/ProjectWorkflowProfileRow.cs`: add disabled-profile-id list column; one EF Core migration.
  - `Workflow/Services/ProjectWorkflowProfileManager.cs`: discovery endpoints take project context and filter by the disabled set; new disable/enable write API with the last-enabled invariant.
  - `Api/SystemRoutes.cs`, `Api/ProjectRoutes.cs`: `/api/workflow-templates/system` and `/api/workflow-profiles` become project-scoped (accept project id/ref) and filtered.
  - Issue creation path (`Issue/`): pre-flight check that rejects creation when no profile is enabled for the project.
- **CLI** (`packages/cli/Mohist.Cli/`): `mo workflow list --described` resolves the current project and consumes the filtered endpoint; no new verbs.
- **Skill data** (`packages/cli/Mohist.Cli/skill-data/mohist-create-issue/`): update the fallback wording to "first enabled profile, else fail".
- **Web** (`packages/web/src/pages/settings/ui/`):
  - `WorkflowProfilesSection.tsx`: replace `DEFAULT_WORKFLOW_STAGES` with real `profile.stages`; populate `WORKFLOW_DESCRIPTORS` for Settings Search; add a `Switch` toggle per card.
  - `ProjectDefaultWorkflowControl.tsx`: replace native `<select>` with base-ui `Select`; add disabled-item styling and amber warning; the `Switch` primitive needs adding to `shared/ui` if absent.
- **Tests**: server-side filtering by disabled set; last-enabled disable rejection; issue-creation rejection when none enabled; UI stages render from real data; Switch toggle behavior; Settings Search entries; default-dropdown warning state.
- **No changes** to workflow execution, runner, or the existing `workflow-profile-resolution` (grain-level) and `pr-first-workflow` (profile content) contracts.
