# Self Review Report

## Result: PASS

Reviewed `proposal.md`, `design.md`, `tasks.json` against issue #308, plus the existing governing spec `openspec/specs/pr-first-workflow/spec.md` and the source under refactor (`packages/runner/src/actions/github-pr.ts`, 1379 lines). All ~50 symbols named in the plan were verified present in the monolith; the claimed import surface (`registry.ts:13`, the three specs importing actions + four `setGitHubPr*ForTest` from `../src/actions/github-pr.js`) was verified verbatim.

## Repaired Items

(none — no safe repairs were needed; the artifacts are internally consistent.)

## Blocking Items

(none)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: completeness
  Evidence: The change ships no new/modified capability specs (correct for a behavior-preserving refactor — the governing `pr-first-workflow` spec is explicitly preserved by the Non-Goals, and every task's `spec` field points at the existing guarding test files instead). This is consistent with the proposal's "Modified Capabilities: none" stance, but means the new `looksLike*` direct unit coverage lives only as a test file, not as a spec requirement.
  SuggestedAction: Optional — after implementation, consider whether the classifier phrase contract is stable enough to deserve a spec requirement; for now the AC-driven test in T-002 is sufficient.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` "Open Questions" lists three items (spec import paths, `runGhPrecheck` placement, optional parse/checks unit specs). All three have declared defaults that the tasks already follow (barrel → D3/T-005; runGhPrecheck → runtime module per T-003; parse/checks unit specs → deferred, only classify required by AC). No conflict, but they remain explicitly open.
  SuggestedAction: Resolve or drop the Open Questions section during implementation if the defaults hold; no plan change required now.
  Status: follow-up

## Review Detail

### alignment — PASS
- Every issue Acceptance Criterion traces to one or more tasks:
  - AC1 (classifier module + `looksLike*` direct tests) → **T-002** (extraction + new `github-pr-classify.spec.ts`).
  - AC2 (parsers / check-rollup / issue-field / output adapters as modules) → **T-001** (parse + checks), **T-005** (issue-fields + output adapters).
  - AC3 (merge-pipeline module + orchestrators collapse) → **T-004** (merge state machine), **T-005** (3 orchestrators + barrel).
  - AC4 (action IDs / output JSON / `GitHubPrErrorCode` / command strings+order / step names invariant) → asserted in T-001/T-004/T-005 acceptance criteria; verified the 7 `GitHubPrErrorCode` values and `classifyGhFailure` precedence (auth→protection→base-moved→pr-state→retry-safe) match `github-pr.ts:1207-1216`.
  - AC5 (three action specs pass) → green-test gate in every task.
  - AC6 (test-injection stubs migrate correctly) → **T-003** (runner setters), **T-004** (timing/retry setters), barrel re-export keeps spec import lines unchanged.
- No "What Changes" entry is orphaned; no issue requirement is missing or misinterpreted.

### completeness — PASS
- All requirements covered: the existing `pr-first-workflow` spec governs behavior (PR-first task graph, stable PR identity, checks-gated merge, merge confirmation, `pr-checks-failed` / `base-moved` contracts) and is preserved verbatim by the Non-Goals — verified against `openspec/specs/pr-first-workflow/spec.md`.
- All specs have tasks: every task references the guarding test file(s); T-002 additionally references `design.md#D5` for the new direct coverage.
- Edge cases considered: output JSON field-order drift (D4/T-001/T-005), import cycles (strict DAG, typecheck gate), stale mutable-runner binding after split (D2 getters in T-003), spec breakage from setter relocation (D3 barrel in T-003/T-004/T-005).

### consistency — PASS
- Proposal "Capabilities: none new/modified" is consistent with the refactor being internal-only; the design's module table (D1) and task descriptions agree.
- Tasks reference correct files: `create-github-pr.spec.ts` / `merge-github-pr.spec.ts` / `mark-github-pr-ready.spec.ts` all exist under `packages/runner/tests/`.
- Design D1 module layout matches task outputs symbol-for-symbol; every symbol named in the plan was confirmed present in `github-pr.ts` (types, parsers, checks, classifier matrix, runtime singletons, merge state machine, issue-field bridge, git-ref probes, output adapters, four setters).
- Naming consistent across proposal/design/tasks (e.g. `github-pr-runtime.ts`, `github-pr-merge.ts`, getter names `getGitHubPrGit`/`getGitHubPrGh`).

### feasibility — PASS
- Dependencies available: T-001 creates the leaf layer; T-002/T-003 build on T-001; T-004 builds on T-001–T-003; T-005 builds on all. Verified `runGhPrecheck` consumes `combinedGhOutput` (T-001 parse) — so T-003's `dependsOn: ["T-001"]` is correct and minimal (no classifier dependency).
- No circular dependencies: strict DAG per D1 (types←classify/parse/checks←runtime←merge←orchestrators←barrel).
- Task granularity appropriate — not over-fine:
  - No task title is a micro-action ("定义接口"/"提取类"/"注册DI"/"创建文件"). Titles are functional slices.
  - T-002 bundles classifier extraction **with** its required new unit tests (issue AC1 demands both together) — not a standalone "add tests" task.
  - Each task leaves the repo green (typecheck + full runner suite) and independently committable/revertible, matching the design's phased migration + rollback plan. The 5 tasks map cleanly onto the design's 6 migration phases (phase 2 folded into T-002, phase 6 final gate folded into T-005 acceptance criteria).

### dependency_completeness — PASS
- T-001 `dependsOn: []` (first task).
- T-002 `dependsOn: ["T-001"]` — needs `github-pr-types.ts`.
- T-003 `dependsOn: ["T-001"]` — needs `combinedGhOutput` from `github-pr-parse.ts`; does not need the classifier. Correct and minimal.
- T-004 `dependsOn: ["T-001","T-002","T-003"]` — merge consumes runtime + classify + parse + checks + types.
- T-005 `dependsOn: ["T-001","T-002","T-003","T-004"]` — final assembly.
- Every `dependsOn` points to an existing ID with strictly lower `priority` (1<2<3<4<5). No cycles.

<promise>PASS</promise>
