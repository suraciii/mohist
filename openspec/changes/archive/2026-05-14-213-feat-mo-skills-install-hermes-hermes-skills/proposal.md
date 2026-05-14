## Why

Hermes users need Mohist's shared skills to install as native Hermes slash commands without first loading Mohist's OpenCode-oriented discovery stubs or learning Hermes configuration internals. This is needed now because Mohist's packaged `skill-data/` already provides version-matched full skill content, while the existing stub installer creates an unnecessary and confusing indirection for Hermes.

## What Changes

- Add a Hermes-specific install target to `mo skills install --hermes`.
- Install the full packaged `mohist` and `mohist-explore` skill-data directories into `${HERMES_HOME:-~/.hermes}/skills/`.
- Preserve supplementary skill files such as `mohist/references/issue-templates.md` when installing to Hermes.
- Report per-skill `created` or `updated` results for repeatable installs.
- Print post-install usage guidance for `/mohist` and `/mohist-explore`, including a note that the current Hermes session may need to reload/reset before seeing newly installed skills.
- Keep the existing OpenCode default installer, `--claude`, and `--path` behavior unchanged.
- Do not install Mohist discovery stubs, modify Hermes `skills.external_dirs`, or include user-defined skills such as `mohist-po`.

## Capabilities

### New Capabilities


### Modified Capabilities

- mohist-skill-guidance

## Impact

- Affects the `mo skills install` CLI command in `packages/cli/src/cli/commands/skills.ts` by adding a mutually distinct Hermes target.
- Affects shared skill installation logic in `packages/cli/src/agent-skills/shared-agent-skills.ts` and packaged skill lookup/copy behavior in `packages/cli/src/agent-skills/skill-data-service.ts` or adjacent utilities.
- Uses existing packaged skill content under `packages/cli/src/agent-skills/skill-data/` rather than the stub content under `packages/cli/src/agent-skills/stubs/`.
- Requires tests around Hermes installation path resolution with `HERMES_HOME`, full directory copying, created/updated reporting, and preservation of existing `--path` and `--claude` behavior.
- Does not require Hermes core changes, Hermes config mutation, new external services, or new runtime dependencies.
