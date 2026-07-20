# Self-Review - issue-443 plan

Reviewed `proposal.md`, `specs/action-output/spec.md`, `design.md`, and
`tasks.json` against issue #443, the corrected task decomposition, and the
authoritative Action design.

## Verdict

The three findings from the previous review are fixed: disconnected declared
output capture is no longer normative, runner/server/Web now switch in one
atomic task, and approval-feedback summary behavior has an explicit
`core/process.output.stdout` adapter with regression coverage. Two remaining
contract issues must be corrected before build.

## Findings

### 1. Blocking: `invalid-action-output` invents a platform error code outside the authoritative Action contract

The design says an invalid success output becomes an
`invalid-action-output` error (`design.md:34`), and the implementation task
requires that named failure (`tasks.json:11`). The proposal and spec require
only an actionable output-shape failure; they do not establish a new error
code.

The authoritative Action design has a closed platform-code list:
`invalid-input`, `unexpected-error`, and `timeout`
(`design/workflow/actions.md:48-50`). It also says the engine normalizes an
Action-boundary implementation failure to `unexpected-error`
(`design/workflow/actions.md:129-138`). Adding `invalid-action-output` only in
this OpenSpec would leave the platform error catalog and recovery matching
contract inconsistent, while updating that catalog would expand this issue's
protocol surface without a stated requirement.

The bounded repair is to use the existing `unexpected-error` platform code
with an actionable message that says successful Action output must be an
object or `null`. If a distinct machine-matchable code is genuinely required,
the proposal, spec, authoritative Action design, and task must all explicitly
add it and cover recovery behavior.

### 2. Blocking: the approval-feedback extraction requirement is not self-contained

The new requirement says `output.stdout` SHALL receive "the existing
feedback-resolution section extraction" (`specs/action-output/spec.md:42`),
but neither the requirement nor its scenarios define that behavior. The only
scenario uses plain text, so it does not establish what happens to the
`## Feedback Resolution` header, the `## Verification` section, surrounding
whitespace, or an empty extracted body. The design and task refer to the
implementation name `ExtractResolutionSummary`, which explains how to reuse
today's code but does not make the normative spec self-contained.

Specify the resulting behavior directly and add a scenario containing both
headers: trim surrounding whitespace, remove a leading
`## Feedback Resolution` header, discard the `## Verification` section and
everything after it, and return `null` when no resolution text remains. The
task's existing "section stripping" test criterion will then have a precise
contract to verify.

## Checks That Pass

- Proposal capability `action-output` has exactly one matching spec file.
- The disconnected `capturedOutputs` path is now an explicit non-goal only;
  no proposal/spec/task requirement claims it is usable.
- All seven requirements have at least one four-hash WHEN/THEN scenario and no
  delta headers.
- `tasks.json` is valid JSON and contains one complete AFK/WRITE task with no
  dependencies, so the graph is a valid atomic DAG.
- The single task includes runner, server, and Web API consumers plus their
  required typecheck/test commands; no intermediate deliverable drops task
  output.
- Approval feedback now has an explicit process-stdout source, rejects generic
  object-to-text coercion, and has server test coverage in the task.
- All issue acceptance paths are represented: `core/process` fields,
  successful `setVars` and task references, missing-path atomic failure,
  recovery matching, built-in Action/profile regression, persistence, and
  task-detail display.

<promise>FAIL</promise>
