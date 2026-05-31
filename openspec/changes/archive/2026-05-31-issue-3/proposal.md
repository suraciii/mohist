## Why

Post-update smoke validation is currently implicit, which makes it easy for maintainers to miss the expected local verification path after updating Mohist. A short rendered-context note gives agents and reviewers a shared checklist for confirming that the update command, backend health endpoint, and issue UI still work together.

## What Changes

- Add a concise documentation note for local post-update smoke validation.
- The note will explicitly mention running `mo update`, checking `GET /api/health`, and opening `/issues`.
- No runtime behavior, API contract, or dependency changes are introduced.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `change-artifacts`: Records the expected rendered-context documentation note for local post-update smoke validation.

## Impact

- Affects documentation/change artifact content only.
- No changes to server APIs, web UI behavior, storage, runner behavior, or external dependencies.
