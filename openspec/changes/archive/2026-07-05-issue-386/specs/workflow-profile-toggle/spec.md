### Requirement: `mo project workflow profile enable` toggles a profile on

The CLI SHALL provide `mo project workflow profile enable <profileId>` as the canonical entry point for enabling a workflow profile. The command SHALL POST `{ profileId }` to the existing `/api/projects/{projectId}/workflow-profile/enable` endpoint. `<profileId>` SHALL be a required positional argument. The project scope SHALL be resolved via `--project` / `--project-id` (active project when neither is given), and an unresolvable project SHALL fail locally without sending a request.

#### Scenario: Enable posts the profile id to the enable endpoint

- **WHEN** a caller runs `mo project workflow profile enable <profileId>` against a resolved project
- **THEN** the CLI SHALL POST a JSON body `{ "profileId": "<profileId>" }` to `/api/projects/{projectId}/workflow-profile/enable`

#### Scenario: Missing profile id fails locally

- **WHEN** a caller runs `mo project workflow profile enable` with no profile id
- **THEN** the CLI SHALL fail locally with a non-zero exit without sending a request

#### Scenario: Unknown profile surfaces the server error verbatim

- **WHEN** the server responds to the enable POST with a `400` carrying code `unknown_workflow_profile`
- **THEN** the CLI SHALL print the error message and code to stderr and exit non-zero

#### Scenario: Project scoping honors --project / --project-id

- **WHEN** a caller runs `mo project workflow profile enable <profileId> --project-id <id>`
- **THEN** the CLI SHALL POST to `/api/projects/<id>/workflow-profile/enable`

### Requirement: `mo project workflow profile disable` toggles a profile off

The CLI SHALL provide `mo project workflow profile disable <profileId>` as the canonical entry point for disabling a workflow profile. The command SHALL POST `{ profileId }` to the existing `/api/projects/{projectId}/workflow-profile/disable` endpoint. `<profileId>` SHALL be a required positional argument. The command SHALL faithfully surface the server's guard errors: `unknown_workflow_profile` when the profile does not exist, and `last_enabled_workflow_profile` when disabling would leave the project with no enabled profile.

#### Scenario: Disable posts the profile id to the disable endpoint

- **WHEN** a caller runs `mo project workflow profile disable <profileId>` against a resolved project
- **THEN** the CLI SHALL POST a JSON body `{ "profileId": "<profileId>" }` to `/api/projects/{projectId}/workflow-profile/disable`

#### Scenario: Disabling the last enabled profile surfaces the guard error

- **WHEN** the server responds to the disable POST with a `400` carrying code `last_enabled_workflow_profile`
- **THEN** the CLI SHALL print the error message and code to stderr and exit non-zero

#### Scenario: Unknown profile on disable surfaces the server error

- **WHEN** the server responds to the disable POST with a `400` carrying code `unknown_workflow_profile`
- **THEN** the CLI SHALL print the error message and code to stderr and exit non-zero

#### Scenario: Missing profile id fails locally

- **WHEN** a caller runs `mo project workflow profile disable` with no profile id
- **THEN** the CLI SHALL fail locally with a non-zero exit without sending a request
