## Why

The current .NET CLI no longer exposes the `mo skills` command group, even though Mohist still documents and ships coder-agent skill stubs that depend on `mo skills get <name>`. Restoring these local filesystem commands removes a migration regression and lets users refresh version-matched Mohist guidance without manually copying skill files or running the Mohist server.

## What Changes

- Restore the `mo skills` command group in the .NET CLI with `install`, `list`, `get`, and `path` subcommands.
- Make `mo skills install` create or refresh Mohist-managed built-in skill discovery stubs under `.agents/skills/` by default.
- Support `mo skills install --path <repo>` for explicit repository targets without writing to the current working directory unless it is the selected target.
- Support `mo skills install --claude` for Claude discovery stubs under `.claude/skills/`.
- Support `mo skills install --hermes` for full packaged skill installs under `${HERMES_HOME:-~/.hermes}/skills/`, rejecting incompatible option combinations.
- Serve built-in skill metadata and full guidance through `mo skills list`, `mo skills get`, `mo skills get --full`, `mo skills get --all`, and `mo skills path` without requiring the Mohist server.
- Keep `mo skills` scoped to shared coder-agent guidance and avoid scanning or mutating internal `.mohist/skills` runtime data.
- Align README and shipped skill stubs with the restored command surface and remove stale `mo skills update` guidance.
- Do not add a separate `mo skills update` command; rerunning `mo skills install` refreshes Mohist-managed built-in targets.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `cli-interface`: Restore and specify the local `mo skills` command group, its command registration, install targets, output modes, error handling, server independence, and exclusion of `update`.
- `mohist-skill-guidance`: Ensure packaged Mohist skill data, discovery stubs, README references, and agent guidance remain aligned with the restored `mo skills install/list/get/path` behavior.

## Impact

- Affects the .NET CLI command registration and local command handlers for `mo skills`.
- Adds or restores packaged built-in skill data resolution for `mohist` and `mohist-explore`, including optional development/test override via `MOHIST_SKILLS_DIR`.
- Writes local files only in coder-agent skill locations: `.agents/skills/`, `.claude/skills/`, or `${HERMES_HOME:-~/.hermes}/skills/` depending on selected mode.
- Updates user documentation and repository skill stubs to remove stale `update` references and point to the restored `get` workflow.
- Requires tests for command registration, install overwrite semantics, target separation, list/get/path output, unknown skill errors, incompatible options, and no mutation of `.mohist/skills`.
