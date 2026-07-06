### Requirement: `notification` is the root resource group; the `notify` verb-name group is removed

The root command SHALL expose a `notification` command group (a resource noun), replacing the legacy `notify` group (a bare verb). The `notify` group name SHALL NOT exist at the root after this change. `notification` is the canonical home for notification-platform configuration; this change introduces no subcommand other than `setup`, which is a pure relocation of the legacy `mo notify setup`.

#### Scenario: notification appears at the root

- **WHEN** a caller runs `mo --help`
- **THEN** the listed top-level subcommands SHALL include `notification`
- **AND** SHALL NOT include `notify`

#### Scenario: the notification group exposes setup

- **WHEN** a caller runs `mo notification --help`
- **THEN** the listed subcommands SHALL include `setup`

### Requirement: `mo notification setup` reproduces the previous `mo notify setup` behavior exactly

`mo notification setup` SHALL be a pure path relocation of the legacy `mo notify setup`. The guided flow — probe Hermes webhook platform health, validate `--platform`, generate a shared signing secret, prompt before overwriting an existing `Mohist:Notifications:Hermes` section, write the outbound Hermes config to the resolved config path, and print the copy-pasteable `hermes webhook subscribe mohist` command — SHALL behave identically to the legacy command. The flags (`--health-base`, `--webhook-url`, `--platform`, `--deliver-chat-id`), their defaults, validation rules, exit codes, output wording, and config-path/seam overrides SHALL be unchanged.

#### Scenario: flags and defaults carry over unchanged

- **WHEN** a caller runs `mo notification setup --help`
- **THEN** the documented options SHALL include `--health-base` (default `http://127.0.0.1:8644`), `--webhook-url`, `--platform`, and `--deliver-chat-id`
- **AND** the descriptions SHALL match the previous `mo notify setup --help` text

#### Scenario: the happy-path flow is identical

- **WHEN** a caller runs `mo notification setup` against a healthy Hermes instance and confirms overwrite of an existing section
- **THEN** the CLI SHALL write `Mohist:Notifications:Hermes` (WebhookUrl, Secret, EnabledTypes) to the resolved config file
- **AND** SHALL print `Wrote Mohist:Notifications:Hermes to <path>` followed by the same `hermes webhook subscribe mohist ...` command the legacy command printed
- **AND** SHALL exit 0

#### Scenario: probe-failure handling carries over

- **WHEN** a caller runs `mo notification setup` and the Hermes health probe fails (connection refused, timeout, non-success status, or invalid URL)
- **THEN** the CLI SHALL print the same `Hermes webhook platform is not started.` guidance (including the `docs/hermes-notifications.md` pointer) the legacy command printed
- **AND** SHALL write no config file
- **AND** SHALL exit non-zero without surfacing a stack trace
