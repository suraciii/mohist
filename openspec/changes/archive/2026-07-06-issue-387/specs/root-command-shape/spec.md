### Requirement: The root command layer exposes only resource / resource-group commands plus the single controlled exception `mo info`

The root command's direct subcommands SHALL consist exclusively of resource or resource-group commands (e.g. `project`, `issue`, `workflow`, `server`, `runner`, `system`, `notification`, `repository`, `agent`, `epic`, `label`, `opencode`, `config`, `otel`, `skills`, `install`, `update`), plus exactly one controlled exception: `mo info`. `mo info` is the cross-resource, read-only, CLI-local diagnostics command and SHALL remain unchanged by this change. No bare verb SHALL hang directly off the root.

#### Scenario: the root help lists resources and the single info exception only

- **WHEN** a caller runs `mo --help` after this change
- **THEN** the listed top-level subcommands SHALL NOT include any of the bare verbs `status`, `logs`, `use`, or `notify`
- **AND** SHALL include `project`, `system`, `server`, and `notification` as resource / resource-group commands
- **AND** SHALL include `info` as the single controlled exception

#### Scenario: mo info is unchanged

- **WHEN** a caller runs `mo info` (or `mo info --verbose` / `mo info --json`) after this change
- **THEN** the CLI SHALL produce the same output and exit code it produced before this change
- **AND** the command's path, flags, and behavior SHALL be identical to the pre-change state

### Requirement: The five legacy bare-verb / misnamed paths are removed from the root

The five legacy root-level entries — `mo status`, `mo logs`, `mo use`, `mo notify`, and `mo system info` — SHALL no longer resolve as commands under their legacy paths after this change (subject to the uniform alias policy below). Each has been relocated to its owning resource: `mo project status`, `mo system logs`, (deleted in favor of) `mo project use`, `mo notification setup`, and `mo server info` respectively.

#### Scenario: the legacy bare verbs do not resolve at the root

- **WHEN** a caller runs `mo status` (or `mo logs`, or `mo use <project>`) after this change
- **THEN** the CLI SHALL fail to resolve the command at the root
- **AND** SHALL exit non-zero with a parse error, unless the legacy path is retained as a uniform alias per the alias-policy requirement

#### Scenario: the legacy misnamed groups do not resolve at the root

- **WHEN** a caller runs `mo notify setup` (or `mo system info`) after this change
- **THEN** the CLI SHALL fail to resolve the command at the root under its legacy name
- **AND** SHALL exit non-zero with a parse error, unless the legacy path is retained as a uniform alias per the alias-policy requirement

### Requirement: The legacy-path alias policy is uniform across all five migrations and recorded in the issue

Whether legacy paths are retained as transition aliases or removed outright SHALL be decided once and applied uniformly to all five migrations (`status`, `logs`, `use`, `notify setup`, `system info`). The implementation SHALL NOT adopt one strategy for some migrations and a different strategy for others. The chosen strategy SHALL be recorded in a comment on this issue so reviewers can verify uniformity.

#### Scenario: alias strategy is uniform

- **WHEN** the implementation of this change is reviewed
- **THEN** all five legacy paths SHALL either all resolve as aliases to their relocated targets (with identical behavior) or all fail to resolve
- **AND** the chosen strategy SHALL be documented in a comment on this issue

#### Scenario: aliases behave identically to canonical paths when retained

- **WHEN** the alias strategy retains a legacy path (e.g. `mo status` → `mo project status`)
- **THEN** invoking the legacy path SHALL produce byte-identical stdout, stderr, and exit code to invoking the canonical path
- **AND** the alias SHALL NOT introduce or remove any flag, default, or behavior

### Requirement: `docs/cli-reference.md` reflects the converged root layer and drops the five gap rows

`docs/cli-reference.md` SHALL be updated to match the converged command surface. The five rows in the实装差距 table that tracked this migration (`mo status`, `mo logs`, `mo use`, `mo notify setup`, `mo system info`) SHALL be removed, because the gap is closed by this change. The command-group sections (Project, 系统诊断 / System, Notification, Server) and any `mo --help` output example SHALL document the new paths (`mo project status`, `mo system logs`, `mo project use`, `mo notification setup`, `mo server info`). The doc's stated invariant — that the root layer carries only resources plus the single `mo info` exception — SHALL hold against the post-change command tree.

#### Scenario: the gap table no longer lists the five migrated paths

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** the实装差距 table SHALL NOT contain rows for `mo status`, `mo logs`, `mo use`, `mo notify setup`, or `mo system info`

#### Scenario: the command-group sections document the new paths

- **WHEN** `docs/cli-reference.md` is read after this change
- **THEN** it SHALL document `mo project status`, `mo system logs`, `mo notification setup`, and `mo server info` under their respective command-group sections
- **AND** SHALL identify `mo project use` as the single entry for setting the active project
- **AND** the root-layer help example SHALL list only resource / resource-group commands plus `mo info`
