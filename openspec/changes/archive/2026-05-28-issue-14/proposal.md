## Why

After running `mo update`, users need a quick way to confirm that their local Mohist server and runner are healthy before starting or resuming workflow work. The existing troubleshooting guide covers stage failures and recovery, but does not include a concise post-update smoke test path.

## What Changes

- Add a small troubleshooting note to `docs/TROUBLESHOOTING.md` for verifying local server and runner health after `mo update`.
- Keep the change documentation-only with no changes to runtime behavior, CLI/API contracts, workflow stages, or configuration.
- Point users to existing health/status checks and log surfaces that help confirm the local environment is ready.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- None. This change updates user-facing documentation only and does not alter spec-level product behavior.

## Impact

- Affected documentation: `docs/TROUBLESHOOTING.md`.
- No API, CLI, workflow, storage, dependency, or runtime behavior impact.
