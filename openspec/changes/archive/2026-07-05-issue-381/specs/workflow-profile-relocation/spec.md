### Requirement: The top-level `mo workflow` group owns WorkflowRun, not WorkflowProfile

After this change the top-level `mo workflow` command group SHALL own WorkflowRun commands (the `workflow-run-control` and `workflow-run-reads` surfaces). The group SHALL NOT carry WorkflowProfile semantics — the overloaded `mo workflow list` (WorkflowProfile listing) is relocated under `mo project workflow profile`, so that `workflow` has a single owner (the core-domain aggregate root) and `docs/cli-reference.md` no longer needs to patch the distinction.

#### Scenario: mo workflow no longer carries profile listing

- **WHEN** the top-level `mo workflow` group is introspected after this change
- **THEN** the group SHALL expose the WorkflowRun control and read subcommands
- **AND** SHALL NOT expose a `list` subcommand that returns WorkflowProfiles

### Requirement: WorkflowProfile listing is relocated to `mo project workflow profile list`

The WorkflowProfile listing — both the plain (`--described` absent) and described (`--described`) forms — SHALL be available at `mo project workflow profile list`, with behavior identical to the previous `mo workflow list`: same project resolution (`--project` / `--project-id`), same `--described` flag semantics (id + display name + description, no `suitable_for` line), same active-project fallback with a degraded stderr note, same conflict handling when `--project` and `--project-id` disagree, and same `-o` output-format handling. The `profile` subgroup SHALL sit alongside the existing `template` and `config` subgroups under `mo project workflow`, reflecting that a profile is project-owned configuration.

#### Scenario: Described profile listing under the new path

- **WHEN** a caller runs `mo project workflow profile list --described --project <projectId>`
- **THEN** the command SHALL send the same request the previous `mo workflow list --described --project <projectId>` sent
- **AND** SHALL render id, display name, and description (without a `suitable_for` line)

#### Scenario: Plain profile listing under the new path

- **WHEN** a caller runs `mo project workflow profile list --project <projectId>`
- **THEN** the command SHALL send the same request the previous `mo workflow list --project <projectId>` sent

#### Scenario: Active-project fallback and conflict behavior carry over

- **WHEN** a caller runs `mo project workflow profile list` with no project flags but an active project selected
- **THEN** the command SHALL use the active project (or, with no resolvable project, fall back to the unfiltered listing with a degraded stderr note)
- **AND** conflicting `--project` / `--project-id` SHALL fail locally without making a request

#### Scenario: profile sits beside template and config

- **WHEN** the `mo project workflow` group is introspected
- **THEN** it SHALL expose `profile`, `template`, and `config` subgroups as peers

### Requirement: `design/cli.md` records the naming-ownership and output-format principles

A new `design/cli.md` SHALL record two decisions that this change relies on. (a) **Naming ownership**: `mo workflow` denotes WorkflowRun (the core-domain aggregate root); WorkflowProfile lives under `mo project workflow profile` because a profile is project-owned configuration — sub-resources hang under the parent that owns them. (b) **Output format never creates a command**, and output-format / subresource / associated-resource are three distinct categories that MUST NOT be mixed: an output format (`-o yaml/json/table`) renders the same resource and never spawns its own command (hence no `mo workflow yaml`); a subresource has its own resource path and its own command or `--subresource` flag; an associated resource is a one-to-many child collection with its own list command. `design/cli.md` is the durable home for these rules so later CLI work is bound by them.

#### Scenario: design/cli.md exists and states naming ownership

- **WHEN** `design/cli.md` is read after this change
- **THEN** it SHALL state that `mo workflow` owns WorkflowRun and that WorkflowProfile lives under `mo project workflow profile`
- **AND** SHALL give the rationale (core-domain aggregate root; profile is project-owned configuration)

#### Scenario: design/cli.md states the output-format-vs-subresource-vs-associated-resource rule

- **WHEN** `design/cli.md` is read after this change
- **THEN** it SHALL state that output format never creates a command
- **AND** SHALL distinguish output format, subresource, and associated resource as three non-mixable categories
- **AND** SHALL cite `show -o yaml` (no separate `yaml` command) as the canonical example

### Requirement: `docs/cli-reference.md` reflects the new surface and migrates the old path

`docs/cli-reference.md` SHALL be updated to document the new `mo workflow` (WorkflowRun) command surface and the `mo project workflow profile list` relocation. The old `mo workflow list` path SHALL be explicitly marked as migrated to `mo project workflow profile list`, so users of the previous path are guided to the new one. The reference SHALL keep `mo project workflow template` / `mo project workflow config` documentation unchanged.

#### Scenario: The reference documents the new workflow surface

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** it SHALL document the `mo workflow` control and read subcommands addressing a WorkflowRun by id

#### Scenario: The reference marks the old path as migrated

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** the previous `mo workflow list` path SHALL be marked as migrated to `mo project workflow profile list`
- **AND** the `mo project workflow profile` subgroup SHALL be documented alongside `template` and `config`

### Requirement: The relocation is the only breaking surface; existing project-workflow commands are untouched

This change's only command-path break is the `mo workflow list` → `mo project workflow profile list` relocation. The existing `mo project workflow template` and `mo project workflow config` subgroups SHALL remain unchanged in path, flags, and behavior. No schema migration and no irreversible action is introduced; the relocated path is documented in the release/changelog as a command-path migration.

#### Scenario: template and config are unchanged

- **WHEN** `mo project workflow template ...` and `mo project workflow config ...` are exercised after this change
- **THEN** both SHALL behave identically to before this change (same paths, flags, and outputs)

#### Scenario: Only the profile-listing path changes

- **WHEN** the set of command-path changes introduced by this change is reviewed
- **THEN** the only relocated path SHALL be the WorkflowProfile listing (`mo workflow list` → `mo project workflow profile list`)
- **AND** no other existing command path SHALL be removed or renamed
