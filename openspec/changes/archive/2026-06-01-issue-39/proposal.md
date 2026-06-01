## Why

Users can configure project-level workflow YAML today, but they cannot inspect or adjust the workflow profile snapshot that a specific issue will actually run with. This makes it hard to tailor a single issue before starting work and impossible to safely evolve an issue's future stages without mutating the global default or regenerating already-initialized stage work.

## What Changes

- Add an issue-scoped workflow profile YAML editor to the issue detail page, with view, edit, dirty-state, save-progress, and validation-error feedback before workflow start.
- Add an issue-scoped REST API for reading and saving workflow profile YAML at `/api/issues/{number}/workflow/profile/yaml`.
- Parse submitted YAML into a normalized `WorkflowDefinition`, return clear YAML and workflow-shape validation errors, and persist the normalized issue workflow profile snapshot.
- When an issue already has an active workflow run, update the run's workflow profile snapshot so future uninitialized stages use the new definition.
- Preserve already-initialized `StageRun` tasks and checks when saving updated YAML; only later stage initialization uses the edited definition.

## Capabilities

### New Capabilities
- `issue-workflow-profile`: Issue-scoped workflow profile snapshot viewing and editing, including YAML validation, persistence, and active-run profile synchronization.

### Modified Capabilities
- `web-ui`: Issue detail page requirements expand to include workflow profile YAML editing UX and safe refresh after save.
- `http-api`: Issue APIs expand to expose issue-scoped workflow profile YAML read/save endpoints and validation error responses.
- `workflow-definition`: Workflow definitions expand from project/global loading to support normalized issue profile snapshots derived from submitted YAML.
- `workflow-run`: Active workflow runs expand to synchronize updated profile snapshots for future uninitialized stages while preserving initialized stage work.

## Impact

- Affected code spans `packages/web` issue detail workflow UI, API client/hooks, and save-state handling.
- Affected server code spans issue workflow profile endpoints, YAML parsing/normalization, issue persistence, and active `WorkflowRun` synchronization.
- Affected persistence includes the stored `IssueWorkflowProfile` snapshot and active run profile records.
- Tests need coverage for backlog issue editing, invalid YAML rejection, active-run profile sync, and next-stage initialization behavior using fake external systems.
