### Requirement: Probe Hermes webhook readiness before changing any config

`mo notify setup` SHALL probe the Hermes webhook health endpoint before
generating a secret or writing Mohist config. The default probe target SHALL be
`http://127.0.0.1:8644/health` and SHALL be overridable via an option.

#### Scenario: Health endpoint reachable

- **WHEN** the configured Hermes health endpoint responds successfully
- **THEN** the command SHALL proceed to generate a secret and write Mohist config

#### Scenario: Health endpoint unreachable aborts without writing config

- **WHEN** the Hermes health endpoint is unreachable or returns an error
- **THEN** the command SHALL NOT generate a secret
- **AND** SHALL NOT write any Mohist configuration
- **AND** SHALL print a clear message stating the Hermes webhook platform is not
  started, with setup steps pointing at the Hermes notification documentation
- **AND** SHALL exit with a non-zero status code
- **AND** SHALL NOT print a stack trace

### Requirement: Generate one shared secret for Mohist signing and Hermes verification

The command SHALL generate a single random secret used identically by Mohist
outbound signing and the Hermes subscription verification command it emits.

#### Scenario: Secret is identical on both sides

- **WHEN** the command completes successfully
- **THEN** the secret written into Mohist config SHALL be byte-for-byte identical
  to the `--secret` value in the printed Hermes subscribe command

### Requirement: Write Mohist outbound Hermes config directly to the config file

The command SHALL write the Hermes webhook receiver URL, the shared secret, and
the default enabled notification types into the `Mohist:Notifications:Hermes`
nested section of `~/.mohist/config.jsonc`. This write SHALL be performed
directly against the JSONC file and SHALL NOT route through the flat
`ConfigService` key schema (which cannot represent nested notification
sections).

#### Scenario: Fresh config is populated with defaults

- **WHEN** no existing `Mohist:Notifications:Hermes` values are present
- **AND** the Hermes health endpoint is reachable
- **THEN** the command SHALL write the Hermes webhook receiver URL (derived from
  the probed Hermes address), the generated secret, and the default enabled
  notification types
- **AND** the default enabled notification types SHALL be `approval_requested`,
  `workflow_failed`, and `issue_completed`

#### Scenario: Existing values are overwritten only after confirmation

- **WHEN** one or more `Mohist:Notifications:Hermes` values already exist
- **THEN** the command SHALL prompt the user for confirmation before overwriting
- **AND** SHALL NOT silently overwrite existing values

#### Scenario: Reload guidance is printed after a successful write

- **WHEN** the command successfully writes Mohist config
- **THEN** it SHALL print guidance to reload the managed server via
  `mo update server`

### Requirement: Print a copy-pasteable Hermes subscribe command

The command SHALL print a complete `hermes webhook subscribe mohist` command
that the user can copy and run in Hermes. The printed command SHALL carry the
same `--secret` as the one written to Mohist config, use an inline
`--prompt '{message}'` passthrough (Mohist renders the body; Hermes only
forwards it), and reflect the user-selected delivery platform via `--deliver`.

#### Scenario: Delivery platform specified

- **WHEN** the user passes a delivery platform option (e.g. `--platform telegram`)
- **THEN** the printed subscribe command SHALL include `--deliver <platform>`
- **AND** SHALL include `--deliver-only`
- **AND** SHALL include `--secret` with the exact generated secret
- **AND** SHALL use the inline `--prompt '{message}'` form
- **AND** SHALL NOT use `--prompt-file`

#### Scenario: No delivery platform specified

- **WHEN** the user does not pass a delivery platform
- **THEN** the printed subscribe command SHALL omit a concrete `--deliver` value
- **AND** SHALL print guidance indicating the user must supply the target
  delivery platform

### Requirement: Do not fork processes or modify Hermes configuration

The command SHALL NOT fork or invoke the `hermes` CLI, and SHALL NOT modify any
Hermes-side configuration (webhook platform settings or subscription files).
Its only external interaction SHALL be a single HTTP GET to the Hermes health
endpoint; all Hermes-side actions are delivered solely as a printed command for
the user to execute.

#### Scenario: No hermes process is launched

- **WHEN** the command runs
- **THEN** it SHALL NOT spawn, fork, or exec any `hermes` process
- **AND** Hermes-side actions SHALL be delivered only as printed commands

#### Scenario: Hermes configuration files are untouched

- **WHEN** the command runs
- **THEN** it SHALL NOT create, edit, or delete any Hermes configuration file or
  subscription definition
- **AND** the only file it writes SHALL be the Mohist `~/.mohist/config.jsonc`
