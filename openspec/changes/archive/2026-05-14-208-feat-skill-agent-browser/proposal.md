## Why

Mohist currently installs shared coder skills by copying full template files into each repository, which lets installed content drift away from the CLI version and has already produced mismatches between shipped templates and what users actually load. Moving to dynamic skill loading is needed now to keep built-in skill guidance authoritative, reduce duplicated on-disk content, and make future skill updates take effect without reinstalling full payloads.

## What Changes

- Replace static full-file installation for Mohist-provided coder skills with lightweight discovery stubs that point `mo skills` commands at version-matched built-in skill content.
- Add CLI read paths for built-in skill data, including listing visible skills, printing skill content, returning full content with supplementary references, and showing the resolved package path for a skill.
- Reorganize bundled skill assets into separate discovery-stub and full-content directories so npm-distributed artifacts can support dynamic lookup instead of repository-local copies.
- Preserve coexistence with user-managed skills in `.agents/skills/` and keep existing fully installed `SKILL.md` files usable during the transition.
- Exclude user-authored skills such as `.agents/skills/mohist-po/` from Mohist-managed built-in distribution and update flows.

## Capabilities

### New Capabilities

### Modified Capabilities

- `cli-interface`
- `mohist-skill-guidance`

## Impact

- Affects `packages/cli/src/agent-skills/`, especially the current shared-skill installer and packaged skill asset layout.
- Extends `packages/cli/src/cli/commands/skills.ts` with additional `mo skills` subcommands and output modes for built-in skill discovery and retrieval.
- Requires package build and publish paths in `packages/cli/package.json` to ship both skill stubs and full skill-data assets with the CLI.
- Likely introduces a built-in skill data resolver/service for path discovery, frontmatter parsing, hidden-skill filtering, and supplementary file collection.
- Requires CLI tests covering stub-only install behavior, `get`/`get --full`/`get --all`, `path`, JSON output, environment-variable overrides, and compatibility with preexisting full installed skill files.
