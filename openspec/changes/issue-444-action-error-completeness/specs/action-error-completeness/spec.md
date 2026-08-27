# Action Error Completeness

## Requirements

### Requirement: Agent Action manifests declare every evidenced Action-owned emitted code

The `mohist/opencode` and `mohist/pi` Action manifests MUST declare every statically evidenced non-reserved business error code reachable from their current production Action boundaries. OpenCode MUST include `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration` in addition to its existing declared codes. Pi MUST include `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted` in addition to its existing declared codes. Both manifests MUST continue to exclude the reserved platform codes `invalid-input`, `unexpected-error`, and `timeout`, and MUST use the existing lowercase kebab-case error-code grammar.

#### Scenario: Manifest declares evidenced OpenCode omissions

- **WHEN** the Runner loads the `mohist/opencode` manifest
- **THEN** its Action-owned error declarations MUST include `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`
- **AND** they MUST NOT include `execution_unavailable`, `skill_not_found`, or `unsupported_execution_configuration`
- **AND** they MUST NOT include `invalid-input`, `unexpected-error`, or `timeout`

#### Scenario: Manifest declares evidenced Pi omissions

- **WHEN** the Runner loads the `mohist/pi` manifest
- **THEN** its Action-owned error declarations MUST include `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`
- **AND** they MUST NOT include `skill_not_found`
- **AND** they MUST NOT include `invalid-input`, `unexpected-error`, or `timeout`

#### Scenario: Existing Agent Action errors remain declared

- **WHEN** either Agent Action boundary can emit an already documented runtime, Session, reporting, permission, interruption, or turn failure
- **THEN** each such Action-owned code MUST remain present in that Action's manifest
- **AND** the completeness change MUST NOT remove or replace an existing valid manifest code

### Requirement: Declared Action errors survive standard normalization

Standard Action result normalization MUST treat the selected manifest as the authority for Action-owned errors. A returned declared code MUST pass through unchanged for either Agent Action, including `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, `unsupported-execution-configuration`, `incompatible-runtime`, and `interrupted`. The normalizer MUST remain generic and MUST NOT add a global list or an Action-specific exception.

#### Scenario: Preserve declared OpenCode errors

- **WHEN** `mohist/opencode` returns a result with code `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, or `unsupported-execution-configuration`
- **THEN** standard result normalization MUST retain the returned code unchanged
- **AND** recovery matching MUST receive that same structured code

#### Scenario: Preserve declared Pi errors

- **WHEN** `mohist/pi` returns a result with code `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, or `interrupted`
- **THEN** standard result normalization MUST retain the returned code unchanged
- **AND** recovery matching MUST receive that same structured code

### Requirement: Runtime and resolver vocabularies map deterministically at the Action boundary

Established runtime, resolver, diagnostic, and cross-system recorded execution categories MUST remain unchanged outside the ActionResult boundary. The Action boundary MUST map `unsupported_execution_configuration` to manifest-owned `unsupported-execution-configuration` and `skill_not_found` to manifest-owned `skill-not-found` before standard result normalization. `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted` already use canonical Action spelling and MUST remain unchanged. Every mapping MUST be explicit and deterministic.

#### Scenario: Map the established OpenCode underscore diagnostic category

- **WHEN** OpenCode rejects an explicit reasoning effort and emits runtime diagnostic or recorded category `unsupported_execution_configuration`
- **THEN** the Action boundary MUST return ActionResult code `unsupported-execution-configuration`
- **AND** the runtime diagnostic or recorded execution evidence MAY retain `unsupported_execution_configuration`
- **AND** no underscore code MUST be added to the Action manifest

#### Scenario: Map the Skill resolver category for both Actions

- **WHEN** the Skill resolver reports source category `skill_not_found` for OpenCode or Pi
- **THEN** the selected Action boundary MUST return ActionResult code `skill-not-found`
- **AND** the source resolver category MAY remain `skill_not_found` in diagnostics
- **AND** neither manifest MUST declare `skill_not_found`

#### Scenario: A bypassed mapping remains undeclared

- **WHEN** a code path returns `unsupported_execution_configuration` or `skill_not_found` directly as an ActionResult code without boundary mapping
- **THEN** standard normalization MUST treat it as undeclared
- **AND** it MUST normalize the result to `unexpected-error`

### Requirement: Truly undeclared Action codes remain unexpected errors

A non-reserved ActionResult error code that is absent from the selected Action manifest MUST normalize to `unexpected-error`. The normalizer MUST NOT accept it because it resembles a runtime diagnostic, a historical recorded category, or a code declared by another Action.

#### Scenario: Reject an arbitrary undeclared code

- **WHEN** either Agent Action returns an ActionResult with code `provider-temporarily-busy` and that code is not in its manifest
- **THEN** standard result normalization MUST replace the code with `unexpected-error`
- **AND** the original human-readable message MUST remain available for diagnosis

#### Scenario: Reserved platform ownership remains separate

- **WHEN** either Agent Action returns a platform failure with code `invalid-input`, `unexpected-error`, or `timeout`
- **THEN** standard normalization MUST preserve the platform code for its platform-defined condition
- **AND** neither Agent manifest MUST claim ownership of that code

### Requirement: Agent Action documentation matches manifest authority

`docs/actions/opencode.md` and `docs/actions/pi.md` MUST list the same Action-owned errors as their respective manifests. The OpenCode page MUST include `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`. The Pi page MUST include `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`. The OpenCode page MUST separately explain that runtime diagnostics and recorded execution evidence may use `unsupported_execution_configuration`; both pages MUST distinguish source resolver category `skill_not_found` from ActionResult code `skill-not-found` where that category is exposed.

#### Scenario: Documentation exposes the Action-owned vocabulary

- **WHEN** a user reads either concrete Agent Action error-code section
- **THEN** it MUST show every Action-owned code declared by that Action's manifest, including the newly evidenced codes
- **AND** it MUST NOT present `unsupported_execution_configuration` or `skill_not_found` as manifest-owned ActionResult codes
- **AND** it MUST link shared platform error ownership to the shared Action Contracts documentation

#### Scenario: Documentation explains diagnostic spelling

- **WHEN** a user investigates an unsupported reasoning-effort or missing-Skill failure
- **THEN** the documentation MUST explain the source runtime/resolver category when it differs from the ActionResult code
- **AND** it MUST state that the ActionResult/recovery codes are `unsupported-execution-configuration` and `skill-not-found` respectively

### Requirement: Agent Action completeness stays evidence-bounded

The future implementation MUST audit only the current production `mohist/opencode` and `mohist/pi` ActionResult emission paths, their agent-turn capability projections, runtime/resolver mappings, and focused tests. It MUST address the evidenced omissions `execution-unavailable`, `skill_not_found`, `provider-quota-exhausted`, `unsupported_execution_configuration`, `incompatible-runtime`, and `interrupted` exactly as specified, while excluding unrelated Actions and speculative parity. Shared codes already declared by each Action MUST remain unchanged.

#### Scenario: Evidence inventory is complete for current paths

- **WHEN** the audit inspects the OpenCode and Pi Action implementations and their reachable capability/runtime/resolver branches
- **THEN** it MUST record the producer path and canonical ActionResult code for each newly declared omission
- **AND** it MUST prove that the final manifest set covers every non-reserved code reachable from those paths
- **AND** it MUST not add codes from unrelated Actions or unreachable internal branches

#### Scenario: No speculative Action parity is added

- **WHEN** a code belongs only to another Action, an unreachable helper, or an unobserved future runtime branch
- **THEN** it MUST remain outside this capability
- **AND** generic normalization MUST continue to classify that code as undeclared for the selected Agent Action
