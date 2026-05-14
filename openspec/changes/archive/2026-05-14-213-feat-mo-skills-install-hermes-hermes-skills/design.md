## Context

Mohist currently installs shared agent skills for OpenCode/OpenClaw-style consumers by copying lightweight discovery stubs into `.agents/skills`, or into `.claude/skills` when `--claude` is used. Full, version-matched skill content lives separately under `packages/cli/src/agent-skills/skill-data/` and is exposed through `mo skills get`/`mo skills path`.

Hermes treats installed skills as native slash commands and reads the installed `SKILL.md` directly when the command is invoked. Installing Mohist's discovery stubs into Hermes would therefore expose `/mohist` as an indirection prompt instead of the actual Mohist workflow guidance. The Hermes path must install the full packaged skill-data into `${HERMES_HOME:-~/.hermes}/skills/` without modifying Hermes config.

## Goals / Non-Goals

**Goals:**

- Add `mo skills install --hermes` as a target-specific install mode.
- Install full packaged `mohist` and `mohist-explore` skill directories into the Hermes native skills directory.
- Preserve nested packaged content such as `mohist/references/issue-templates.md`.
- Respect `HERMES_HOME`, defaulting to `~/.hermes` when unset.
- Keep created/updated reporting and clear post-install usage guidance.
- Keep existing default `.agents/skills`, `--claude`, and `--path` behavior unchanged.

**Non-Goals:**

- Do not configure or inspect Hermes `skills.external_dirs`.
- Do not install Mohist stubs into Hermes.
- Do not install user-authored skills such as `mohist-po`.
- Do not invoke `hermes skills install`, publish to a Hermes hub, or require Hermes core changes.

## Decisions

### D1: Add Hermes as a separate install target, not a variant of `--path`

`--hermes` should select a distinct target resolver that ignores `--path` and writes to `${HERMES_HOME:-~/.hermes}/skills`. The existing `--path` option remains repository-scoped for `.agents/skills` and `.claude/skills` installs.

**Alternatives considered:** Reusing `--path` to point at a Hermes home would make users understand Hermes layout details and would overload an option that currently means repository path. Adding a separate `mo skills hermes install` command would avoid option conflicts, but is more discoverability overhead than the existing `skills install` target pattern.

### D2: Copy full `skill-data` directories for Hermes

Hermes installation should copy each built-in skill directory from `skill-data/<name>/` to `<hermesHome>/skills/<name>/`. This preserves `SKILL.md` and any packaged subdirectories exactly as Mohist ships them.

**Alternatives considered:** Installing stubs would keep Hermes aligned with the OpenCode path, but produces a poor `/mohist` experience because Hermes loads installed skill content directly. Generating full files through `mo skills get --full` would require reconstructing directory layout from printed supplementary files and risks diverging from packaged assets; direct directory copy is simpler and less lossy.

### D3: Replace only Mohist-managed built-in target directories

Repeat installs should determine `created` versus `updated` from whether `<skills>/<name>/SKILL.md` already exists, then replace the target directory for `mohist` and `mohist-explore` with the packaged source directory. No other Hermes skill directories are touched.

**Alternatives considered:** Merging files into the target directory would preserve user edits, but can leave stale packaged files after Mohist removes or renames references. Deleting the entire Hermes skills root is simpler but unsafe because it would remove unrelated Hermes skills.

### D4: Keep built-in skill names centralized

The Hermes installer should reuse the same built-in skill name source as the existing shared skill installer, currently `mohist` and `mohist-explore`. It should not discover arbitrary directories from `.agents/skills` or user workspaces.

**Alternatives considered:** Discovering all local skills would make the command broader, but would accidentally include user-specific skills such as `mohist-po` and blur the boundary between Mohist distribution and user customization.

### D5: Keep CLI output target-aware

The install command should format Hermes results with the native Hermes destination path and print examples for `/mohist` and `/mohist-explore`, plus a short note that an existing Hermes session may need a reload/reset or a new session before newly installed skills are visible.

**Alternatives considered:** Reusing the existing `.agents/.claude` formatter would be simpler, but would report the wrong target and omit the Hermes-specific slash command/reload guidance.

## Risks / Trade-offs

- [Risk] A user may have manually edited `~/.hermes/skills/mohist` or `mohist-explore`; repeat install replaces those directories -> Mitigation: limit replacement to the two Mohist built-in skill names and clearly report updated results so users understand Mohist owns those installed copies.
- [Risk] Recursive copy behavior can differ across Node versions -> Mitigation: use the project's supported Node filesystem APIs consistently in one helper and cover recursive directory copy with tests that assert `references/issue-templates.md` exists.
- [Risk] Tests could write to a real Hermes home -> Mitigation: tests must set `HERMES_HOME` to a temporary directory and assert no dependency on the developer's `~/.hermes`.
- [Risk] Option combinations such as `--hermes --claude` or `--hermes --path` are ambiguous -> Mitigation: validate mutually incompatible target options in the CLI and fail fast with a clear error instead of guessing.

## Migration Plan

1. Add a Hermes installer helper near the existing shared skill installer, using `SkillDataService.resolveSkillPath(name)` or an equivalent packaged skill-data resolver to locate full skill directories.
2. Extend `mo skills install` with `--hermes`, target validation, Hermes-specific formatting, and post-install guidance.
3. Add tests for `HERMES_HOME` path resolution, recursive full skill-data copying, exclusion of stubs and `mohist-po`, created/updated reporting, and unchanged `--path`/`--claude` behavior.
4. Rollback is removing the `--hermes` option and helper; existing OpenCode/Claude install paths remain independent and require no data migration.
