# Action Error Completeness

## Requirements

### Requirement: Agent Action manifests declare every evidenced Action-owned emitted code

The `mohist/opencode` and `mohist/pi` Action manifests MUST declare every statically evidenced non-reserved business error code reachable from their current production Action boundaries. Their complete Action-owned inventories MUST be:

- OpenCode: `runtime-unavailable`, `session-workspace-mismatch`, `session-binding-failed`, `session-reporting-failed`, `runtime-session-missing`, `unavailable-runtime`, `incompatible-runtime`, `permission-required`, `interrupted`, `generation-drain-timeout`, `turn-failed`, `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`.
- Pi: `runtime-unavailable`, `session-workspace-mismatch`, `session-binding-failed`, `session-reporting-failed`, `runtime-session-missing`, `unavailable-runtime`, `turn-failed`, `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`.

Both manifests MUST continue to exclude the reserved platform codes `invalid-input`, `unexpected-error`, and `timeout`, and MUST use the existing lowercase kebab-case error-code grammar. Existing valid entries MUST remain present.

#### Scenario: Complete OpenCode manifest inventory

- **WHEN** the Runner loads the `mohist/opencode` manifest
- **THEN** its Action-owned error declarations MUST contain exactly the complete OpenCode inventory listed above
- **AND** they MUST include `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`
- **AND** they MUST NOT include `execution_unavailable`, `skill_not_found`, or `unsupported_execution_configuration`
- **AND** they MUST NOT include `invalid-input`, `unexpected-error`, or `timeout`

#### Scenario: Complete Pi manifest inventory

- **WHEN** the Runner loads the `mohist/pi` manifest
- **THEN** its Action-owned error declarations MUST contain exactly the complete Pi inventory listed above
- **AND** they MUST include `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`
- **AND** they MUST NOT include `skill_not_found`
- **AND** they MUST NOT include `invalid-input`, `unexpected-error`, or `timeout`

### Requirement: Declared Action errors survive standard normalization

Standard Action result normalization MUST treat the selected manifest as the authority for Action-owned errors. A returned declared code MUST pass through unchanged for the Action that declares it, including `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, `unsupported-execution-configuration`, `incompatible-runtime`, and `interrupted`. The normalizer MUST remain generic and MUST NOT add a global list or an Action-specific exception.

#### Scenario: Preserve all declared OpenCode errors

- **WHEN** `mohist/opencode` returns a result with any code in its complete manifest inventory
- **THEN** standard result normalization MUST retain that returned code unchanged
- **AND** recovery matching MUST receive that same structured code

#### Scenario: Preserve all declared Pi errors

- **WHEN** `mohist/pi` returns a result with any code in its complete manifest inventory
- **THEN** standard result normalization MUST retain that returned code unchanged
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
- **THEN** standard normalization MUST replace the code with `unexpected-error`
- **AND** the original human-readable message MUST remain available for diagnosis

#### Scenario: Reserved platform ownership remains separate

- **WHEN** either Agent Action returns a platform failure with code `invalid-input`, `unexpected-error`, or `timeout`
- **THEN** standard normalization MUST preserve the platform code for its platform-defined condition
- **AND** neither Agent manifest MUST claim ownership of that code

### Requirement: Action documentation matches manifest and platform authority

`docs/actions/opencode.md` MUST list exactly the complete OpenCode Action-owned inventory. `docs/actions/pi.md` MUST list exactly the complete Pi Action-owned inventory. Shared `docs/actions/README.md` MUST remain authoritative for platform ownership and MUST enumerate the shared Agent Action business-code intersection: `runtime-unavailable`, `session-workspace-mismatch`, `session-binding-failed`, `session-reporting-failed`, `runtime-session-missing`, `unavailable-runtime`, and `turn-failed`. It MUST also retain platform-owned `invalid-input`, `unexpected-error`, and `timeout` without claiming them as Action-owned.

The OpenCode page MUST separately explain that runtime diagnostics and recorded execution evidence may use `unsupported_execution_configuration`; both concrete pages MUST distinguish source resolver category `skill_not_found` from ActionResult/recovery code `skill-not-found` where that category is exposed. No page may present an underscore category as a manifest-owned ActionResult code.

#### Scenario: Documentation exposes each authority-owned vocabulary

- **WHEN** a user reads either concrete Agent Action error-code section and the shared Action Contracts error section
- **THEN** each page MUST show exactly the codes owned by its authority boundary
- **AND** the concrete pages MUST show the complete inventories above
- **AND** the shared page MUST show exactly the shared intersection and reserved platform ownership
- **AND** no page MUST list `unsupported_execution_configuration` or `skill_not_found` as manifest-owned ActionResult codes
- **AND** the shared platform ownership MUST link or point to the shared Action Contracts documentation

#### Scenario: Documentation explains diagnostic spelling

- **WHEN** a user investigates an unsupported reasoning-effort or missing-Skill failure
- **THEN** the documentation MUST explain the source runtime/resolver category when it differs from the ActionResult code
- **AND** it MUST state that the ActionResult/recovery codes are `unsupported-execution-configuration` and `skill-not-found` respectively

### Requirement: Agent Action completeness stays evidence-bounded

The implementation MUST audit only the current production `mohist/opencode` and `mohist/pi` ActionResult emission paths, their Agent-turn capability projections, runtime/resolver mappings, manifests, documentation, and focused tests. It MUST address the evidenced omissions `execution-unavailable`, `skill_not_found`, `provider-quota-exhausted`, `unsupported_execution_configuration`, `incompatible-runtime`, and `interrupted` exactly as specified, while preserving the complete inventories above and excluding unrelated Actions and speculative parity.

#### Scenario: Evidence inventory is complete for current paths

- **WHEN** the audit inspects the OpenCode and Pi Action implementations and their reachable capability/runtime/resolver branches
- **THEN** it MUST record the producer path and canonical ActionResult code for each newly declared omission
- **AND** it MUST prove that the final manifest sets cover every non-reserved code reachable from those paths
- **AND** it MUST verify concrete pages and the shared README agree with those sets
- **AND** it MUST not add codes from unrelated Actions or unreachable internal branches

#### Scenario: No speculative Action parity is added

- **WHEN** a code belongs only to another Action, an unreachable helper, or an unobserved future runtime branch
- **THEN** it MUST remain outside this capability
- **AND** generic normalization MUST continue to classify that code as undeclared for the selected Agent Action

### Requirement: Focused tests use exact strict assertions without validator exceptions

The implementation MUST add or update focused Runner tests that assert complete manifest arrays, explicit source-to-Action mappings, normalization pass-through for every declared code, `unexpected-error` fallback for a truly undeclared code, and documentation authority alignment. The tests MUST use fakes for runtime/connection dependencies and MUST NOT add network, process, database, or system-service dependencies. No global validator exception is permitted.

#### Scenario: Focused verification command set

- **WHEN** the focused Action completeness suite is run
- **THEN** `npm run test:run -w packages/runner -- tests/opencode-action-turn.spec.ts src/actions/pi.test.ts tests/action-registry.test.ts tests/define-action.test.ts` MUST pass
- **AND** `npm run typecheck -w packages/runner` MUST pass
- **AND** `npm run typecheck:tests -w packages/runner` MUST pass
- **AND** `npm run docs:check` MUST pass
- **AND** `npm run archtest` MUST pass
- **AND** `npm run test:fast` MUST pass
- **AND** `npm run verify` MUST pass
