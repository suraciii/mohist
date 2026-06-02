## Context

Mohist ships full built-in coder skill guidance in CLI publish output under `skill-data/`, but the local install path keeps only a bare `mo` executable in `~/.local/bin/mo`. Current skill resolution falls back to `AppContext.BaseDirectory/skill-data`, so an installed binary looks beside `~/.local/bin/mo` and fails even though the published assets exist elsewhere. Repository-installed discovery stubs then point agents at `mo skills get <name>`, but the command cannot reliably find version-matched packaged guidance after `mo update` or `scripts/install-mo.sh`.

The CLI must remain a local command that works without a running Mohist server. Packaged coder-agent assets are CLI-owned build artifacts, not user configuration and not runtime/internal `.mohist/skills` state. Development and tests still need `MOHIST_SKILLS_DIR` to override asset resolution explicitly.

Stakeholders are Mohist operators who install a simple `mo` binary, agents that load discovery stubs, contributors running local publish/dev flows, and release/update code that must keep the binary and packaged guidance synchronized.

## Goals / Non-Goals

**Goals:**

- Resolve built-in skill assets from `MOHIST_SKILLS_DIR`, then a version-compatible managed cache at `~/.mohist/cli/skill-data`, then sibling publish/dev assets at `AppContext.BaseDirectory/skill-data`.
- Synchronize published `skill-data` into the managed cache during `mo update` and `scripts/install-mo.sh`.
- Record a manifest in the managed cache with CLI build identity and bundled built-in skill names.
- Fail with actionable diagnostics when managed assets are missing, stale, incompatible, or incomplete.
- Keep `mo skills get`, `mo skills path`, `mo skills list`, repository/Claude discovery stubs, and Hermes full-skill install backed by packaged assets.
- Avoid reading, writing, scanning, or mutating runtime/internal `.mohist/skills` for packaged coder-agent assets.

**Non-Goals:**

- Add a separate `mo skills update` command.
- Store user-authored or external agent skills in `~/.mohist/cli/skill-data`.
- Serve skill assets from the Mohist server.
- Treat the managed cache as editable user configuration.
- Change the runtime `SkillService` behavior for internal Mohist skills.

## Decisions

### Decision 1: Use a CLI-managed cache as the default installed asset root

Built-in packaged assets will be copied to `~/.mohist/cli/skill-data` and treated as the preferred default root when no environment override is set and the cache is version-compatible. This keeps `~/.local/bin/mo` as a single executable install while avoiding fragile assumptions about sibling resource files.

Alternatives considered:

- Install `skill-data` beside `~/.local/bin/mo`: simple lookup, but pollutes a shared binary directory and can fail when users install the binary into paths that should not contain mutable resources.
- Embed all skill data into the executable: strong version coupling, but makes supplementary files and future asset layout changes harder to inspect, test, and synchronize.
- Require `MOHIST_SKILLS_DIR`: useful for development, but not acceptable for normal operators or installed discovery stubs.

### Decision 2: Keep explicit resolution precedence with validation at each root

`SkillAssetService.ResolveAssetRoot` should evaluate roots in this order: valid `MOHIST_SKILLS_DIR`, compatible managed cache, compatible sibling `AppContext.BaseDirectory/skill-data`. The environment override remains intentionally highest priority for development and tests. Managed and sibling roots should be validated using the manifest and expected built-in skill directories before being selected.

Alternatives considered:

- Prefer sibling assets over the managed cache: convenient in publish directories, but reintroduces the installed-bare-binary failure mode.
- Ignore manifest compatibility for sibling fallback: useful during early development, but weakens the guarantee that guidance matches the running CLI. If development needs relaxed behavior, it should use a valid generated manifest or `MOHIST_SKILLS_DIR`.

### Decision 3: Add a small manifest to packaged skill-data

`manifest.json` will live at the asset root and record at least the CLI build identity, such as version and/or git hash, plus the built-in skill names included in that asset set. The same manifest format is used in publish output and in the managed cache. The CLI compares the manifest build identity with the running binary's build identity and verifies that requested built-in skills are listed and present.

Alternatives considered:

- Infer compatibility only from file presence: avoids manifest generation, but cannot distinguish stale assets from current assets and produces poor diagnostics.
- Store manifest outside `skill-data`: separates metadata from assets, but complicates atomic replacement and path reporting. Keeping metadata inside the root makes the asset directory self-describing.

### Decision 4: Synchronize assets by replacing the managed root atomically enough

`mo update` and `scripts/install-mo.sh` will copy publish output `skill-data` to a temporary directory under `~/.mohist/cli/`, verify expected files and manifest, then replace the managed `skill-data` directory with the prepared directory. This limits the window where users could observe partial content and ensures Mohist only replaces its own managed cache.

Alternatives considered:

- Copy files in place: simplest, but a concurrent `mo skills get` can observe mixed old/new files or a partially copied skill directory.
- Versioned cache directories with a symlink/current pointer: more robust rollback semantics, but adds platform-specific symlink behavior and more cleanup policy than this change requires.

### Decision 5: Centralize asset mismatch diagnostics in skill asset resolution

Missing `SKILL.md`, absent manifest, stale build identity, omitted skill names, or incompatible cache state should surface through a clear resolver error that explains the selected/attempted roots and tells users to rerun `mo update` or `scripts/install-mo.sh`. Command handlers should not degrade to only reporting a missing file path for managed cache failures.

Alternatives considered:

- Let each command produce its own file-not-found errors: lower implementation cost, but leads to inconsistent and non-actionable diagnostics.
- Auto-repair by running update from `mo skills get`: surprising side effects and may require source/network/build context that the local command does not have.

## Risks / Trade-offs

- [Risk] The running binary build identity may not be available consistently across dev, publish, and tests -> Mitigation: reuse the existing CLI version/git hash source where available and generate the manifest from the same source during publish/install tests.
- [Risk] Atomic directory replacement differs across filesystems and platforms -> Mitigation: prepare a complete temporary directory first, then use the smallest possible remove/rename window and keep synchronization scoped to `~/.mohist/cli/skill-data`.
- [Risk] Strict manifest validation could break local development when publish metadata is missing -> Mitigation: keep `MOHIST_SKILLS_DIR` as the explicit development/test override and ensure publish/dev fallback assets include a generated manifest.
- [Risk] A stale managed cache could mask valid sibling publish assets -> Mitigation: treat an incompatible existing managed cache as a diagnostic failure for installed use, because the user needs repair guidance rather than silent fallback to potentially unrelated assets.
- [Risk] Install/update scripts could accidentally touch user skill directories -> Mitigation: synchronize only the managed packaged asset root and do not traverse `.agents/skills`, `.claude/skills`, `.hermes/skills`, or `.mohist/skills` during cache refresh.

## Migration Plan

1. Add manifest generation for packaged `skill-data` in the CLI publish/build path so publish output is self-describing.
2. Update `SkillAssetService` to resolve and validate roots in the required precedence order, including managed cache path calculation under the Mohist local state root.
3. Update `mo skills get`, `mo skills path`, `mo skills list`, and install modes that copy full packaged skills to consume the validated asset root and report resolver diagnostics.
4. Update `SourceCodeUpdater.UpdateCliAsync` to synchronize `publishDir/skill-data` into `~/.mohist/cli/skill-data` after publishing and before/with binary installation completion.
5. Update `scripts/install-mo.sh` to perform the same managed cache synchronization for manual installs.
6. Add tests for managed root resolution, env override precedence, sibling fallback, update/script synchronization, manifest mismatch diagnostics, and preservation of runtime/internal `.mohist/skills` separation.

Rollback strategy: keep `MOHIST_SKILLS_DIR` support and sibling `AppContext.BaseDirectory/skill-data` fallback so developers can recover manually if the managed cache is invalid. A rollback of the code should also remove or ignore `~/.mohist/cli/skill-data`; because it is a CLI-owned cache, leaving it on disk is safe and should not affect runtime/internal skills.

## Open Questions

- Which exact build identity fields are already available in the CLI and should be considered compatibility-defining: semantic version, git hash, build timestamp, or a combination of version and git hash?
- Should incompatible managed cache always fail before sibling fallback, or should a development-mode signal allow fallback when a stale cache exists? The proposed default is fail with repair guidance for installed use.
- Should synchronization retain a previous cache as a backup for debugging, or is complete replacement sufficient for this managed asset cache?
