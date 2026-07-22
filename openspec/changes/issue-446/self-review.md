# Self-Review — issue-446

Reviewing the plan artifacts (`proposal.md`, `design.md`, `tasks.json`, `specs/profile-action-validation/spec.md`) against issue #446's acceptance criteria and non-goals.

## Acceptance-criteria coverage

| AC | Where covered | Status |
|---|---|---|
| AC1 — unknown `uses` rejected (task/check, action name, YAML path, Action source) | spec R2; design D1; tasks T-002 + T-003 | Covered |
| AC2 — unknown `with` / missing required / constant type rejected with field + reason | spec R4/R5/R6; design D6; task T-002 | Covered |
| AC3 — tombstone "removed" distinct from "unknown" | spec R3; design D1; task T-002 | Covered |
| AC4 — template inputs: field-name only at save; value type at dispatch | spec R7; design D5; task T-002; R10 boundary | Covered |
| AC5 — Definition/template rules only from #432; no second parser | spec R1 (scenario) + R9; design D1/D8; task T-002/T-003 | Covered |
| AC6 — no catalog ⇒ no false reject + outcome states skipped | spec R8; design D4; task T-003 | Covered |
| AC7 — dispatch fail-closed preserved | spec R10; design D8 + risk; task T-003 (regression AC) | Covered |

Non-goals (CLI stays catalog-free; no multi-runner merge; no manifest changes) are consistently reflected across proposal, design Non-Goals, and task scope. The `tasks.json` dependency graph is a valid DAG (T-001, T-002 independent; T-003 depends on both), every dependency points to a strictly-lower-priority task, and each task carries its own test coverage with no standalone test task. Spec format is compliant (10 requirements, 25 scenarios, all `####`, SHALL/MUST, no delta headers).

## Findings

### F1 (must-fix) — Spec R5 describes an impossible catalog state and contradicts dispatch semantics

**Where:** `specs/profile-action-validation/spec.md`, Requirement "Missing required inputs are rejected" — both the requirement text ("…a required input that `with` omits **and for which the catalog declares no default**…") and its scenario "required input with a catalog default is accepted when omitted".

**What is wrong:**

1. A catalog entry is **never** both required and defaulted. #444's manifest validation rejects this combination at definition time: `packages/runner/src/actions/define-action.ts:140-142` throws `"must not be both required and defaulted"`. Since the catalog is the projection of validated manifests, no real catalog contains a required-and-defaulted input. The "for which the catalog declares no default" qualifier is therefore meaningless, and the scenario cannot be constructed from a real catalog.
2. The scenario's expectation ("accepted when omitted") contradicts the dispatch behavior this change is explicitly built to mirror. At dispatch, the missing-required check runs *before* defaults are ever applied (`packages/runner/src/actions/input-validation.ts:52-61`), so an omitted required field is rejected regardless of any default. A faithful save-time validator (per design D6) would reject, not accept.

**Impact:** The implementer of T-002 codes tests against this spec. Read literally, the spec demands a test asserting "accepted" for a state the system forbids and that dispatch would reject — directly opposing design D6's mirror-the-Runner rule. This is a contradiction *within* the plan (spec vs design D6 vs #444 invariant).

**Suggested fix (for the fixer task):** Rewrite R5 so save mirrors dispatch — an omitted required input is rejected; defaults are a dispatch-time concern and not a save-time acceptance factor. Concretely: drop the "for which the catalog declares no default" qualifier from the requirement text, and replace the "required input with a catalog default" scenario with one that asserts an *optional* input (defaulted or not) may be omitted at save without error, plus the existing "omitted required input is rejected" scenario.

### Observations (non-blocking, no fix required to proceed)

- **O1 — Built-in-vs-catalog validity is asserted, not verified.** Design D8/risk states built-in profiles are "already valid against the catalog (migrated by #432/#444)", but there is no test asserting a built-in profile passes the new catalog check against a fixture catalog. A latent mismatch (e.g. a built-in `with` field not in the shipped manifest) would only surface at the first runner-assisted save. This is already listed as an Open Question ("Catalog-check coverage for built-ins in CI"); flagging only so it is not forgotten.
- **O2 — `null`-`RegisteredAt` selection edge.** Design D3 selects "max `RegisteredAt`", but `RunnerInfo.RegisteredAt` is nullable. The selection rule does not state how a null `RegisteredAt` participates in the max. Minor implementation detail for T-001; not plan-blocking.
- **O3 — required-but-explicitly-`null` has no dedicated scenario.** R6's general "null matches no kind" rule covers it implicitly (a required field present-but-null fails the kind check, mirroring dispatch `input-validation.ts:71-76`), so behavior is correct; an explicit scenario would just remove ambiguity. Not a blocker.

## Verdict

The plan is coherent across proposal/specs/design/tasks and covers all seven acceptance criteria with a sound task split. However, finding F1 is a concrete contradiction in the spec contract that T-002 builds against — it must be corrected before implementation so the save-time and dispatch-time required/default semantics agree.

<promise>FAIL</promise>
