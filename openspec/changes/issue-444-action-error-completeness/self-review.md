# Self-Review

## Scope

This change repairs one manifest-authority gap for concrete `mohist/opencode` Action errors and aligns its documentation. It does not alter reserved platform ownership, generic result validation, runtime diagnostic vocabulary, Pi behavior without evidence, Server/API schemas, persistence, or Workflow recovery semantics.

## Findings

- The Action-owned vocabulary is explicit: OpenCode adds `execution-unavailable`, `skill-not-found`, `provider-quota-exhausted`, and `unsupported-execution-configuration`; Pi adds `skill-not-found`, `provider-quota-exhausted`, `incompatible-runtime`, and `interrupted`.
- The established `unsupported_execution_configuration` spelling remains limited to runtime diagnostics and recorded execution evidence, with one required mapping at the Action boundary.
- The Skill resolver's `skill_not_found` source category maps to `skill-not-found` without a validator exception.
- No validator exception or global error allowlist is proposed.
- Truly undeclared ActionResult codes still normalize to `unexpected-error`.
- Reserved `invalid-input`, `unexpected-error`, and `timeout` remain platform-owned and absent from both manifests.
- The OpenCode and Pi additions are limited to statically evidenced current production Action paths; unrelated Actions and speculative parity remain excluded.
- Documentation, design, spec, tasks, and focused tests all describe the same code boundary.
- Future implementation tasks intentionally remain `passes: false`; no product or test files are changed in this spec-only PR.

## Residual Risks

- A future Action projection could bypass a runtime/resolver-to-Action mapping. Tests must cover both source category and normalized ActionResult spelling.
- Existing consumers that inspect runtime diagnostics must continue to receive established source categories; changing those vocabularies is outside scope.
- The exact complete OpenCode and Pi Action error inventories must be checked against all current production emission branches during implementation.

## Verdict

No blocking finding. The artifacts are self-contained, KISS-scoped, and ready for future implementation and focused validation.
