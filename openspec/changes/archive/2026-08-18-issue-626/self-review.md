# Self-Review: issue-626

## Review Basis

The requested issue command was run before reviewing the artifacts. It reports issue 626 with the title `Workflow recovery replay task rejected by schema: core/script unknown input resourceProfile`; its body is empty, so no additional issue-specific acceptance text was available. The review is anchored to that stated failure and to the explicit goals and scenarios in the plan artifacts.

Reviewed:

- `proposal.md`
- `design.md`
- `tasks.json`
- `specs/workflow-replay-input-compatibility/spec.md`
- Current Runner and Server dispatch, persistence, recovery, Action validation, manifest, and focused test code

## Coverage

**Checked, no issue.** The plan covers the complete goal visible in the issue and all normative scenarios in the capability spec:

- Historical `core/script` input containing `resourceProfile` is accepted during direct Workflow redelivery.
- The same retired field is ignored on `retrySelf` continuations, including repeated recovery attempts.
- `run`, `shell`, `timeout`, templates, task identity and metadata, artifacts, variables, completion expectations, recovery declarations, and the remaining recovery budget are preserved.
- The current `core/script` manifest remains strict and does not expose `resourceProfile` to new definitions.
- Other unknown inputs and invalid supported inputs remain rejected without Action invocation.
- Persisted `TaskRun` state and dispatch snapshots are preserved rather than rewritten.
- T-001 owns Runner execution and recovery behavior; T-002 owns Server translation, snapshot preservation, and definition validation. Both tasks include executable regression criteria and relevant test-suite gates.

## Correctness

**Checked, no issue.** The proposed execution-boundary normalization matches the current failure path and survives the adversarial cases required by the issue:

- `WorkExecutor` already makes a defensive copy before unresolved-reference detection, deferred rendering, validation, work-directory resolution, and Action invocation. Removing only `resourceProfile` from that copy before those operations allows the retired field to be ignored without mutating `DispatchWorkItem.with`.
- A retired value containing an unresolved template or an invalid type is removed before the unresolved-reference and schema checks. Supported fields remain in the map, so their current validation still applies.
- An unrelated unknown field remains in the normalized map and continues to produce the existing validation failure.
- The proposed owner gate includes legacy envelopes with an omitted owner kind, excludes `agent-job` work, and is scoped to `core/script` task execution. Checks and other Actions do not pass through the task compatibility rule.
- Direct redelivery is protected because snapshots and freshly translated dispatches both retain the raw declaration, while execution normalizes every attempt.
- `tryRecovery` already deep-clones the raw declaration and decrements `recoveryRemaining` once. Keeping that behavior means the continuation retains the historical field for auditability while the next execution removes it from effective Action input.

## Current-Code Consistency

**Checked, no issue.** The plan follows the existing boundaries and conventions:

- The current Runner manifest in `packages/runner/src/actions/built-ins.ts` declares only required `run` and optional `shell` and `timeout` for `core/script`, matching the plan's strict-contract requirement.
- `packages/runner/src/runtime/executor.ts` is the actual shared execution boundary for Workflow task validation and handler invocation; `packages/runner/src/actions/input-validation.ts` remains the strict validator rather than gaining a global exception.
- `packages/runner/src/runtime/recovery.ts` copies `with`, `expect`, artifacts, variables, recovery, and recovery budget into continuation inputs, matching the plan's decision to leave recovery construction unchanged.
- `WorkflowItemTranslator` serializes the persisted task declaration, and `DispatchService` reuses stored task dispatch snapshots for redelivery. The proposed Server tests exercise those existing preservation paths without requiring a migration.
- `ActionContractValidator` already validates new definitions against the current Runner catalog, so keeping `resourceProfile` out of the manifest preserves rejection for new definitions.

## Task Breakdown

**Checked, no issue.** T-001 and T-002 form a complete, acyclic, source-ordered breakdown with appropriate ownership:

- T-001 covers the pure compatibility projection, execution ordering, owner scoping, raw-input immutability, direct replay, retry-self replay, strict rejection, and Runner type/test verification.
- T-002 covers translator and snapshot preservation, task metadata and recovery fields, new-definition rejection, unchanged catalog behavior, and Server test verification.
- Neither task requires a database migration or a generalized validator exception, consistent with the proposal and non-goals.
- The acceptance criteria are specific enough to verify both successful replay and rejection behavior, including no handler invocation on invalid supported or unrelated unknown inputs.

## Findings

No must-fix findings.

## Observations

- The issue body contains no acceptance criteria beyond the issue title, so any requirements not represented by that title or the supplied plan could not be independently checked.
- The artifacts use `verbatim` for the raw declaration. The current Server transport serializes parsed JSON maps, so this should be understood as preserving JSON fields, values, and templates rather than preserving original whitespace or byte formatting. That distinction does not affect the issue's execution or persistence goals.
- The retention signal for removing the compatibility rule and optional diagnostic instrumentation remain open questions. They are operational follow-ups and do not block replay correctness.

<promise>PASS</promise>
