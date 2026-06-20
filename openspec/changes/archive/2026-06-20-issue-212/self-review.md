# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: blocking-was / info-now
  Scope: consistency | feasibility
  Evidence: Design D3 originally recommended evolving `coderModels` / `/opencode/models` from `models: string[]` to structured items `Array<{ id, variants }>`. That choice had two defects found during review: (1) it broke an unmentioned existing consumer — the runner-status Web view (`RunnerStatusRow.coderModels: string[]`, `packages/web/src/entities/runner/model/types.ts:30`, rendered via `RunnerList.tsx`) and the runner-status server DTO — and no task covered updating that surface; (2) it contradicted the http-api spec's "same shape" backward-compatibility scenario. Repaired by adopting D3's already-listed additive alternative: keep `coderModels`/`models: string[]` unchanged and add a parallel variants map (`coderModelVariants` on registration, `modelVariants` on the endpoint). This leaves the runner-status consumers untouched, honors the proposal's "additive, non-breaking" commitment, and makes the http-api spec literal.
  Files changed: `design.md` (D3 rewritten, D6 wording, Risks bullet, Migration steps), `tasks.json` (T-001/T-002/T-005 descriptions, acceptance criteria, notes), `specs/http-api/spec.md` (endpoint requirement + two scenarios made shape-agnostic: "associate with" instead of "include"/"represented with"), `proposal.md` (Impact line: "gain a per-model variants map alongside the existing string[]" instead of "change from a flat string[]").
  Verification: re-ran DAG/dependency validator — tasks.json still valid JSON, acyclic, all dependsOn lower-priority; `openspec validate issue-212` → "Change 'issue-212' is valid". coderModels: string[] consumers now require no changes, so the runner-status gap is closed.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-004 lists `dependsOn: ["T-001", "T-003"]`, but T-004 (runner delivery) consumes the variant only from the dispatched agent config (produced by T-003); it does not directly consume T-001's discovery output at delivery time (best-effort, no pre-validation per spec). The T-004→T-001 edge is therefore a soft ordering constraint, not a hard output dependency.
  SuggestedAction: Acceptable as-is (it is a valid, acyclic, lower-priority edge that keeps the runner discovery+delivery slice ordered). Drop T-001 from T-004 dependsOn during build if stricter minimization is desired.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design D5 / T-004 rely on opencode honoring the `provider/model:<variant>` id syntax across all providers. Confirmed for `reasoningEffort`-bearing variants via `opencode models --verbose`, but edge providers are not yet verified.
  SuggestedAction: T-004 already carries a "BUILD SPIKE FIRST" note; perform that spike early. The existing `applyRequestedModel` try/catch guarantees best-effort even if a provider does not honor the suffix, so this cannot flip a success to failure.
  Status: follow-up

## Coverage Summary

- All 8 issue acceptance criteria trace to proposal "What Changes", spec requirements, and tasks.
- All 11 delta spec requirements are covered by tasks (model-reasoning-variants ×4 distributed across T-001/T-003/T-004/T-005; agent-runtime ×2 → T-001/T-004; http-api ×2 → T-002/T-003; local-issue-store → T-003; web-ui → T-005; workflow-engine → T-003).
- Task granularity: 5 functional slices, no over-fine ("define interface"/"register DI"/standalone test) tasks; every task embeds its own test coverage.
- Dependencies: T-001[]; T-002[T-001]; T-003[]; T-004[T-001,T-003]; T-005[T-002,T-003] — acyclic, all edges to strictly lower priorities.

<promise>PASS</promise>
