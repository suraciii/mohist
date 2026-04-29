## ADDED Requirements

### Requirement: Systemd user service installation

CLI SHALL install mohist server as a systemd user service via `mo server install`, auto-detecting install mode (source vs npm global), generating the service file, enabling/starting the service, enabling linger, and handling headless SSH environments.

#### Scenario: Source mode installation
- **WHEN** user executes `mo server install`
- **AND** mohist is running from a source checkout (detected by presence of `packages/cli/` relative to `__dirname`)
- **THEN** CLI generates a .service file with `ExecStart=<node-path> <repo-root>/packages/cli/bin/mo-server --print-logs` and `WorkingDirectory=<repo-root>`
- **AND** CLI writes the file to `~/.config/systemd/user/mohist.service`
- **AND** CLI runs `systemctl --user daemon-reload`
- **AND** CLI runs `systemctl --user enable mohist.service`
- **AND** CLI runs `systemctl --user start mohist.service`
- **AND** CLI enables linger for the current user
- **AND** CLI displays success message with service name

#### Scenario: npm global installation
- **WHEN** user executes `mo server install`
- **AND** mohist is installed globally via npm (no `packages/cli/` relative to `__dirname`)
- **THEN** CLI generates a .service file with `ExecStart=<node-path> <global-prefix>/lib/node_modules/mohist/bin/mo-server --print-logs`
- **AND** the .service file does NOT include `WorkingDirectory`
- **AND** CLI performs the same enable/start/linger steps as source mode

#### Scenario: Service already installed
- **WHEN** user executes `mo server install`
- **AND** `~/.config/systemd/user/mohist.service` already exists
- **THEN** CLI overwrites the file with a new one
- **AND** CLI runs daemon-reload and restarts the service

#### Scenario: Headless SSH environment
- **WHEN** user executes `mo server install` over SSH without a local session
- **AND** `systemctl --user` fails due to user session not being registered
- **THEN** CLI retries with `systemctl --machine <username>@ --user`
- **AND** CLI displays a note about headless environment detection

### Requirement: Systemd user service uninstallation

CLI SHALL uninstall the mohist systemd user service via `mo server uninstall`.

#### Scenario: Uninstall running service
- **WHEN** user executes `mo server uninstall`
- **AND** mohist.service is installed and running
- **THEN** CLI runs `systemctl --user disable --now mohist.service`
- **AND** CLI deletes `~/.config/systemd/user/mohist.service`
- **AND** CLI runs `systemctl --user daemon-reload`
- **AND** CLI displays success message

#### Scenario: Uninstall when service not installed
- **WHEN** user executes `mo server uninstall`
- **AND** `~/.config/systemd/user/mohist.service` does not exist
- **THEN** CLI displays "Service not installed" and exits with code 0

### Requirement: Systemd service file generation with security hardening

CLI SHALL generate a valid systemd .service file with CR/LF injection protection and correct path escaping.

#### Scenario: Generated service file structure
- **WHEN** CLI generates the service file
- **THEN** the file contains `[Unit]` section with `Description=Mohist AI Workflow Server` and `After=network-online.target`
- **AND** `[Service]` section with `Type=simple`, `Restart=on-failure`, `RestartSec=5`, `TimeoutStopSec=30`, `SuccessExitStatus=0 143`, `StandardError=journal`
- **AND** `[Install]` section with `WantedBy=default.target`

#### Scenario: CR/LF injection prevention
- **WHEN** CLI generates the service file
- **THEN** all values written to the file SHALL NOT contain `\n` or `\r` characters
- **AND** paths with spaces or special characters SHALL be properly quoted

### Requirement: Path resolution for service ExecStart

CLI SHALL resolve the correct Node.js binary path and mo-server script path for the systemd service file.

#### Scenario: Node path resolution
- **WHEN** CLI resolves the node binary path
- **THEN** CLI uses `process.execPath` to find the current Node.js binary
- **AND** if the path contains `node_modules` (nvm/fnm/volta managed), CLI still uses `process.execPath`

#### Scenario: Source mode script path
- **WHEN** mohist is running from source
- **THEN** ExecStart points to `<repo-root>/packages/cli/bin/mo-server`

#### Scenario: npm global script path
- **WHEN** mohist is installed globally via npm
- **THEN** ExecStart points to `<npm-global-prefix>/lib/node_modules/mohist/bin/mo-server`

### Requirement: Linger management

CLI SHALL enable linger during install to ensure the user service survives logout.

#### Scenario: Enable linger
- **WHEN** CLI installs the systemd service
- **THEN** CLI runs `loginctl enable-linger <username>`
- **AND** if linger is already enabled, no error is shown

### Requirement: Systemd service detection

CLI SHALL detect whether the mohist systemd service is currently installed, for use by other commands (start/stop/status/restart).

#### Scenario: Service installed
- **WHEN** `~/.config/systemd/user/mohist.service` exists
- **THEN** `isSystemdServiceInstalled()` returns `true`

#### Scenario: Service not installed
- **WHEN** `~/.config/systemd/user/mohist.service` does not exist
- **THEN** `isSystemdServiceInstalled()` returns `false`
