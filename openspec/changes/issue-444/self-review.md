# Self-Review - Issue #444 Plan

Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`, `specs/action-manifests/spec.md`, and `specs/action-input-validation/spec.md` under `openspec/changes/issue-444/`, against the current issue and implementation surfaces.

## Findings

### F1 (blocker): The input type contract is knowingly unresolved

`specs/action-manifests/spec.md:3` requires every input to declare exactly one JSON type, but `design.md:159` records that `mohist/opencode.prompt` currently accepts both strings and objects and leaves the representation open. Both object prompt loaders and string prompts are shipped behavior. `tasks.json:6-23` defers the product-language decision to build task T-001, so the approved specs would not be the contract that implementation follows.

Resolve this before build approval: choose the prompt representation, define its manifest and wire encoding, add normative scenarios for every preserved prompt form, and update the design. If finite unions are selected, the generic type/default validation requirements must describe unions consistently rather than making a one-field exception. T-001 should then be removed or converted from contract repair into implementation work.

### F2 (high): T-002 is not independently deliverable before T-003

`design.md:40-48` defines Action execution functions as receiving `ValidatedActionContext`, with `with` narrowed to validated input. T-002 promises that typed context and switches all task/check call sites to the new definitions (`tasks.json:26-48`), but actual validation is deferred to dependent T-003 (`tasks.json:51-74`). T-002 therefore must either invoke a validated signature with unvalidated values, add an unsafe cast, or duplicate part of T-003.

Merge T-002 and T-003, or keep T-002 limited to unused definition/catalog construction and move the production registry switchover, typed context, and all call-site changes into T-003. The resulting task boundary must leave every committed task in a truthful, usable state.

### F3 (high): Built-in preservation has no exhaustive pre-migration contract

`specs/action-manifests/spec.md:61-69` preserves behavior only for inputs "valid under their declared contracts," but this change creates those declarations. An incomplete manifest could therefore reject a currently accepted field and still satisfy the circular wording. The profile traversal in `design.md:128` and T-002 (`tasks.json:35-38`) checks shipped references, not the complete custom-profile surface. Current behavior includes aliases and conditional inputs such as `core/marker.contains`, implicit Variable fallbacks, and dynamically selected business error codes.

Define an auditable migration matrix for all 17 built-ins before implementation: every accepted top-level key and exact type, aliases, required/default/fallback behavior, public outputs, and every possible business error code. Require focused tests against that matrix. In particular, either type failure helpers by each manifest's error-code union or add an exhaustive test proving every code emitted through static and dynamic branches is declared; otherwise T-003 will silently turn missed declarations into `unexpected-error` and break recovery.

### F4 (medium): Result-contract failure behavior exists only in design/tasks

The normative error spec says Action business failures must use declared codes (`specs/action-manifests/spec.md:71-87`), but it does not state what happens when an Action returns an undeclared code, throws, or returns a malformed result. `design.md:102-108` and T-003 (`tasks.json:54-64`) introduce normalization to `unexpected-error`, task recovery eligibility, and row-level check handling without corresponding scenarios.

Add normative task and individual-check scenarios for undeclared business codes, thrown exceptions, malformed results, `unexpected-error` normalization, recovery eligibility, row-level check errors, and the unchanged aggregate `check-failed` verdict. This prevents implementers from choosing incompatible failure boundaries while still satisfying the current prose.

### F5 (medium): Runner verification omits test-source and boundary guards

T-002 and T-003 require production typecheck plus raw Vitest (`tasks.json:38` and `tasks.json:64`). Both tasks substantially rewrite test registries, but `packages/runner/package.json` places test-source typechecking and repository boundary/file-budget checks under `test:ci`, not `test`.

Require `npm run test:ci -w packages/runner` for both tasks, or explicitly require `typecheck:tests` and `check:test-boundaries` in addition to the current commands. Keep focused tests in each task as already planned.

## Coverage Summary

The issue's visible behaviors are otherwise represented: unknown, missing, and wrong-type fields produce actionable `invalid-input`; defaults are applied; tombstones distinguish removed Actions; catalog publication is separated from Profile validation; and the declared non-goals are respected. The task dependency graph is acyclic and T-004 can run in parallel with dispatch validation once a usable manifest registry exists. These strengths do not offset the unresolved schema and invalid T-002/T-003 delivery boundary.

## Verdict

The plan is not ready to build. The contract must be finalized and the task graph/test guarantees corrected before implementation begins.

<promise>FAIL</promise>
