## Why

Mohist ships reusable coder agent skills, but users currently have to copy `.agents/skills` files into each repository by hand, which is error-prone and makes updates hard to apply consistently. A dedicated CLI flow is needed now to distribute the shared `mohist` and `mohist-explore` AgentSkills while keeping Mohist's internal `.mohist/skills` execution model separate.

## What Changes

- Add CLI commands for installing and updating Mohist-provided coder agent skills into a target repository's `.agents/skills` directory.
- Generate `.agents/skills/mohist/SKILL.md` and `.agents/skills/mohist-explore/SKILL.md` from built-in templates that satisfy AgentSkills frontmatter requirements.
- Make install/update safe to re-run by refreshing Mohist-generated files idempotently while preserving user-edited files unless `--force` is supplied.
- Support selecting the target repository path with `--path <repo>`.
- Ensure `mohist-walkthrough` is not installed or updated by default.
- Document or expose help text that distinguishes coder agent skills in `.agents/skills` from Mohist internal skills in `.mohist/skills`.

## Capabilities

### New Capabilities


### Modified Capabilities

- cli-interface

## Impact

- Affects `packages/cli/src/cli/index.ts` and a new or existing CLI command module for the `mo skills` command group.
- Adds a small service or utility for rendering built-in AgentSkills templates, writing `.agents/skills/<name>/SKILL.md`, detecting Mohist-generated files, and enforcing overwrite behavior.
- Adds tests under `packages/cli/tests` for first install, idempotent update, user modification protection, forced overwrite, target path handling, and exclusion of `mohist-walkthrough`.
- May update CLI help text or user documentation to clarify `.agents/skills` versus `.mohist/skills`.
- Does not change `SkillService` scanning or execution semantics for `.mohist/skills`, and does not require server-side API or database changes.
