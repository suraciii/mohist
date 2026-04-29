## ADDED Requirements

### Requirement: Source-mode rebuild and restart

CLI SHALL provide `mo server update` to rebuild the project and restart the systemd service, available only in source install mode.

#### Scenario: Source mode rebuild
- **WHEN** user executes `mo server update`
- **AND** mohist is installed in source mode
- **THEN** CLI runs `npm run build` in `packages/cli/`
- **AND** CLI runs `npm run build` in `packages/cli/web/`
- **AND** CLI runs `systemctl --user restart mohist.service`
- **AND** CLI displays the result of each step

#### Scenario: Build failure
- **WHEN** user executes `mo server update`
- **AND** `npm run build` fails (non-zero exit code)
- **THEN** CLI stops and displays the build error
- **AND** CLI does NOT restart the service
- **AND** CLI exits with non-zero code

#### Scenario: npm global mode rejection
- **WHEN** user executes `mo server update`
- **AND** mohist is installed via npm global
- **THEN** CLI displays "Update is only available for source installations. For npm, run: npm update -g mohist"
- **AND** CLI exits with code 1

#### Scenario: No systemd service installed
- **WHEN** user executes `mo server update`
- **AND** no systemd service is installed
- **THEN** CLI displays "No systemd service installed. Run 'mo server install' first."
- **AND** CLI exits with code 1
