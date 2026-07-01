## ADDED Requirements

### Requirement: Coder-agent skills are a contract-bearing surface aligned to the real command surface

The coder-agent skill guidance shipped from `packages/cli/Mohist.Cli/skill-data/` (consumed via `mo skills get <name>`) SHALL be treated as a contract-bearing surface that MUST stay aligned with the real `mo` CLI / API command surface as it actually behaves today. Skill content accuracy is itself a requirement, distinct from `update-runtime-consistency`, which only verifies that `SKILL.md` files exist in the managed cache — it does not verify their correctness. Where a skill references CLI commands, those references SHALL match commands the CLI actually provides; where the CLI omits a capability, the skill SHALL NOT advertise it as a CLI entry.

#### Scenario: Skill command references match the real CLI

- **WHEN** a coder-agent skill references a `mo` command by name
- **THEN** the referenced command SHALL exist and behave as documented in the real `mo` CLI

#### Scenario: Existence check does not imply correctness

- **WHEN** the `update-runtime-consistency` managed-skill-assets check resolves to `Pass`
- **THEN** that result SHALL verify only that `SKILL.md` files are present
- **AND** SHALL NOT be taken as evidence that the skill content is accurate or current

### Requirement: Epic skill documents the autopilot lifecycle and recommends self-directed progression

The epic guidance skill (`mohist-create-epic`) SHALL document the full autopilot lifecycle: `mo epic start`, `mo epic pause`, and `mo epic resume`, including their effects on autonomous issue progression. The skill SHALL document the idempotency semantics of `mo epic start` (starting an already-running epic does not error) and SHALL cover the running-but-idle state (all member issues complete, awaiting the next). The skill SHALL recommend autopilot self-directed progression as the preferred path over manually starting issues one by one. The skill SHALL NOT describe the epic as a non-executing organizer or state that epics do not participate in workflow execution.

#### Scenario: Autopilot start, pause, and resume are documented

- **WHEN** an agent reads the epic guidance skill
- **THEN** it SHALL find `mo epic start`, `mo epic pause`, and `mo epic resume` documented with their effects

#### Scenario: Autopilot is recommended over manual per-issue starts

- **WHEN** an agent reads the epic guidance skill's lifecycle guidance
- **THEN** the skill SHALL recommend autopilot self-directed progression over manually starting each member issue

#### Scenario: Idempotency and running-but-idle states are covered

- **WHEN** an agent reads the epic guidance skill's autopilot guidance
- **THEN** it SHALL find the idempotency semantics of `mo epic start`
- **AND** SHALL find guidance for the running-but-idle state

#### Scenario: Stale non-executing framing is removed

- **WHEN** an agent reads the epic guidance skill
- **THEN** it SHALL NOT find any statement that the epic does not participate in workflow execution

### Requirement: Dispatcher skill surfaces the complete issue and epic lifecycle command set

The dispatcher skill (`mohist`) SHALL surface the complete issue lifecycle command set — `start`, `approve`, `reject`, `retry`, `rerun`, `stop`, `force-stop`, `resume`, `rebase`, and `close` — so the agent can drive an issue through its full lifecycle directly rather than guessing or falling back to a subset. The dispatcher skill SHALL additionally surface the epic autopilot lifecycle commands `mo epic start`, `mo epic pause`, and `mo epic resume`. The partial cheat-sheet (`show|list|start|approve|close` only) SHALL be replaced by this complete surface.

#### Scenario: Complete issue lifecycle command surface is surfaced

- **WHEN** an agent reads the dispatcher skill's CLI command guidance
- **THEN** it SHALL find `reject`, `retry`, `rerun`, `stop`, `force-stop`, `resume`, and `rebase` documented alongside `start`, `approve`, and `close`

#### Scenario: Epic autopilot commands are surfaced

- **WHEN** an agent reads the dispatcher skill's CLI command guidance
- **THEN** it SHALL find `mo epic start`, `mo epic pause`, and `mo epic resume`

### Requirement: Operations-skill decision is resolved and recorded

The decision of whether to introduce a dedicated `mohist-operate` scenario skill (covering issue/epic start, approve, reject, retry, stop, resume lifecycle) versus keeping these operational flows inside the dispatcher skill SHALL be resolved and recorded in the change. If the decision favors a dedicated operations skill, its minimum viable content SHALL be shipped under `packages/cli/Mohist.Cli/skill-data/` and registered (including any `manifest.json` entry); if the decision favors keeping the flows in the dispatcher, the dispatcher content SHALL be the single source for those lifecycle commands.

#### Scenario: Decision is recorded

- **WHEN** the change is reviewed
- **THEN** the operations-skill decision (introduce a dedicated operate skill, or keep flows in the dispatcher) SHALL be documented in the change

#### Scenario: Dedicated operate skill, if chosen, ships minimum viable content

- **WHEN** the decision favors introducing a dedicated `mohist-operate` skill
- **THEN** the skill SHALL exist under `packages/cli/Mohist.Cli/skill-data/` with minimum viable content
- **AND** SHALL be registered in the skill manifest

### Requirement: Skill source edits propagate to the managed cache via skill sync

When skill source under `packages/cli/Mohist.Cli/skill-data/` is edited, the change SHALL run the skill sync so that the managed skill cache reflects the edited source. The `mo skills get <name>` output SHALL match the edited source `SKILL.md` content byte-for-byte (modulo formatting normalization performed by sync).

#### Scenario: Source and managed cache agree after sync

- **WHEN** skill source is edited and the skill sync is run
- **THEN** `mo skills get <name>` output SHALL match the source `SKILL.md` content

### Requirement: Display-surface vs functional-entry boundary is recorded as a standing convention

The standing boundary rule — display / read-only surfaces stay Web-only, while functional entry points get `mo` CLI and coder-agent skill entries — SHALL be recorded in `design/conventions.md` as the test for what gets a CLI/skill entry versus what stays Web-only. This rule SHALL be the standing reference for deciding, for any future capability, whether it warrants a CLI and/or skill entry or remains Web-only.

#### Scenario: Boundary rule is present in conventions

- **WHEN** a reader consults `design/conventions.md`
- **THEN** it SHALL find the display-surface vs functional-entry boundary rule recorded
- **AND** the rule SHALL serve as the standing test for CLI/skill scope decisions
