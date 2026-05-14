## Findings

1. Error: `startEligibility` does not include lifecycle startability, so API/detail/list/create responses can report non-backlog issues as startable.
File: `packages/cli/src/services/issue-prerequisite-service.ts:112-135`
Evidence: `evaluateStartEligibility()` only checks whether prerequisites are delivered and otherwise always returns `{ startable: true, reason: 'ready' }`. The design and spec require start eligibility to be computed from both workflow lifecycle state and prerequisite state. The API exposes this value directly in list/detail/create responses at `packages/cli/src/api/issues.ts:529-537`, `604-615`, and `896-915`, and the CLI create hint uses it as the source of truth at `packages/cli/src/cli/commands/issue.ts:248-257`.
Impact: a non-startable issue outside backlog can still be advertised as startable in server responses and client UI/CLI rendering, violating the shared contract for `startEligibility` and the requirement that create guidance be based on true server start eligibility.
Suggested fix: Update `packages/cli/src/services/issue-prerequisite-service.ts:112-135` so `evaluateStartEligibility()` first evaluates lifecycle startability and returns `reason: 'not-startable-lifecycle'` with `startable: false` when the issue is not in a startable workflow state, then overlays the waiting-for-delivery case when prerequisites are undelivered. Add regression tests covering non-backlog/non-startable issues in `packages/cli/tests/services/issue-prerequisite-service.test.ts` and one API projection test in `packages/cli/tests/api-issue-prerequisites.test.ts`.

## Correctness

- FAIL: The shared eligibility service does not implement the full domain rule. See finding 1.

## Complexity

- PASS: The new service/repo methods are small and straightforward. `IssueStartPrerequisiteRepo` methods are short, and `IssuePrerequisiteService` stays readable despite one larger batched projection method.

## Test Coverage

- PASS with gap: Focused tests pass for declaration, waiting state, API rejection, CLI rendering, queue backstop, and web rendering.
- Evidence: `npm test -- issue-prerequisites` passed; `npm test -- IssueDetailPage-prerequisites` passed.
- Gap: No regression test covers lifecycle-driven `startEligibility.reason = 'not-startable-lifecycle'`, which is the missing behavior behind finding 1.

## Security

- PASS: The new API validates `prerequisiteNumber` type before use in `packages/cli/src/api/issues.ts:1214-1216`, uses parameterized SQL in `packages/cli/src/db/issue-start-prerequisite-repo.ts:23-27, 36-39, 44-46, 62-64`, and does not introduce obvious injection or secret-handling issues.

## Spec Compliance

- PASS: Users can declare one issue as a prerequisite of another.
Evidence: `POST /api/issues/:number/prerequisites` implemented at `packages/cli/src/api/issues.ts:1204-1247`; CLI command support in `packages/cli/src/cli/commands/issue.ts:546-569`; web mutation hooks in `packages/cli/web/src/components/IssueDetailPage.tsx:132-141, 840-899`.

- PASS: Issue detail shows start prerequisites and delivered/waiting state.
Evidence: API detail response includes `prerequisites` and `startEligibility` at `packages/cli/src/api/issues.ts:896-915`; CLI renders prerequisites at `packages/cli/src/cli/commands/issue.ts:62-70, 505-508`; web detail renders them at `packages/cli/web/src/components/IssueDetailPage.tsx:816-838`.

- PASS: Issue list/card shows a concise waiting reason.
Evidence: CLI list shows `[Waiting for #N]` at `packages/cli/src/cli/commands/issue.ts:335-337`; web card shows waiting text at `packages/cli/web/src/components/IssueCard.tsx:301-314`.

- PASS: `mo issue start`, API start, and Web UI Start use the same server-side waiting-for-delivery guard.
Evidence: API guard at `packages/cli/src/api/issues.ts:1164-1172`; queue backstop at `packages/cli/src/services/agent-runner-service.ts:1058-1067`; CLI surfaces API rejection at `packages/cli/src/cli/commands/issue.ts:804-823`; web start action relies on API and renders server error in page error area at `packages/cli/web/src/components/IssueDetailPage.tsx:617-635, 787-793`.

- PASS: If a prerequisite is not delivered, the issue cannot enter the pipeline.
Evidence: API rejects before enqueue at `packages/cli/src/api/issues.ts:1164-1172`; queue worker skips before worktree/run/session creation at `packages/cli/src/services/agent-runner-service.ts:1058-1067`.

- PASS: Rejected start does not enqueue `start-pipeline`.
Evidence: enqueue happens only after guard at `packages/cli/src/api/issues.ts:1183`; test asserts no enqueue on rejection in `packages/cli/tests/api-issue-prerequisites.test.ts:352-379`.

- PASS: Once prerequisite issues are delivered, the issue becomes startable without cleanup.
Evidence: delivery rule in `packages/cli/src/services/issue-prerequisite-service.ts:169-175`; returned readiness after delivered prerequisite at `packages/cli/tests/services/issue-prerequisite-service.test.ts:173-185`.

- PASS: Circular prerequisite declarations are rejected.
Evidence: cycle check at `packages/cli/src/services/issue-prerequisite-service.ts:177-196`; API returns structured reason at `packages/cli/src/api/issues.ts:1227-1235`; tests in `packages/cli/tests/services/issue-prerequisite-service.test.ts:73-99` and `packages/cli/tests/api-issue-prerequisites.test.ts:112-143`.

- PASS: Issue-level start prerequisites are not mixed with task-level `tasks.json dependsOn`.
Evidence: implementation only uses `issue_start_prerequisites` repo/service; separation tests in `packages/cli/tests/services/issue-prerequisite-service.test.ts:353-415`.

- PASS with deviation: API returns structured prerequisite/status data so clients do not parse body text.
Evidence: API list/detail payloads include `prerequisites` and `startEligibility` at `packages/cli/src/api/issues.ts:529-537, 896-915`; web/CLI tests assert no body parsing in `packages/cli/tests/cli-issue-prerequisites.test.ts:315-357` and `packages/cli/web/src/components/IssueDetailPage-prerequisites.test.tsx:207-232`.
Deviation: the structured `startEligibility` value is incomplete for lifecycle startability because of finding 1.

- FAIL: `startEligibility` is not fully spec-compliant as a shared projection.
Evidence: spec requires eligibility to be computed from startable workflow state and prerequisites; `packages/cli/src/services/issue-prerequisite-service.ts:112-135` only evaluates prerequisites and otherwise returns `ready`. This can make API/CLI/Web projections disagree with actual startability rules enforced elsewhere.

## Overall

- FAIL: The feature is close and the focused regressions pass, but the shared `startEligibility` contract is incomplete and can misreport non-startable lifecycle states.

<promise>FAIL</promise>
