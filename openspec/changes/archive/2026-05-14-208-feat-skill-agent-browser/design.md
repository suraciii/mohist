## Context

Mohist currently distributes shared coder skills by copying full markdown templates from `packages/cli/src/agent-skills/templates/` into each repository's `.agents/skills/<name>/SKILL.md`. That makes the installed copy the runtime source of truth, so packaged template updates do not reach existing repositories and the installed content can diverge from the CLI version.

This change affects Mohist-provided coder skills only. It does not change the internal `.mohist/skills` runtime handled by `SkillService`, and it must not take ownership of user-authored skills such as `.agents/skills/mohist-po/`.

## Goals / Non-Goals

**Goals:**

- Make Mohist-provided coder skills dynamically readable from the installed CLI package instead of from copied full payloads in each repository.
- Install only lightweight discovery stubs into `.agents/skills/` and `.claude/skills/`.
- Add local CLI commands to list built-in skills, print built-in skill content, print built-in skill paths, and optionally include supplementary reference files.
- Keep built-in skill content version-matched with the running Mohist CLI.
- Preserve compatibility with repositories that already contain fully installed `SKILL.md` files from the old copy-based model.
- Support overriding built-in skill asset lookup with `MOHIST_SKILLS_DIR` for development and tests.

**Non-Goals:**

- Do not change `.mohist/skills` scanning, execution, APIs, database tables, or workflow behavior.
- Do not manage or rewrite user-authored skills outside the fixed built-in Mohist skill set.
- Do not add remote/server-backed skill retrieval; all operations remain local filesystem reads.
- Do not introduce automatic migration of every existing repository; migration happens when users rerun `mo skills install`.

## Decisions

### D1: Split built-in skill assets into `stubs/` and `skill-data/`

Move the current source assets under `packages/cli/src/agent-skills/` into two roles:

- `stubs/<name>/SKILL.md` contains a short discovery stub with frontmatter and `hidden: true`
- `skill-data/<name>/SKILL.md` contains the full built-in guidance
- `skill-data/<name>/references/*` and `skill-data/<name>/templates/*` hold optional supplementary content

This mirrors the agent-browser model while fitting the existing TypeScript package layout. The directory names become stable packaging units that build scripts can copy into `dist/agent-skills/` unchanged.

**Alternatives considered:** Keep one directory and distinguish stubs vs full content by filename suffix. That reduces directory count but makes discovery logic and packaging rules more implicit. Embed all full skill content in TypeScript strings. That avoids runtime file lookup but makes large markdown assets harder to review and maintain.

### D2: Introduce a local `SkillDataService` as the single built-in skill resolver

Add a small service module under `packages/cli/src/agent-skills/` responsible for:

- finding the packaged built-in skill root
- scanning `stubs/` and `skill-data/`
- parsing frontmatter (`name`, `description`, `hidden`)
- returning discovered `SkillInfo` records sorted by name
- resolving one named skill to its content and packaged directory
- appending supplementary files for `--full`

This keeps path discovery, metadata parsing, and file aggregation out of the Commander command module and out of the installer. Both `mo skills list/get/path` and the installer can share one source of truth for the built-in skill set.

The service should prefer directories in this order:

1. `MOHIST_SKILLS_DIR` when set
2. packaged runtime layout derived from `__dirname` / installed `dist/agent-skills`
3. source checkout layout for local development

The environment variable should point at the built-in asset root containing `stubs/` and `skill-data/`.

**Alternatives considered:** Keep resolver logic inside `cli/commands/skills.ts`. That is simpler short term but would duplicate logic once install and tests also need packaged path resolution. Reuse `SkillService`. That would mix unrelated concerns because `SkillService` manages `.mohist/skills` project runtime data, not packaged built-in skill assets.

### D3: Discovery should merge both directories but deduplicate by skill name with `skill-data` preferred for content reads

The discovery model needs to support two different use cases:

- stubs are what get installed into user repositories
- full skill data is what `mo skills get` should return

To support both cleanly, discovery should scan both `stubs/` and `skill-data/` and keep enough metadata to know whether a result is a stub or a full skill payload. When both exist for the same skill name:

- `list` should hide entries whose selected record is `hidden: true`
- `get <name>` should prefer the `skill-data` entry when present
- `path <name>` should return the selected built-in source directory, which is normally the `skill-data` directory
- `get --all` should return all built-in skills, including hidden stubs only if explicitly requested by future behavior; for this change it should return the visible built-in skill set backed by full content

This preserves the intent of `hidden: true` on stubs without making the full content itself hidden or undiscoverable.

**Alternatives considered:** Mark the full `skill-data` entries as hidden too and rely on explicit name lookup only. That would make `list` effectively empty for the current built-in skill set. Treat stubs and full content as separate skills with the same name. That creates duplicate and ambiguous command behavior.

### D4: `mo skills install` should write only stub `SKILL.md` files for the Mohist-managed skill names

Refactor `installSharedAgentSkills()` so it no longer copies full markdown payloads or supplementary files into the target repository. Instead it should:

- resolve the built-in stub content from `SkillDataService`
- write `SKILL.md` into `.agents/skills/<name>/` or `.claude/skills/<name>/`
- manage only the fixed Mohist built-in names (`mohist`, `mohist-explore`)

Existing installed full files remain supported because coding agents can still read them directly; they are simply no longer the preferred install target. Re-running `mo skills install` upgrades those managed names to stub content so future reads flow through `mo skills get`.

**Alternatives considered:** Leave install behavior unchanged and only add `get`/`path`. That would preserve version drift and duplicated payloads. Delete supplementary files from existing installs during upgrade. That is more invasive than necessary and risks removing user-managed files from skill directories.

### D5: `mo skills` remains a local filesystem command group with explicit built-in vs repository behavior

The `skills` command group should stay local and not require the Mohist server. Subcommands divide into two categories:

- repository write: `install`
- built-in asset read: `list`, `get`, `path`

`get` and `path` operate on packaged built-in skill data, not `.agents/skills` in the current repository. `install` writes repository-local stubs that instruct agents to call back into the CLI. Help text should say this plainly to avoid confusion with `.mohist/skills` and with user-authored repository skills.

For output shape:

- human mode prints plain markdown for `get`, one path for `path`, and a readable list for `list`
- `--json` returns structured objects for `list`, `get`, and `path`

**Alternatives considered:** Make `get` inspect the current repository's `.agents/skills` first. That would reintroduce drift by letting stale installed files shadow packaged assets. Route these commands through server APIs. That adds operational dependency to a purely local read path.

### D6: Supplementary file expansion is append-only and deterministic

`mo skills get <name> --full` should read the base `SKILL.md` from `skill-data/<name>/` and then append supplementary files from `references/` and `templates/` in sorted order. The output format should be deterministic and clearly separate files, for example with section delimiters containing the relative path.

This keeps the base skill readable in normal mode while allowing larger reference payloads only when explicitly requested. Deterministic ordering also makes tests and downstream tooling stable.

**Alternatives considered:** Inline references directly into `SKILL.md`. That removes the ability to fetch a compact version and makes the source harder to maintain. Emit a tarball or multipart format. That is harder for both humans and coding agents to consume.

### D7: Build and packaging should copy the new directory structure verbatim

Update the backend build scripts in `packages/cli/package.json` so `dist/agent-skills/` contains:

- `stubs/`
- `skill-data/`
- any remaining standalone shared files only if still needed elsewhere

The npm `files` list can remain `dist/`-based as long as the build copies these assets into `dist`. The design should avoid requiring package-root `skill-data/` directories unless Mohist later wants agent-browser-style top-level publish layout.

**Alternatives considered:** Change npm packaging to ship `src/agent-skills/` directly. That breaks the current compiled-package convention and couples runtime reads to source layout. Move assets to package-root top-level directories immediately. That is possible, but it creates a broader packaging change than the feature requires.

## Risks / Trade-offs

- [Risk] Hidden stub semantics can accidentally hide every built-in skill from `list` if full entries are not modeled separately. → Mitigation: make `skill-data` the visible/readable record and keep `hidden: true` only on installed stubs.
- [Risk] Path discovery may fail in packaged installs if it relies on source-only layout assumptions. → Mitigation: centralize lookup in `SkillDataService`, test both `dist` and source-style roots, and support `MOHIST_SKILLS_DIR` override.
- [Risk] Existing old-style installed full skills may remain stale if users never reinstall. → Mitigation: preserve compatibility, but make the stub upgrade path explicit through `mo skills install` output and docs.
- [Risk] Supplementary file concatenation could become too large for normal agent usage. → Mitigation: keep references behind `--full` and leave base `get` output compact.
- [Risk] Mixing built-in and user-authored skills under `.agents/skills/` may confuse users about what Mohist owns. → Mitigation: scope installer writes to the fixed built-in names only and keep help text explicit that other skills are untouched.

## Migration Plan

1. Restructure `packages/cli/src/agent-skills/` into `stubs/` and `skill-data/`, moving current markdown assets without changing the full guidance text.
2. Add `SkillDataService` plus small frontmatter parsing helpers and supplementary-file collection.
3. Refactor `shared-agent-skills.ts` to install stub content only and to use the built-in resolver instead of hard-coded template file paths.
4. Extend `cli/commands/skills.ts` with `list`, `get`, and `path`, including `--full`, `--all`, and `--json` where required.
5. Update build scripts so `dist/agent-skills/` includes the new asset directories.
6. Add tests covering source-root lookup, packaged-root lookup, environment override, visible list behavior, `get --full` aggregation, stub-only install, and compatibility with preexisting full installed skill files.
7. Rollback is reverting the command and asset-layout changes; installed stubs are plain markdown files and do not require database or server cleanup.

## Open Questions

- The current CLI still supports `.claude/skills` installs. This design preserves that behavior, but the acceptance criteria primarily mention `.agents/skills`; confirm whether both targets remain required for this change.
