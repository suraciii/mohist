## ADDED Requirements

### Requirement: CLI provides mo system info command

The CLI SHALL provide `mo system info` as a top-level read-only command that sends `GET /api/system/info` and renders the server's system diagnostics, surfacing the identity (version, git hash, started-at), source (path/branch/head/dirty), install (mode/service-manager/units), update (status/availability), services (server/runner service state), and paths (db/config/logs/opencode) sections returned by the endpoint. The command SHALL accept `-o table|json`. In `-o json` mode the CLI SHALL print the raw server response payload without omission. The command SHALL be distinct from the client-local `mo info` command (which reports the CLI binary's own environment and install source), and `mo system info --help` SHALL disambiguate the two data sources. The `GET /api/system/info` endpoint is global and SHALL NOT require project resolution.

#### Scenario: Table mode renders all diagnostic sections

- **WHEN** the user runs `mo system info -o table`
- **AND** the server returns the full system info payload
- **THEN** the CLI SHALL send `GET /api/system/info`
- **AND** the rendered output SHALL present the identity, source, install, update, services, and paths sections

#### Scenario: JSON mode emits the raw payload

- **WHEN** the user runs `mo system info -o json`
- **THEN** the CLI SHALL print the raw server response payload as JSON

#### Scenario: Server unreachable degrades gracefully

- **WHEN** the user runs `mo system info`
- **AND** the `GET /api/system/info` request fails because the server is not running
- **THEN** the CLI SHALL print a "server not running" notice
- **AND** the CLI SHALL print any locally-derivable diagnostic subset (for example CLI version or install source)
- **AND** the CLI SHALL NOT abort with only a hard error and no diagnostic output

#### Scenario: Help disambiguates from client-local mo info

- **WHEN** the user runs `mo system info --help`
- **THEN** the output SHALL explain that `mo system info` reports server-side system diagnostics
- **AND** SHALL distinguish it from `mo info` which reports the CLI's own local environment

### Requirement: CLI provides mo opencode models command

The CLI SHALL provide `mo opencode models` as a top-level read-only command that lists the available coder model IDs by sending `GET /api/projects/:projectId/opencode/models`. Because the endpoint is project-scoped, the command SHALL resolve the target project via `--project`/`--project-id` (or the active-project fallback), identical to other project-scoped commands. In `-o table` mode the CLI SHALL print exactly one model ID per line so the output can be copied directly into a `--model` flag value. In `-o json` mode the CLI SHALL print the raw server payload, preserving both the `models` array and any model-variant information the server returns. The command SHALL accept `-o table|json`.

#### Scenario: Table mode lists one model ID per line

- **WHEN** the user runs `mo opencode models -o table`
- **AND** the server returns `models: ["anthropic/claude-sonnet", "openai/gpt-5"]`
- **THEN** the CLI SHALL send `GET /api/projects/:projectId/opencode/models`
- **AND** the output SHALL print `anthropic/claude-sonnet` and `openai/gpt-5` on separate lines with no extra per-row decoration

#### Scenario: JSON mode emits the raw payload

- **WHEN** the user runs `mo opencode models -o json`
- **THEN** the CLI SHALL print the raw server payload including the `models` array and any model-variant fields

#### Scenario: Project resolution is required

- **WHEN** the user runs `mo opencode models` with no resolvable project (no `--project`/`--project-id` and no active project)
- **THEN** the CLI SHALL print a clear error explaining no project is resolved
- **AND** SHALL exit with a non-zero status

### Requirement: CLI provides mo runner status online diagnostic command

The CLI SHALL provide `mo runner status` as a read-only command that sends `GET /api/projects/:projectId/runners` and renders a focused online-runner summary: each runner's identifier, last heartbeat timestamp, and idle/busy state (idle when used capacity is zero, busy when used capacity is non-zero). Because the endpoint is project-scoped, the command SHALL resolve the target project via `--project`/`--project-id`. The command SHALL accept `-o table|json`; in `-o json` mode the CLI SHALL print the raw server payload. The command SHALL focus on the online/heartbeat/idle summary and SHALL remain distinct from `mo runner list`, which renders the full-detail runner table (kind/scope/capacity/hostname, etc.).

#### Scenario: Table mode renders online runner summary

- **WHEN** the user runs `mo runner status -o table`
- **AND** the server returns one online idle runner and one online busy runner
- **THEN** the CLI SHALL send `GET /api/projects/:projectId/runners`
- **AND** the rendered output SHALL show each runner's identifier, last heartbeat, and idle/busy state

#### Scenario: JSON mode emits the raw payload

- **WHEN** the user runs `mo runner status -o json`
- **THEN** the CLI SHALL print the raw server runner-status payload

#### Scenario: No runners connected

- **WHEN** the user runs `mo runner status`
- **AND** the server returns an empty runner list
- **THEN** the CLI SHALL report that no runners are connected
- **AND** SHALL exit with status 0

#### Scenario: Project resolution is required

- **WHEN** the user runs `mo runner status` with no resolvable project
- **THEN** the CLI SHALL print a clear error explaining no project is resolved
- **AND** SHALL exit with a non-zero status

### Requirement: CLI runner service-status preserves the service-lifecycle status verb

To resolve the `mo runner status` naming collision in favor of the online-runner diagnostic, the pre-existing service-lifecycle status verb (which reports systemd/scheduled-task unit status for the runner) SHALL be renamed from `mo runner status` to `mo runner service-status`. The renamed `mo runner service-status` command SHALL behave identically to the former `mo runner status` service-lifecycle verb (same flags, same `--dry-run`/`--unit-dir` options, same underlying service-installer status action). The `mo runner --help` output SHALL list `service-status` (not `status`) as the service-lifecycle status command, and SHALL list `status` as the online-runner diagnostic command. This is a breaking rename of the prior `mo runner status` invocation.

#### Scenario: Service-lifecycle status is available under the new name

- **WHEN** the user runs `mo runner service-status`
- **THEN** the CLI SHALL invoke the same service-installer status action that the former `mo runner status` service-lifecycle verb invoked
- **AND** SHALL accept the same `--dry-run` and `--unit-dir` options as the other service-lifecycle verbs

#### Scenario: Runner help lists both verbs with distinct descriptions

- **WHEN** the user runs `mo runner --help`
- **THEN** the output SHALL list `status` described as the online-runner diagnostic
- **AND** SHALL list `service-status` described as the service-lifecycle (systemd/scheduled-task) status
