### Requirement: Runner command surface is read-only over Server-registered resources

The `runner` command group SHALL expose only `list`, `view`, and `status`. These commands SHALL read exclusively from the Server-registered Runner resources — presence, capacity, and heartbeat — over the Server API. The `runner` group MUST NOT host any local managed-service lifecycle verb (`start`, `stop`, `restart`, `service-status`, `logs`, `uninstall`).

#### Scenario: Runner help lists only read subcommands
- **WHEN** the user runs `mo runner --help`
- **THEN** the listed subcommands include `list`, `view`, and `status`, and MUST NOT include `start`, `stop`, `restart`, `service-status`, `logs`, or `uninstall`

#### Scenario: Runner list queries the registered-runner resource endpoint
- **WHEN** the user runs `mo runner list`
- **THEN** the CLI issues `GET /api/projects/<project>/runners` against the connected Server and renders the returned runners, without invoking any local service manager

### Requirement: Single-runner detail is reached via `view`

The single-runner detail read SHALL be `mo runner view <runner-id>`, querying `GET /api/projects/<project>/runners/<runner-id>` on the connected Server. The former `runner show` verb MUST be removed and MUST NOT resolve (no alias retained).

#### Scenario: Runner view reads a single runner from the Server
- **WHEN** the user runs `mo runner view r-busy`
- **THEN** the CLI issues `GET /api/projects/<project>/runners/r-busy` and renders the runner detail

#### Scenario: Legacy runner show no longer resolves
- **WHEN** the user runs `mo runner show r-1`
- **THEN** the command does not resolve, exits non-zero, and issues no HTTP request

### Requirement: Remote runner facts are independent of local service state

`runner view` and `runner status` SHALL report Server-known Runner resource facts regardless of whether the local Runner managed service is running. The CLI MUST NOT fabricate Runner resource state from local service-manager status; the only source of Runner resource facts is the Server.

#### Scenario: Local runner service stopped still reports remote resource facts
- **WHEN** the local Runner managed service is stopped and the user runs `mo runner view r-1`
- **THEN** the CLI reports the Server-known facts for `r-1` and MUST NOT substitute local service-manager status for Runner resource state

### Requirement: Runner reads resolve Project via the shared contract

`runner list`, `runner view`, and `runner status` SHALL resolve the target Project through the shared `--project <name-or-id>` contract or the active project, exactly one. When no Project can be resolved they MUST fail with the standard no-active-project guidance and issue no API call. The runner command surface MUST NOT accept `--project-id`.

#### Scenario: Explicit project override resolves and is queried
- **WHEN** the user runs `mo runner list --project proj_other`
- **THEN** the CLI resolves `proj_other` and issues the read against `/api/projects/proj_other/runners`

#### Scenario: No active project fails with guidance
- **WHEN** no project is active and the user runs `mo runner status` without `--project`
- **THEN** the CLI exits non-zero, prints guidance to run `mo project use`, and issues no HTTP request
