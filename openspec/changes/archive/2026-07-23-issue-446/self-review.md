# Self-Review (re-run) — issue-446

Re-reviewing the plan artifacts (`proposal.md`, `design.md`, `tasks.json`, `specs/profile-action-validation/spec.md`) against issue #446 after the fix round. The prior review raised one must-fix (F1: a spec scenario describing an impossible required-and-defaulted catalog state that contradicted dispatch) plus three non-blocking observations; this run verifies those were resolved and scans for new issues.

## Prior findings — resolution status

- **F1 (was must-fix) — RESOLVED.** `specs/.../spec.md` Requirement "Missing required inputs are rejected" now mirrors dispatch: the requirement text states a required input that `with` omits is rejected considering only presence (defaults are a dispatch-time concern; a catalog input is never both required and defaulted). The impossible scenario was replaced by "an optional input may be omitted". The same defect was fixed consistently in `tasks.json` T-002 (acceptance criterion rewritten) and `design.md` D6 (null/required parenthetical corrected). A residue scan (`required input with a catalog default`, `for which the catalog declares no default`, `with no catalog default`) returns no matches in any plan artifact.
- **O2 — RESOLVED.** Design D3 now specifies that a runner with a null `RegisteredAt` is ordered after valued runners, so the "most recent" comparison is total and deterministic.
- **O3 — RESOLVED.** Spec R6 gained "an explicit null on a required input is rejected", mirroring dispatch where a present-but-null required value fails the kind check. The null matrix is now complete: required/omitted→reject, required/null→reject, optional/omitted→accept, optional/null→absent.
- **O1 — ADDRESSED.** The design risk wording was softened from "already valid" to "expected to be valid … not yet covered by a test", cross-referencing the existing Open Question on built-in catalog-coverage CI. This remains an acknowledged open question, not a defect.

## Acceptance-criteria coverage

All seven criteria are covered and trace to artifacts:

| AC | Coverage |
|---|---|
| AC1 unknown `uses` (task/check, action name, path, source) | spec R2; design D1; tasks T-002 + T-003 |
| AC2 unknown `with` / missing required / constant type | spec R4/R5/R6; design D6; task T-002 |
| AC3 tombstone "removed" vs "unknown" | spec R3; design D1; task T-002 |
| AC4 template inputs: name-only at save, type at dispatch | spec R7; design D5; tasks T-002; boundary R10 |
| AC5 Definition/template rules only from #432; no second parser | spec R1 + R9; design D1/D8; tasks T-002/T-003 |
| AC6 no catalog ⇒ no false reject + outcome states skipped | spec R8; design D4; task T-003 |
| AC7 dispatch fail-closed preserved | spec R10; design D8 + risk; task T-003 regression AC |

Non-goals (CLI stays catalog-free; no multi-runner merge; no manifest changes) are consistently reflected across proposal, design Non-Goals, and task scope.

## Mechanical verification

- `tasks.json` is valid JSON; 3 tasks; dependency graph is a valid DAG (T-001, T-002 independent; T-003 depends on both); every dependency points to a strictly-lower-priority task.
- Spec is format-compliant: 10 `### Requirement:` blocks, 26 `#### Scenario:` blocks, no malformed 3-hashtag scenarios, no `## ADDED/MODIFIED/REMOVED` headers, normative SHALL/MUST throughout.
- Each task carries its own test coverage; there is no standalone test task.
- Save-time and dispatch-time semantics are consistent across every rule (unknown/tombstoned `uses`, unknown field, missing required, type mismatch, optional-null, required-null, template-name-only) — verified against `packages/runner/src/actions/input-validation.ts` and `define-action.ts`.

## Verdict

The prior must-fix is fully resolved across spec, tasks, and design with no residue; the non-blocking observations are addressed; all acceptance criteria are covered; and the artifacts are mechanically sound. The plan is ready to build.

<promise>PASS</promise>
