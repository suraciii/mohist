# Full verification resource budget

## Scenario: Full verification has enough finite address space

- **GIVEN** a `core/script` action declares `resourceProfile: full-verify`
- **WHEN** the Runner resolves command resource limits
- **THEN** the command receives a 16384 MiB memory bound
- **AND** the existing wall-clock and watchdog limits remain unchanged
- **AND** the command remains subject to per-work containment

## Scenario: Ordinary work keeps the conservative default

- **GIVEN** a command does not declare `full-verify`
- **WHEN** the Runner resolves its resource limits
- **THEN** it keeps the configured ordinary work memory bound
- **AND** no unbounded command profile is introduced
