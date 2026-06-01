## Context

The current .NET/Orleans `mo` CLI no longer registers the `skills` command group, while Mohist still ships coder-agent skill stubs and documentation that depend on `mo skills get <name>`. This breaks the local workflow for OpenCode, Claude Code, and Hermes users who expect Mohist-provided agent guidance to be discoverable and refreshable from the current CLI binary.

This change restores the pre-Orleans product shape from the final stub-based TypeScript CLI baseline: local `install`, `list`, `get`, and `path` commands for shared coder-agent skills. These commands are filesystem-only operations and must not depend on the Mohist server, Orleans grains, workflow runtime, or internal `.mohist/skills` scheduling behavior.

The primary stakeholders are Mohist users installing or refreshing coder-agent guidance, agent runtimes discovering skills from their native directories, and maintainers who need packaged skill guidance to stay version-matched with the installed CLI.

## Goals / Non-Goals

**Goals:**

- Restore `mo skills install`, `mo skills list`, `mo skills get`, and `mo skills path` in the .NET CLI.
- Serve built-in Mohist skill data from packaged CLI assets, with optional `MOHIST_SKILLS_DIR` override for development and tests.
- Install lightweight discovery stubs for OpenCode under `.agents/skills/` and Claude under `.claude/skills/`.
- Install full packaged skill directories for Hermes under `${HERMES_HOME:-~/.hermes}/skills/`.
- Keep the visible built-in set deterministic, sorted by skill name, and limited initially to `mohist` and `mohist-explore`.
- Refresh existing Mohist-managed built-in targets by rerunning `mo skills install`.
- Align README and shipped stubs with `install/list/get/path` and remove stale `update` guidance.

**Non-Goals:**

- Do not add `mo skills update`; reinstall is the refresh path.
- Do not restore old internal skill scheduling, runtime execution, or `.mohist/skills` behavior.
- Do not require or contact the Mohist server.
- Do not scan, execute, create, delete, or mutate `.mohist/skills`.
- Do not manage arbitrary user-authored skills such as `mohist-po` unless they are intentionally added to the built-in asset registry later.
- Do not read or mutate Hermes `config.yaml` or `skills.external_dirs`.

## Decisions

1. Implement `mo skills` as a local CLI command group backed by a small skill service.

   Rationale: command registration, option validation, asset resolution, install behavior, and output formatting should be testable without booting the server. A local service keeps this feature independent from Orleans and avoids coupling coder-agent guidance to Mohist workflow runtime state.

   Alternatives considered: route commands through existing server APIs or grain calls. This was rejected because the required behavior explicitly works without a running server and only touches local packaged assets and filesystem targets.

2. Use packaged skill-data as the source of truth for full guidance.

   Rationale: `mo skills get <name>` should return guidance matching the running CLI version, not stale repository-local copies. Packaged `skill-data/<name>/SKILL.md` provides full content, while supplementary `references/` and `templates/` files can be appended deterministically for `--full`.

   Alternatives considered: serve guidance from installed `.agents/skills` or `.claude/skills` directories. This was rejected because those installs are intentionally lightweight stubs and may be stale, edited, or absent.

3. Keep the built-in skill set explicit.

   Rationale: the command should install and list only Mohist-managed built-ins, initially `mohist` and `mohist-explore`. An explicit registry prevents accidentally exposing local development skills or user-authored directories such as `mohist-po`.

   Alternatives considered: discover all directories under the packaged skill root. This is simpler but risks unintentionally installing hidden, experimental, or user-created skills. If directory discovery is used internally for tests, it should still be filtered through explicit visible metadata.

4. Generate OpenCode and Claude installs as discovery stubs.

   Rationale: repository-local and Claude skill directories should remain small, stable entry points that direct agents to `mo skills get <name>` for version-matched full guidance. Reinstall can safely overwrite the Mohist-managed `SKILL.md` for built-in names without touching unrelated directories.

   Alternatives considered: copy full packaged guidance into `.agents/skills` and `.claude/skills`. This was rejected because it recreates drift between installed repository copies and the CLI version, and conflicts with the requested stub-based baseline.

5. Copy full packaged directories for Hermes.

   Rationale: Hermes expects directly usable skill directories under its native skills home. Hermes installs must not rely on a discovery stub that requires a later `mo skills get` call.

   Alternatives considered: write stubs to Hermes as well, or modify Hermes config to point at Mohist packaged assets. Stubs are not sufficiently usable in Hermes, and config mutation is outside the required behavior and riskier for user environments.

6. Validate install modes before writing files.

   Rationale: `--hermes` is mutually exclusive with `--path` and `--claude`. Validation should happen before any target directory creation or file write so incompatible commands leave the filesystem unchanged.

   Alternatives considered: allow combined modes and install to multiple targets. This was rejected because the spec defines mutually exclusive Hermes behavior and target separation is easier to reason about and test.

7. Use deterministic output for list, get-all, JSON, and full content.

   Rationale: sorted skill names and sorted supplementary files make command output stable for users, tests, and downstream tooling.

   Alternatives considered: preserve filesystem enumeration order. This was rejected because enumeration order is platform-dependent and can cause flaky tests or surprising diffs.

8. Keep `MOHIST_SKILLS_DIR` as a development/test override only for asset resolution.

   Rationale: the old CLI supported this parity point, and it makes tests independent from publish layout. The override should point to a valid built-in asset root and should not change install target semantics.

   Alternatives considered: omit the override and rely only on embedded or copied publish assets. This would simplify production behavior but make tests and local package-layout validation harder.

## Risks / Trade-offs

- [Packaged asset path differs between development, test, and published CLI layouts] -> Centralize asset-root resolution and cover default and `MOHIST_SKILLS_DIR` paths in tests.
- [Reinstall overwrites user edits inside built-in skill names] -> Limit overwrite behavior to Mohist-managed built-in directories and document rerun-install as refresh semantics.
- [Accidental mutation of unrelated skill directories] -> Iterate only over the explicit built-in registry and never scan target roots for cleanup.
- [Hermes install may partially copy if an error occurs mid-write] -> Validate options before writes and prefer replace-per-skill behavior with directory creation scoped to each built-in skill. Tests should verify incompatible options perform no writes.
- [Full `--full` output may become noisy as references grow] -> Append only deterministic files from packaged `references/` and `templates/` directories and keep normal `get` focused on `SKILL.md`.
- [Documentation can drift again] -> Update README and shipped stubs in the same change and test for absence of `skills update` command registration.

## Migration Plan

1. Add packaged built-in skill assets for `mohist` and `mohist-explore` in the .NET CLI package layout, including full `SKILL.md` content and optional `references/` or `templates/` files.
2. Add the local skill asset resolver and built-in skill registry, honoring `MOHIST_SKILLS_DIR` when set.
3. Register `mo skills` with `install`, `list`, `get`, and `path`, and do not register `update`.
4. Implement install modes: default OpenCode stubs, explicit `--path`, Claude stubs, and Hermes full-copy installs with mutual-exclusion validation.
5. Implement deterministic text and JSON output for `list`, `get`, `get --all`, and `path`.
6. Update README and repository skill stubs to document reinstall-as-refresh and `mo skills get <name>`.
7. Add tests for command registration, overwrite behavior, install target separation, Hermes validation, list/get/path output, unknown skill errors, `MOHIST_SKILLS_DIR`, and no `.mohist/skills` mutation.

Rollback is straightforward because this change is additive to the .NET CLI command surface and local packaged assets. If a regression is found, remove the `skills` command registration or revert the CLI package changes; installed user files remain local filesystem artifacts and can be overwritten by a corrected `mo skills install` in a later release.

## Open Questions

- Should packaged skill assets be embedded resources, copied content files, or both for publish scenarios? The implementation should choose the smallest reliable approach that works in development, tests, and packaged CLI execution.
- Should stub overwrite detection include a Mohist-managed marker, or should built-in name ownership be sufficient? The current requirements allow overwrite by built-in name, but a marker could make future user-edit preservation more explicit.
- Should `mo skills get --all --json` be supported if the CLI parser permits combining those flags? The specs require `--json` for named `get` and `--all` output, but do not explicitly define their combination.
