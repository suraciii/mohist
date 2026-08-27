# Self-Review

## Scope

This change completes the manifest-authority contract for the current `mohist/opencode` and `mohist/pi` production Action paths and aligns the two concrete Action pages plus shared Action Contracts documentation. It does not alter reserved platform ownership, generic result validation, runtime diagnostic vocabulary, Server/API schemas, persistence, or Workflow recovery semantics.

## Findings

- The complete OpenCode and Pi manifest inventories are explicit, including existing valid entries and the newly evidenced omissions.
- The shared `docs/actions/README.md` boundary is included because it enumerates platform and shared Action semantics; its shared inventory is the exact intersection of the two concrete manifests.
- The established `unsupported_execution_configuration` spelling remains limited to runtime diagnostics and recorded execution evidence, with one required mapping to kebab Action code `unsupported-execution-configuration`.
- The Skill resolver's `skill_not_found` source category maps to `skill-not-found` without a validator exception.
- No validator exception or global Action allowlist is proposed; truly undeclared codes still normalize to `unexpected-error`.
- Reserved `invalid-input`, `unexpected-error`, and `timeout` remain platform-owned and absent from both manifests.
- The OpenCode and Pi additions are limited to statically evidenced current production Action paths; unrelated Actions and speculative parity remain excluded.
- Exact focused Runner tests, typechecks, docs, architecture, fast, and full verification commands are recorded in `tasks.json`.
- Future implementation tasks intentionally remain `passes: false`; no product, test, or documentation implementation files are changed in this spec-only repair.

## Residual Risks

- A future Action projection could bypass a runtime/resolver-to-Action mapping. Tests must cover both source category and normalized ActionResult spelling.
- Existing consumers that inspect runtime diagnostics must continue to receive established source categories; changing those vocabularies is outside scope.
- The exact complete OpenCode and Pi Action error inventories must be checked against all current production emission branches during implementation.

## Verdict

No blocking finding. The artifacts are self-contained, include all three documentation authority pages, preserve complete evidenced inventories and vocabulary boundaries, and require strict focused commands without a global validator exception.
