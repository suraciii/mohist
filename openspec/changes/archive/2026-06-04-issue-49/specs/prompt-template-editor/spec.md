## ADDED Requirements

### Requirement: Settings → Templates tab is a top-level navigation entry

The Web UI SHALL expose a new `Templates` tab in the Settings page, alongside the existing `Project`, `System` (Coder Agent / Runtime / Repositories / Workflows / System) tabs. The tab SHALL be reachable via `/settings/templates` and SHALL follow the existing settings routing convention.

#### Scenario: Settings page renders a Templates tab

- **WHEN** a user opens `/settings/templates`
- **THEN** the Settings page renders the Templates tab as the active tab
- **AND** the tab list shows `Templates` alongside the other Settings tabs

#### Scenario: Templates tab URL is routable

- **WHEN** a user navigates to `/settings/templates` directly
- **THEN** the page loads the Templates section
- **AND** the URL does not redirect to another tab

#### Scenario: Templates tab requires a project context

- **WHEN** no project is selected and the user opens the Templates tab
- **THEN** the section shows a "No project selected" placeholder
- **AND** no API requests to `/api/projects/{id}/templates` are issued

### Requirement: Web API hooks cover templates list, override, delete, preview, and extract-variables

The Web UI SHALL provide 5 React Query hooks for the new template management endpoints, all keyed off the current project. The hooks SHALL follow the existing `entities/<feature>/api/queries.ts` + `client.ts` convention.

| Hook | Endpoint | Method |
|------|----------|--------|
| `useProjectTemplates(projectId)` | `/api/projects/{id}/templates` | GET |
| `useProjectTemplateOverride(projectId, key)` | `/api/projects/{id}/templates/{key}/override` | GET |
| `useUpsertProjectTemplateOverride(projectId)` | `/api/projects/{id}/templates/{key}/override` | PUT |
| `useDeleteProjectTemplateOverride(projectId)` | `/api/projects/{id}/templates/{key}/override` | DELETE |
| `usePreviewProjectTemplate(projectId, key)` | `/api/projects/{id}/templates/{key}/preview` | POST |

The system-template list (`/api/templates/system`) MAY be fetched in the same component but is not a mutation hook.

#### Scenario: useProjectTemplates returns effective templates

- **WHEN** a component calls `useProjectTemplates(projectId)` for a project that has 2 system templates and 1 override
- **THEN** the hook calls `GET /api/projects/{id}/templates`
- **AND** returns an array of effective templates with their `source` field

#### Scenario: useUpsertProjectTemplateOverride sends PUT

- **WHEN** a component calls `useUpsertProjectTemplateOverride(projectId).mutate({ key, displayName, description, tags, stage, body })`
- **THEN** the hook sends `PUT /api/projects/{id}/templates/{key}/override` with the payload
- **AND** invalidates the `project-templates` and `project-template-{key}` queries on success
- **AND** surfaces API errors via toast

#### Scenario: useDeleteProjectTemplateOverride sends DELETE

- **WHEN** a component calls `useDeleteProjectTemplateOverride(projectId).mutate(key)`
- **THEN** the hook sends `DELETE /api/projects/{id}/templates/{key}/override`
- **AND** invalidates the templates list on success

#### Scenario: usePreviewProjectTemplate sends POST

- **WHEN** a component calls `usePreviewProjectTemplate(projectId, key).mutate({ variables })`
- **THEN** the hook sends `POST /api/projects/{id}/templates/{key}/preview` with `{ variables }`
- **AND** returns `{ rendered, missingVariables, depth }`

### Requirement: List view shows templates with search, source label, and actions

The Templates list view SHALL show one row per template, with columns for `key`, `stage` (badge), `tags`, `displayName`, and a `source` label. The API returns `source` as `system` | `project-override` | `project-new`; the list view SHALL transform these into display labels `system`, `projectⓘ` (overridden system key), and `projectⓘ new` (project-unique key), each with a hover/tap hint explaining the difference. A search box SHALL filter rows by `key`, `displayName`, `tag`, or `description`. Each row SHALL expose `Override`, `Edit`, `Preview`, `Reset` (only for overridden system keys), and `Delete` (only for project rows) actions. A `+ New Template` button SHALL open the New Template dialog.

#### Scenario: List view renders one row per effective template

- **WHEN** a project has 12 effective templates (12 system, no overrides)
- **THEN** the list shows 12 rows
- **AND** each row is labeled `system`

#### Scenario: Source label distinguishes override vs new

- **WHEN** a project has 1 override for `proposal` and 1 new project template `deploy-checklist`
- **THEN** the `proposal` row's source label is `projectⓘ` with hover text explaining the override replaces the system body
- **AND** the `deploy-checklist` row's source label is `projectⓘ new` with hover text explaining the key is project-unique

#### Scenario: Search filters by key/name/tag/description

- **WHEN** a user types `plan` in the search box
- **THEN** the list is filtered to rows whose `key`, `displayName`, `tags`, or `description` contain `plan`
- **AND** clearing the search restores the full list

#### Scenario: Override action opens the editor with project body

- **WHEN** a user clicks `Override` on a system row
- **THEN** the editor opens for that key with the system body pre-filled
- **AND** the `Save` action will create a project override

#### Scenario: Reset action removes the override

- **WHEN** a user clicks `Reset` on a `projectⓘ` row
- **THEN** the client sends `DELETE /api/projects/{id}/templates/{key}/override`
- **AND** the row's source reverts to `system` in the list

#### Scenario: Delete action is only available on project rows

- **WHEN** the list is rendered
- **THEN** the `Delete` action is only shown on `projectⓘ` and `projectⓘ new` rows
- **AND** `Delete` is hidden on `system` rows

#### Scenario: New Template button opens the new template dialog

- **WHEN** a user clicks `+ New Template`
- **THEN** the New Template dialog opens
- **AND** the dialog collects `key`, initial `displayName`, and initial `body`
- **AND** submitting creates a `projectⓘ new` row

### Requirement: Editor view renders metadata form and live preview side-by-side

The Template editor SHALL be a two-pane layout: the left pane collects `key` (read-only after create), `displayName`, `description`, `tags`, `stage`, and `body` (textarea or Markdown editor); the right pane shows a live preview rendered against user-editable variables and a checklist of referenced variables with their availability (`✓` resolved / `✗` missing). The editor SHALL expose `Save`, `Reset`, and `Cancel` actions.

#### Scenario: Editor opens with current values populated

- **WHEN** a user opens the editor for an existing template
- **THEN** all metadata fields are populated from the current effective template
- **AND** the body textarea shows the current effective body
- **AND** the `key` field is read-only

#### Scenario: Preview pane lists referenced variables

- **WHEN** the body contains `${{ openspecChangeDir }}` and `${{ issue.number }}`
- **THEN** the preview pane lists both variables
- **AND** marks `✓` for variables present in the user-supplied preview variables
- **AND** marks `✗` for variables missing from the preview variables

#### Scenario: Preview re-renders on variable change

- **WHEN** a user edits the preview variables
- **THEN** the rendered output updates to reflect the new values
- **AND** the variable availability list updates accordingly

#### Scenario: Preview defaults include a representative sample

- **WHEN** the editor first opens
- **THEN** the preview variables are pre-filled with a representative sample (e.g. `openspecChangeDir`, `issue: { number, title }`, `project: { id, name }`, `mohist: { system }`)
- **AND** the user can edit them freely

#### Scenario: Save persists the override

- **WHEN** a user clicks `Save`
- **THEN** the client sends `PUT /api/projects/{id}/templates/{key}/override` with the current form values
- **AND** on success, the list view re-fetches and the editor closes

#### Scenario: Reset reverts unsaved edits

- **WHEN** a user has edited the form and clicks `Reset`
- **THEN** the form reverts to the values present when the editor was opened
- **AND** no API request is sent

#### Scenario: Cancel closes the editor without saving

- **WHEN** a user clicks `Cancel`
- **THEN** the editor closes
- **AND** any unsaved local edits are discarded
- **AND** no API request is sent

### Requirement: New Template dialog creates a project-unique key

The New Template dialog SHALL collect `key`, an initial `displayName`, and an initial `body`. On submit, the client SHALL call `PUT /api/projects/{id}/templates/{key}/override` with the provided values, then close the dialog and refresh the list. The dialog SHALL reject empty `key` or `body` values.

#### Scenario: Submit creates a new project template

- **WHEN** a user enters `deploy-checklist`, displayName `Deploy Checklist`, body `Run checks` and clicks Create
- **THEN** the client sends `PUT /api/projects/{id}/templates/deploy-checklist/override` with `{ displayName, description: "", tags: [], stage: null, body }`
- **AND** the dialog closes
- **AND** the new row appears in the list as `projectⓘ new`

#### Scenario: Empty key is rejected

- **WHEN** a user submits the dialog with an empty `key`
- **THEN** the dialog shows a validation error
- **AND** no API request is sent

#### Scenario: Empty body is rejected

- **WHEN** a user submits the dialog with an empty `body`
- **THEN** the dialog shows a validation error
- **AND** no API request is sent

### Requirement: Editor warns on rename-like key changes

Because the API treats `key` as immutable, the editor SHALL render a visible warning whenever the user changes the `key` field (only possible in the New Template dialog), stating that renaming requires delete + create and that workflow YAML references to the old key will break.

#### Scenario: New template key change shows warning

- **WHEN** a user edits `key` in the New Template dialog
- **THEN** the dialog shows a warning about breaking workflow YAML references if the key is renamed after creation
- **AND** the warning is dismissed once the user closes the dialog
