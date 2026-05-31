## ADDED Requirements

### Requirement: Rendered context documents post-update smoke validation
The change artifacts SHALL include a concise rendered-context note that records the expected local post-update smoke validation path. The note MUST mention running `mo update`, checking `GET /api/health`, and opening `/issues`.

#### Scenario: Post-update smoke validation note is present
- **WHEN** maintainers or agents review the rendered context for this change
- **THEN** they SHALL see a short local smoke validation note
- **AND** the note MUST mention running `mo update`
- **AND** the note MUST mention checking `GET /api/health`
- **AND** the note MUST mention opening `/issues`
