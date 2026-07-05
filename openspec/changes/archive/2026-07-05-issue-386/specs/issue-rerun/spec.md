### Requirement: `mo issue rerun` accepts a `--from-stage` flag

`mo issue rerun <number>` SHALL accept a `--from-stage <stage>` flag. When the flag is absent, the command SHALL POST to `/api/projects/{projectId}/issues/{number}/rerun` with an empty body (rerun the whole workflow from the start). When the flag is present and non-empty, the command SHALL POST `{ stage: <stage> }` to `/api/projects/{projectId}/issues/{number}/rerun-from-stage` (invalidate the target stage and all later stages, creating new attempts). The command SHALL honor `--project` / `--project-id` scoping identical to other issue lifecycle commands.

#### Scenario: Rerun without --from-stage reruns from the start

- **WHEN** a caller runs `mo issue rerun <number>` against a resolved project
- **THEN** the CLI SHALL POST an empty body to `/api/projects/{projectId}/issues/{number}/rerun`

#### Scenario: Rerun with --from-stage reruns from the named stage

- **WHEN** a caller runs `mo issue rerun <number> --from-stage <stage>` with a non-empty stage
- **THEN** the CLI SHALL POST a body `{ "stage": "<stage>" }` to `/api/projects/{projectId}/issues/{number}/rerun-from-stage`

#### Scenario: Empty --from-stage fails locally

- **WHEN** a caller runs `mo issue rerun <number> --from-stage ""` (or whitespace)
- **THEN** the CLI SHALL print a `--from-stage is required and must not be empty` error to stderr and exit non-zero without sending a request

#### Scenario: Project scoping honors --project / --project-id

- **WHEN** a caller runs `mo issue rerun <number> --from-stage <stage> --project-id <id>`
- **THEN** the CLI SHALL POST to `/api/projects/<id>/issues/<number>/rerun-from-stage`

### Requirement: `rerun-from-stage` is a transitional alias of `rerun --from-stage`

`mo issue rerun-from-stage <number> --stage <stage>` SHALL be retained as a transitional alias with identical behavior to `mo issue rerun <number> --from-stage <stage>` (same endpoint, same body). The alias exists solely to keep scripts written against the prior surface working; it MUST NOT diverge in behavior from `rerun --from-stage`.

#### Scenario: Alias behaves identically to `rerun --from-stage`

- **WHEN** a caller runs `mo issue rerun-from-stage <number> --stage <stage>`
- **THEN** the CLI SHALL POST the same body to the same `/rerun-from-stage` endpoint as `mo issue rerun <number> --from-stage <stage>` and produce the same exit code
