## Why

The backend already supports a project-level default workflow template (`ProjectWorkflowProfile.DefaultTemplateId` exposed via `GET/PUT/DELETE /api/projects/{projectRef}/workflow-profile/default-template`), but Settings → Workflows is a read-only system catalog and never reads or writes it. Users cannot configure the project default from the UI, so every new issue that should inherit a non-default policy requires a per-issue override. Worse, the static `Default` badge on `mohist/default` describes system-default metadata but is presented identically to a project default, so users cannot tell which workflow their project actually inherits.

## What Changes

- Add a **Project default workflow** control at the top of Settings → Workflows that reads `GET /api/projects/{projectRef}/workflow-profile` and answers "what will new issues inherit for this project?".
- Let the user select a system workflow (e.g. `mohist/github-pr`) by writing `PUT /api/projects/{projectRef}/workflow-profile/default-template`, with readback confirming the new `defaultTemplateId`.
- Let the user clear the project default via `DELETE /api/projects/{projectRef}/workflow-profile/default-template`; the UI then explains the project is inheriting the system default.
- Visually separate **system default** metadata (static `isDefault` on `mohist/default`) from **this project's current default** state, renaming or re-styling the badge so it cannot be mistaken for the project default.
- Stop hardcoding `workflowProfiles.find((p) => p.isDefault)` as the only default in create-issue / profile-selection surfaces; honor a configured project default when present.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `web-ui`: Settings → Workflows gains a project default workflow control (read/select/clear) sourced from `GET/PUT/DELETE /api/projects/{projectRef}/workflow-profile/default-template`, the system-default and project-default states are visually distinguished, and create-issue/profile-selection surfaces resolve the default from the project configuration rather than a hardcoded `isDefault` lookup.

## Impact

- **Web (`packages/web`)**:
  - `pages/settings/ui/WorkflowProfilesSection.tsx` — add project default readback + select/clear control above the catalog.
  - `entities/settings` (or `entities/project`) API client + TanStack Query hooks/mutations for the project workflow-profile default-template endpoints.
  - `features/create-issue/ui/CreateIssueDialog.tsx` — replace the hardcoded `find((p) => p.isDefault)` default with the configured project default (falling back to system default when unset).
- **Backend (`packages/server`)**: No change required — the `workflow-profile` and `default-template` endpoints already exist in `ProjectRoutes.cs` and operate on `ProjectWorkflowProfile.DefaultTemplateId`.
- **Tests**: Web tests covering readback, switching to `mohist/github-pr`, clearing the default, and the system-vs-project default distinction.
- **Out of scope (per Non-Goals)**: workflow execution semantics, resolver precedence, new profile types, built-in YAML edits, and unrelated workflow-profile read-model bugs.
