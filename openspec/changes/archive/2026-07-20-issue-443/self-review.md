# Self-Review - issue-443 plan

Reviewed `proposal.md`, `specs/action-output/spec.md`, `design.md`, and
`tasks.json` against issue #443, the current implementation boundaries, and
the authoritative Action result contract in `design/workflow/actions.md`.

## Verdict

No blocking findings remain. The plan is internally consistent, stays within
the issue's Action-output scope, and is ready for autonomous implementation.

## Findings

None.

## Contract Checks

- The proposal defines one `action-output` capability and exactly one matching
  spec file exists.
- All seven requirements are normative and self-contained; every requirement
  has at least one four-hash WHEN/THEN scenario and there are no delta headers.
- Action success output is object-or-null from production through report,
  persistence, `setVars`, task references, recovery, and task-detail APIs.
- `core/process` has the exact `{stdout, exitCode}` success contract, including
  trimmed stdout and numeric exit code.
- Missing `setVars` paths name the missing source and preserve atomicity.
- Invalid output uses the existing `unexpected-error` platform code with an
  actionable shape message, consistent with the authoritative Action design;
  no new recovery protocol code is introduced.
- Other built-in Action fields remain unchanged, and the disconnected
  `capturedOutputs` path is an explicit non-goal rather than a promised
  capability.
- Approval feedback has a complete text-adapter contract: process stdout,
  whitespace trimming, exact feedback-header removal, verification-section
  removal, empty-result null, and no generic object-to-text coercion.
- Checks and AgentJobs are isolated at their own shared-envelope/domain
  boundaries, so the Workflow object-root rule is not applied to unrelated
  result shapes.

## Task And Verification Checks

- `tasks.json` is valid JSON and contains one AFK/WRITE task with no
  dependencies, making the runner/server/Web wire and API change atomic.
- Acceptance criteria cover all issue scenarios plus invalid output,
  check/AgentJob regression, approval feedback, dynamic artifacts, structured
  API rendering, PR delivery metadata, and the Action-design gap closure.
- Required verification includes runner typecheck/tests, server tests, and Web
  typecheck/tests under the repository's no-real-dependency/no-real-time rules.
- Migration and rollback explicitly require coordinated runner/server/Web
  versions; no data rewrite or compatibility fallback is implied.

## Residual Risk

The implementation has a broad mechanical surface because every built-in
Action test currently parses string output. The task addresses this with an
ActionResult type-first migration, production/test parser sweeps, focused
cross-boundary specs, and full package verification. No additional planning
change is required.

<promise>PASS</promise>
