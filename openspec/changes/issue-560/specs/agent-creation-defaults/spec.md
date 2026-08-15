### Requirement: Conflict-free derived naming

When the caller does not supply `name`, the Server SHALL derive a name for the
created Agent that is non-empty, unique among all Agents in the Project
(active and archived), and not a reserved built-in Agent name. Derivation
SHALL use the task when it yields a usable name and SHALL disambiguate
deterministically instead of failing. A task-first creation with an
unspecified name MUST NOT surface `AGENT_NAME_CONFLICT`.

#### Scenario: Two tasks derive the same base name

- **WHEN** two task-first creations without a name hint derive the same base name
- **THEN** the second creation derives a distinct, conflict-free name
- **AND** both Agents exist in the Project

#### Scenario: The derived name avoids reserved names

- **WHEN** the derived base name equals a reserved built-in Agent name
- **THEN** derivation disambiguates so the created Agent does not collide with the built-in catalog

### Requirement: Baseline description and Instructions are derived from the task

The created task-first Agent definition SHALL carry baseline Instructions
derived from the task (non-empty) and a baseline description identifying the
task the Agent was created for. Baseline Instructions are ordinary
Instructions: they are fixed into each AgentJob's launch-time snapshot and are
editable through the same definition editor as any other Agent. A task-first
Agent MUST NOT report the `instructions-missing` Readiness gap.

#### Scenario: The created definition is complete without caller input

- **WHEN** the caller supplies only a prompt
- **THEN** the created Agent has non-empty Instructions and a description
- **AND** its Readiness result contains no `instructions-missing` gap

### Requirement: One Project default execution configuration

A Project SHALL hold at most one default execution configuration consisting of
a Runtime, a Model, and an optional Variant. Setting a new default SHALL
replace the previous one. The Runtime SHALL be `opencode` or `pi`, the Model
SHALL use the `provider/model` form, and an invalid default SHALL be rejected
at configuration time. The configured default SHALL be readable through the
Project read surface.

#### Scenario: A valid default is stored and replaces the prior one

- **WHEN** the Project default is set to runtime `pi`, model `provider/model`, variant `high` after an earlier default was configured
- **THEN** the Project reports exactly that default and no other

#### Scenario: An invalid default is rejected

- **WHEN** the default is set with runtime `fast` or model `gpt`
- **THEN** the Server rejects the configuration with a validation error
- **AND** the previous default, if any, remains unchanged

### Requirement: One precedence rule resolves every execution field

Each execution field — Runtime, Model, and Variant — SHALL resolve by exactly
one precedence rule: the caller-supplied value first, then the Agent
definition value, then the Project default. An explicitly supplied invalid
value MUST NOT be masked by a lower-precedence source. A Runtime that resolves
from no source defaults to `opencode` under the existing rule.

#### Scenario: The Agent definition wins over the Project default

- **WHEN** an Agent definition names model `a/one` and the Project default names `b/two`
- **THEN** resolution uses `a/one`

#### Scenario: The Project default fills a definition gap

- **WHEN** an Agent definition omits the model and the Project default names `b/two`
- **THEN** resolution uses `b/two`

#### Scenario: A caller hint wins over both

- **WHEN** a task-first request supplies model `c/three` while the Project default names `b/two`
- **THEN** the created definition uses `c/three`

#### Scenario: An explicit malformed value is not masked

- **WHEN** an Agent definition carries a model without the `provider/model` form while the Project default names a valid model
- **THEN** the definition's malformed value remains a Readiness gap

### Requirement: Task-first creation materializes the resolved execution configuration

The created task-first Agent definition SHALL store the resolved execution
configuration — the Runtime, the Model, and the Variant when one resolves — so
the Agent is launchable immediately, independent of later Project-default
changes, and self-describing in Agent lists. A task-first creation whose
execution configuration cannot be resolved (no caller hints and no Project
default) SHALL be rejected before creation rather than producing a
Needs-setup Agent.

#### Scenario: The created definition carries the resolved configuration

- **WHEN** a task-first creation resolves runtime `pi` and model `provider/model` from the Project default
- **THEN** the created Agent definition stores those values and the Agent list shows them

#### Scenario: Readiness is never Needs setup from missing defaults

- **WHEN** a task-first creation succeeds
- **THEN** the created Agent's Readiness is `Ready` or `Unknown`
- **AND** it is never `Needs setup` caused only by missing defaults

### Requirement: Readiness rules for default-resolved and default-missing definitions

Structural Readiness evaluation SHALL resolve Model and Variant by Agent
definition, then Project default. When a configured default resolves a missing
Model or Variant, the `model-missing` and `variant-without-model` gaps MUST
NOT appear, and the conclusion becomes `Ready` or `Unknown` under the existing
history rules. When no default resolves them, the gaps remain `Needs setup`
with their actionable repair. The `runtime-invalid` and
`model-reference-malformed` gaps are definition errors and are unaffected by
any default.

#### Scenario: A default resolves the missing model

- **WHEN** an active Agent has no model configured and the Project default supplies one
- **THEN** Readiness reports `Ready` or `Unknown` and contains no `model-missing` gap

#### Scenario: A default resolves a variant without a model

- **WHEN** an Agent sets a variant but no model and the Project default supplies a model
- **THEN** Readiness contains no `variant-without-model` gap

#### Scenario: Without a default the gap remains

- **WHEN** an Agent has no model configured and the Project has no default execution configuration
- **THEN** Readiness remains `Needs setup` with the actionable `model-missing` gap

#### Scenario: Definition errors are not masked

- **WHEN** an Agent's configured runtime is unsupported and a Project default exists
- **THEN** Readiness keeps the `runtime-invalid` gap
