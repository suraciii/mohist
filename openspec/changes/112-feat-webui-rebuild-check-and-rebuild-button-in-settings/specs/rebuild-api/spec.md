## ADDED Requirements

### Requirement: Rebuild API endpoint

The system SHALL provide `POST /api/settings/system/rebuild` that triggers a background rebuild and restart sequence. The endpoint SHALL only be available when the server is running in source mode (i.e. `detectInstallMode().workingDir` is defined).

#### Scenario: Successful rebuild trigger in source mode

- **WHEN** client sends `POST /api/settings/system/rebuild`
- **AND** server is running in source mode (`detectInstallMode().workingDir` is defined)
- **THEN** server immediately returns `{ success: true }` with HTTP 200
- **AND** server starts a background process that executes `npm run build` in the CLI package directory, then `npm run build` in the web directory, then `systemctl --user restart mohist`

#### Scenario: Rebuild rejected in non-source mode

- **WHEN** client sends `POST /api/settings/system/rebuild`
- **AND** server is NOT running in source mode (`detectInstallMode().workingDir` is undefined)
- **THEN** server returns HTTP 400 with error message indicating rebuild is only available in source mode

#### Scenario: Rebuild rejected when systemd service not installed

- **WHEN** client sends `POST /api/settings/system/rebuild`
- **AND** server is in source mode
- **AND** systemd service is not installed (`isSystemdServiceInstalled()` returns false)
- **THEN** server returns HTTP 400 with error message indicating systemd service is not installed

#### Scenario: Background build failure does not affect API response

- **WHEN** `POST /api/settings/system/rebuild` returns `{ success: true }`
- **AND** the background build process fails
- **THEN** the server SHALL log the build failure
- **AND** SHALL NOT restart the service

### Requirement: Rebuild reuses existing build and restart logic

The rebuild background process SHALL reuse the same build steps and systemd restart mechanism as the CLI `mo server update` command implemented in `server-systemd.ts`.

#### Scenario: Build sequence matches mo server update

- **WHEN** the rebuild background process runs
- **THEN** it executes `npm run build` in `packages/cli` directory
- **AND** then executes `npm run build` in `packages/cli/web` directory
- **AND** then executes `systemctl --user restart mohist.service`
- **AND** the sequence matches `updateServer()` in `server-systemd.ts`
