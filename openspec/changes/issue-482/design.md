## Context

The CLI is already assembled in `packages/cli/Mohist.Cli` from a single `System.CommandLine` tree, but it still registers legacy paths and aliases (`skills`, `repository`, `show`, `update`, `opencode`, and `config`). Default framework help mirrors that unstructured tree, while the packaged `mohist` Skill duplicates lifecycle commands, flags, startup details, and obsolete paths.

The preceding Issues delivered the domain-specific command slices. This change is the compatibility-breaking convergence pass: humans and Agents need one discoverable syntax, with the command tree as the executable authority. CLI users, Agent authors, and scripts using `mo` are affected; server and runner contracts are not.

## Goals / Non-Goals

**Goals:**

- Expose only canonical command areas, verbs, ownership paths, project selection, and JSON field-selection vocabulary.
- Generate root, group, leaf, and usage-error help from the current command tree without Server access.
- Make packaged Skill guidance decision-oriented, syntax-light, and validated against the command parser.
- Update command-oriented documentation and tests with the same breaking release.

**Non-Goals:**

- Change server API routes, domain semantics, persistence, or runner behavior.
- Provide migration aliases, an Agent-only command mode, a machine-readable command catalog, or a generic configuration area.
- Add generic output renderers, shell tooling guidance, or a full CLI manual to help or the Skill.

## Decisions

### Keep the command tree as the only syntax authority

Rename command registrations and their public actions in place while preserving the existing handler, request, and resource-output implementation behind each canonical action. Remove aliases and deprecated command registrations rather than mapping them to the new path. Register only the canonical root areas, including singular `skill`; move no behavior into the server solely to support this migration.

The alternative is to retain aliases for a release cycle. It reduces immediate script breakage but leaves two authoritative paths and lets documentation, Skills, and tests diverge. A separate command catalog was also rejected because it would duplicate `System.CommandLine` parsing, flags, and validation.

### Attach presentation metadata to actual commands

Introduce a small CLI-local presentation model for category, resource boundary, optional see-also links, and leaf-only explanatory text. Builders attach that metadata when constructing the actual `Command` instances. A help renderer walks the registered command tree plus this metadata to produce root, group, and leaf layouts; arguments, options, legal values, and JSON fields continue to come from the command and its existing resource descriptor.

This makes the command object and its parameter definitions the authority for grammar while giving root and group help the information that the framework's default flat renderer does not express. A standalone markdown/help catalog was rejected because every command rename or option change would require synchronized updates in two models.

### Install a single local help and parse-error path

Wire the custom renderer into the command-line help hook for root, group, and leaf commands. Route parse failures through the same renderer for the nearest parsed command, writing diagnostics and local usage to stderr before returning exit code 2. The help hook and parse-error branch must run before project resolution or any API invocation.

This preserves the existing local parser validation behavior while replacing default help formatting. Leaving default framework help in place was rejected because it cannot enforce grouped root output, bounded group context, leaf JSON-field sections, or forbidden-content checks.

### Rename the Skill command without changing asset ownership

Refactor `SkillsCommands` into the singular public `skill` group. Rename resource reads from `get` to `view`, retain `list`, `install`, `path`, and `sync` only where each has an independent product behavior, and keep `SkillAssetService`, `SkillInstallService`, packaging, and managed-cache semantics unchanged. The command rename affects only the CLI facade; skill-data remains the packaged source of guidance.

An alternative is to keep `skills` as a hidden alias. It was rejected because hidden aliases remain executable protocol and violate the single-path requirement.

### Rewrite the entry Skill as progressive disclosure

Rewrite `skill-data/mohist/SKILL.md` around five sections: scope, first read, scenario routing, Mohist-specific decisions, and CLI handoff. Keep detailed issue creation, epic creation, and exploration in existing sibling Skills. Refer Agents to canonical leaf help for current flags and JSON fields; remove lifecycle tables, common-flag inventories, implementation history, and local startup/test instructions.

Extract fenced CLI examples from packaged Skills in a CLI test and parse them against the built root command tree without invoking handlers. This makes examples executable syntax rather than a manually reviewed copy. Parsing Skill text with a second grammar was rejected because it would repeat the command parser.

### Test public contracts by layer

Add focused CLI specs for canonical command registration, removed-path parse failures, ownership paths, Project input resolution, JSON selection, root/group/leaf help sections, and stderr-only usage errors. Keep existing behavior tests by renaming their invocations to canonical paths rather than duplicating old and new coverage. Add Skill content tests for required decision routing, forbidden copied material, and parseable examples. Update user documentation examples in the same change and add an exact-path search guard for removed public commands outside explicit migration-status text.

Full-page help snapshots were rejected as the primary guard because incidental formatting changes would be noisy; structural section and semantic assertions lock the user contract more directly.

## Risks / Trade-offs

- [Breaking scripts and Agent prompts that use removed paths fail immediately] -> Publish canonical replacements in release notes and documentation; return nearest-command usage with exit code 2, but do not retain aliases.
- [Presentation metadata drifts from a command registration] -> Require builders to attach metadata when registering visible commands and test that every visible root/group command has the expected presentation entry.
- [Custom help misses parser-defined option details] -> Render arguments and options from the `System.CommandLine` objects, not copied text; compare declared JSON fields with runtime selection tests.
- [Skill examples become stale] -> Extract and parse every fenced `mo` invocation in packaged Skill assets during CLI tests.
- [Repository-wide documentation update is incomplete] -> Search command documentation and Skill assets for removed public paths as part of the migration test/review checklist.
- [A release rollback restores ambiguity] -> Roll back by redeploying the prior CLI package as a complete version rollback; do not reintroduce aliases into the converged command tree.

## Migration Plan

1. Create the presentation metadata and custom help/error renderer around the existing command tree, with local-only help tests.
2. Rename registrations and public actions to canonical areas and verbs, remove aliases and obsolete root areas, and update existing handler tests to use only canonical calls.
3. Rename `skills` to `skill` and `get` to `view`, preserving packaged-asset and installation behavior behind the new facade.
4. Rewrite the packaged Mohist Skill, add parser-based example validation, and update sibling-Skill references.
5. Update `docs/cli-reference.md`, remaining user-facing examples, and help contract tests; run CLI specs and the complete affected documentation/Skill path search.
6. Ship as a breaking CLI release with the canonical-path migration notes. If a critical regression is found, redeploy the prior CLI package; no data migration or server rollback is required.

## Open Questions

- None. The target command vocabulary, ownership boundaries, and help/Skill contracts are established by the proposal and capability specifications.
