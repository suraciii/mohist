### Requirement: Versioned runtime catalog entries separate variants from reasoning efforts

The runtime catalog wire contract SHALL carry, per runtime entry, the
append-only fields `reasoningEfforts` (per model), `supportsReasoningEffort`,
`complete`, and `capabilityRevision`, beside the existing `models` and
`variants`. The `variants` map SHALL contain only true variants. An entry
without `complete` and `capabilityRevision` MUST NOT be treated as
authoritative evidence of capability support.

#### Scenario: Pi publishes thinking levels as reasoning efforts

- **WHEN** the Pi runtime registers its catalog
- **THEN** each model's native thinking levels MUST be published under
  `reasoningEfforts`
- **AND** the `variants` map MUST NOT contain any thinking level

#### Scenario: OpenCode reports variant-only support

- **WHEN** the OpenCode runtime registers its catalog
- **THEN** it MUST report `supportsReasoningEffort=false` with its true
  variants preserved
- **AND** it MUST NOT publish reasoning efforts

#### Scenario: Legacy entries are not proof of support

- **WHEN** a catalog entry arrives without `complete` or `capabilityRevision`
- **THEN** capability decisions MUST treat the entry as non-authoritative

#### Scenario: Capability revision identifies the producing catalog

- **WHEN** a runtime's capability changes and it re-registers
- **THEN** the new entry MUST carry a new immutable `capabilityRevision`
- **AND** revisions recorded in earlier snapshots MUST remain identifiable

### Requirement: A pure resolver returns typed dispositions for the frozen tuple

The Server SHALL resolve the frozen tuple `(runtime, model, reasoningEffort,
variant)` together with the catalog's `capabilityRevision` using a pure
resolver that returns exactly one disposition: `supported`, `needs-setup`,
`unavailable`, `unsupported_execution_configuration`, or
`incompatible_execution_configuration`. The resolver MUST NOT mutate owner
state, claim work, or execute anything.

#### Scenario: Supported tuple

- **GIVEN** a complete catalog revision lists the tuple's model, effort, and
  variant
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `supported` with the compatible runner identity

#### Scenario: Missing or incomplete catalog

- **GIVEN** the catalog is absent, incomplete, or carries no matching
  capability revision
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `needs-setup`
- **AND** the disposition MUST NOT be terminal for the work

#### Scenario: Runtime known but not ready

- **GIVEN** the tuple's runtime is known but not ready for admission
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `unavailable`
- **AND** the work MUST remain pending

#### Scenario: Runtime explicitly does not support effort

- **GIVEN** a complete catalog reporting `supportsReasoningEffort=false` and a
  tuple with an explicit effort
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `unsupported_execution_configuration`

#### Scenario: Complete catalog lacks a tuple member

- **GIVEN** a complete catalog that explicitly lacks the tuple's model,
  effort, or variant
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `incompatible_execution_configuration`
- **AND** the frozen tuple MUST be preserved in the failure evidence

#### Scenario: Unset effort imposes no requirement

- **GIVEN** a tuple with no effort on a runtime reporting
  `supportsReasoningEffort=false`
- **WHEN** the resolver evaluates the tuple
- **THEN** the absent effort MUST NOT produce an explicit rejection

### Requirement: Only supported tuples are admitted

Admission SHALL consume the resolver against the same runner catalog snapshot
that produces the dispatch. Only `supported` MAY be admitted; `needs-setup`
and `unavailable` MUST leave the work pending; an explicit rejection MUST
become a deterministic preflight failure recorded with the frozen tuple.

#### Scenario: Absent catalog keeps work pending

- **WHEN** a pending job's required catalog is missing or incomplete
- **THEN** no dispatch may be created
- **AND** no terminal failure may be recorded for the job

#### Scenario: Explicit rejection fails deterministically

- **WHEN** the resolver returns `unsupported_execution_configuration` or
  `incompatible_execution_configuration`
- **THEN** the work MUST fail as a preflight failure carrying the frozen tuple
  and a matching failure category
- **AND** re-evaluating the same tuple against the same catalog MUST reproduce
  the same disposition

### Requirement: Claims are fenced by an immutable capability expectation

AgentJob and Workflow claims SHALL be conditional on an immutable capability
expectation — the frozen tuple plus its capability revision. A claim MUST be
granted only when the runner's current catalog still satisfies the
expectation, so the tuple cannot go stale between resolution and claim.

#### Scenario: Matching catalog grants the claim

- **GIVEN** the runner's current catalog revision still lists the frozen tuple
- **WHEN** the conditional claim is attempted
- **THEN** the claim MUST be granted and the dispatch delivered

#### Scenario: Changed catalog refuses the claim

- **GIVEN** the runner re-registered a catalog revision that no longer lists
  the frozen tuple
- **WHEN** the conditional claim is attempted
- **THEN** the claim MUST be refused
- **AND** the work MUST remain pending for later resolution against the new
  catalog

#### Scenario: A runner rejects a stale dispatch snapshot

- **GIVEN** a dispatch snapshot frozen at capability revision R arrives at a
  runner whose current catalog revision differs
- **WHEN** the runner validates the snapshot
- **THEN** it MUST reject the snapshot deterministically
- **AND** it MUST NOT execute the work with silently changed capability
  semantics

### Requirement: Native effort translation stays inside the runtime adapter

The canonical effort SHALL be translated to a runtime-native value only inside
the selected runtime adapter. The Pi adapter SHALL map the canonical effort to
its private thinking level; the OpenCode adapter MUST reject an explicit effort
as a configuration failure and MUST NOT silently drop it or fold it into the
model/variant path. No runtime MAY receive another runtime's native value.

#### Scenario: Pi applies the effort as a thinking level

- **GIVEN** a supported Pi tuple with effort `high` and no variant
- **WHEN** the Pi adapter prepares its native session
- **THEN** it MUST map `high` to its native thinking level privately
- **AND** the applied effort MUST be reported in the execution evidence

#### Scenario: Pi never applies a variant as a thinking level

- **GIVEN** a Pi dispatch carrying a variant but no effort
- **WHEN** the Pi adapter prepares its native session
- **THEN** it MUST NOT apply the variant to the thinking-level input

#### Scenario: OpenCode rejects an explicit effort

- **GIVEN** an OpenCode dispatch carrying an explicit reasoning effort
- **WHEN** the executor runs the turn
- **THEN** it MUST fail with the `unsupported_execution_configuration` category
- **AND** the effort MUST NOT be appended to the model id, written to the
  variant, or silently ignored

#### Scenario: Native values do not cross runtimes

- **GIVEN** a Pi-native thinking-level name
- **WHEN** any non-Pi adapter builds its execution request
- **THEN** the native name MUST NOT appear in its model, variant, or effort
  fields
