### Requirement: The `mo workflow` group offers direct workflowRunId entry points for every state-changing workflow action

The `mo workflow` command group SHALL provide a control command for each state-changing WorkflowRun action: `approve`, `reject`, `retry`, `rerun`, `resume`, `pause`, and `stop`. Each command SHALL address exactly one WorkflowRun by its `workflowRunId` (the core-domain aggregate-root id) and SHALL NOT require an issue number, a project ref, or any reverse-resolution step to identify the target run. The commands exist so that a consumer that already holds a run id (an agent event-subscription handler, a script, an operator) can act on the run that is — by domain definition — the thing being addressed, instead of resolving the run back to an issue number.

#### Scenario: A control command addresses a run by id without a project or issue number

- **WHEN** a caller runs `mo workflow approve <runId>` (or any other control verb) with only the workflowRunId
- **THEN** the CLI SHALL resolve the target WorkflowRun solely from that id
- **AND** SHALL NOT require `--project` / `--project-id` to identify the run
- **AND** SHALL NOT require the caller to supply an issue number

#### Scenario: Every control verb is present on the workflow group

- **WHEN** the `mo workflow` group is introspected (e.g. `mo workflow --help`)
- **THEN** the group SHALL expose `approve`, `reject`, `retry`, `rerun`, `resume`, `pause`, and `stop` subcommands
- **AND** SHALL NOT expose a separate `rerun-from-stage` subcommand (that variant collapses into the `--from-stage` flag of `rerun`)

### Requirement: A direct control command triggers the same grain method and the same state guards as the matching `mo issue` shortcut

Each `mo workflow <verb> <runId>` command SHALL exercise the same `IWorkflowGrain` state-referee method, and the same active/failed-state admission guards, as the corresponding `mo issue <verb> <number>` shortcut. The two entry points are two addressing axes onto one behavior — they MUST NOT diverge in semantics, admissible run states, or error mapping. Concretely: `approve`→`ApproveAsync`, `reject`→`RequestChangesAsync`, `retry`→`RetryAsync`, `rerun`→`RerunAsync`, `rerun --from-stage`→`RerunFromStageAsync`, `resume`→`ResumeAsync`, `pause`→`PauseAsync`, `stop`→`StopAsync`, matching the grain calls behind the existing issue routes.

Active-only actions (`approve`, `reject`, `resume`, `pause`, `stop`) SHALL be rejected when the run is not in a controllable (active) state; `retry` and `rerun` SHALL additionally be admissible from a failed state — mirroring the `WorkflowControlAction.ActiveOnly` vs `RetryOrRerun` distinction already enforced server-side.

#### Scenario: approve on an active run behaves like the issue shortcut

- **WHEN** a caller runs `mo workflow approve <runId>` for a run whose status is active
- **THEN** the run's approval gate SHALL be passed (the same effect as `mo issue approve <number>` for the issue aliased to that run)
- **AND** the resulting run state SHALL be indistinguishable from invoking the issue shortcut on the correlated issue

#### Scenario: An active-only action on a non-active run is rejected

- **WHEN** a caller runs `mo workflow approve <runId>` (or `resume`, `pause`, `stop`) against a run that is stopped or completed
- **THEN** the command SHALL fail with a not-active conflict
- **AND** SHALL NOT mutate the run

#### Scenario: retry and rerun remain admissible from a failed run

- **WHEN** a caller runs `mo workflow retry <runId>` or `mo workflow rerun <runId>` against a failed run
- **THEN** the command SHALL be admitted (the failed-state carve-out that already applies to the issue shortcuts)
- **WHEREAS** the active-only verbs would be rejected for the same run

### Requirement: `reject` requires a non-empty reason, matching the issue shortcut

`mo workflow reject <runId>` SHALL require a non-empty reason message (e.g. via `--message` / `-m`), and SHALL fail without making a server request when the reason is missing or whitespace. This matches the existing `mo issue reject --message` contract and the server-side `Reject reason is required` guard.

#### Scenario: reject without a message fails locally and makes no request

- **WHEN** a caller runs `mo workflow reject <runId>` without `--message` (or with a whitespace-only value)
- **THEN** the CLI SHALL exit non-zero and print a validation error naming the required flag
- **AND** SHALL NOT send any state-changing request to the server

#### Scenario: reject with a message forwards the reason

- **WHEN** a caller runs `mo workflow reject <runId> --message <reason>`
- **THEN** the reason SHALL be forwarded to the server and the run SHALL be sent back at its approval gate (same effect as `mo issue reject <number> --message <reason>`)

### Requirement: `rerun --from-stage` is the single rerun variant

`mo workflow rerun <runId>` SHALL rerun from the start by default, and SHALL accept an optional `--from-stage <stage>` flag that reruns from the specified stage (invalidating that stage and all later stages, creating new attempts). The flag-bearing form MUST be behaviorally identical to the existing `mo issue rerun-from-stage <number> --stage <s>`. A `--from-stage` value that is blank or whitespace SHALL be rejected locally with no server request; an unknown / not-yet-reached stage SHALL surface the server's structured error (e.g. `unknown_stage` / `stage_not_reached` / `active_work_in_range`) rather than a generic failure.

#### Scenario: rerun without the flag reruns from the start

- **WHEN** a caller runs `mo workflow rerun <runId>` with no `--from-stage`
- **THEN** the run SHALL rerun from the beginning (same effect as `mo issue rerun <number>`)

#### Scenario: rerun with --from-stage reruns from that stage

- **WHEN** a caller runs `mo workflow rerun <runId> --from-stage build`
- **THEN** the command SHALL rerun from the `build` stage (same effect as `mo issue rerun-from-stage <number> --stage build`)

#### Scenario: --from-stage with a blank value is rejected locally

- **WHEN** a caller runs `mo workflow rerun <runId> --from-stage "  "`
- **THEN** the CLI SHALL exit non-zero, print a validation error, and make no server request

### Requirement: `pause` is resumable; `stop` is terminal

`mo workflow pause <runId>` SHALL map to the workflow pause action (the same grain call behind `mo issue force-stop`) and the run SHALL remain resumable afterwards via `mo workflow resume`. `mo workflow stop <runId>` SHALL map to terminal stop (the same grain call behind `mo issue stop`) and the run SHALL NOT be resumable afterwards. The two verbs MUST NOT be aliased to one another; their distinction (resumable pause vs permanent stop) SHALL be conveyed in command help, mirroring the existing issue-shortcut help wording.

#### Scenario: pause then resume round-trips

- **WHEN** a caller runs `mo workflow pause <runId>` followed by `mo workflow resume <runId>`
- **THEN** the pause SHALL leave the run resumable and the subsequent resume SHALL succeed

#### Scenario: stop is terminal

- **WHEN** a caller runs `mo workflow stop <runId>`
- **THEN** the run SHALL be permanently stopped and a subsequent `mo workflow resume <runId>` SHALL fail

### Requirement: New control commands obey the shared CLI command-factory conventions

Each control command SHALL be constructed through the same shared factory / option helpers as other state-changing `mo` commands, so that the global output-format (`-o`), dry-run, and error-reporting conventions apply uniformly. Control commands SHALL surface structured server errors (error message + code) on stderr and exit non-zero on failure, consistent with the existing issue lifecycle commands.

#### Scenario: A structured server error is surfaced verbatim

- **WHEN** a control command receives a structured error response (e.g. `stage_not_reached` with a message)
- **THEN** the CLI SHALL print both the message and the code on stderr and exit non-zero

#### Scenario: Output format and dry-run conventions are honored

- **WHEN** a caller passes `-o json` (or another supported format) to a control command
- **THEN** the command SHALL render its result through the shared output-format path used by the rest of the CLI

### Requirement: The existing `mo issue` shortcuts remain unchanged

The new `mo workflow` control commands are additive. The existing `mo issue approve / reject / retry / rerun / rerun-from-stage / resume / force-stop / stop` shortcuts SHALL continue to resolve `issue → workflowRunId → IWorkflowGrain.<method>` exactly as today, with no change in path, flags, or behavior. This change MUST NOT alter or remove any issue-scoped command.

#### Scenario: Issue shortcuts are unaffected

- **WHEN** a caller runs `mo issue approve <number>` (or any other existing issue workflow shortcut) after this change
- **THEN** the command SHALL behave identically to before this change, resolving the issue to its run and invoking the same grain method
