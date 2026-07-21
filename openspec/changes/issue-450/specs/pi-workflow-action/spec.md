### Requirement: `mohist/pi` is a Workflow Inline Agent Action

Workflow SHALL register `mohist/pi` as a UserFacing Inline Agent Action. Task and check hosts SHALL invoke the same Action contract, and the Action SHALL NOT branch on its host kind.

#### Scenario: Task dispatch recognizes Pi

- **WHEN** a Workflow task declares `uses: mohist/pi`
- **THEN** dispatch and Runner Action resolution select the Pi adapter
- **AND** the task is classified and presented as UserFacing Inline Agent work

#### Scenario: Check dispatch reuses Pi

- **WHEN** a stage check declares `uses: mohist/pi`
- **THEN** the check host invokes the same Pi adapter and maps its normal success or failure to pass or fail
- **AND** the Pi adapter does not know that its caller is a check

#### Scenario: Pi does not launch a named Agent

- **WHEN** a Workflow directly invokes `mohist/pi`
- **THEN** the turn belongs to the TaskRun or check work
- **AND** no Agent definition or AgentJob is resolved

### Requirement: Pi Action input is explicit and recursively expanded

The Action SHALL require a resolved non-empty `prompt`, accept optional `session`, and consume optional `options.model` and `options.variant`. Other option keys SHALL be ignored with sanitized diagnostics. It SHALL use normal Workflow input expansion and SHALL NOT read hidden `vars.agent` configuration.

#### Scenario: Explicit options reach Pi

- **WHEN** variables expand into an options object containing model and variant
- **THEN** the adapter validates and passes those explicit values to PiRuntime

#### Scenario: Omitted Session uses Work ID

- **WHEN** `session` is absent or null
- **THEN** the logical Workflow Session name is the current Work ID

#### Scenario: Explicit Session is normalized

- **WHEN** `session` contains surrounding whitespace around a non-empty value
- **THEN** the trimmed value is used as the logical Session name

#### Scenario: Invalid input prevents execution

- **WHEN** prompt is empty, session is empty after trimming, options is not an object, or model/variant has an invalid type
- **THEN** the Action returns `invalid-input`
- **AND** no logical Session, physical Session, runtime event, or prompt is created

#### Scenario: Unknown option remains diagnostic

- **WHEN** the options object contains an unknown key
- **THEN** the key may be reported as a runtime diagnostic but does not alter execution or Action output

### Requirement: Binding and input reporting precede the Pi prompt

For a first logical Session turn, the Action SHALL open the logical AgentSession, create the physical Pi Session, persist its binding, and report `session.input` before submitting the prompt.

#### Scenario: First turn is bound before execution

- **WHEN** no physical binding exists
- **THEN** attach of the absolute Pi session-file path succeeds before `session.prompt()` starts

#### Scenario: Binding failure prevents submission

- **WHEN** logical Session open or physical attach fails
- **THEN** the Action returns `session-binding-failed`
- **AND** it does not submit the prompt

#### Scenario: Input reporting failure prevents submission

- **WHEN** the required `session.input` batch does not receive one explicit AgentSession acceptance per submitted fact
- **THEN** the Action returns `session-reporting-failed`
- **AND** it does not submit the prompt

#### Scenario: OpenCode to Pi switch uses Pi context

- **WHEN** the logical Session is currently bound to OpenCode and the next serialized Action uses Pi
- **THEN** the Pi adapter guarded-attaches a new Pi physical Session before its prompt
- **AND** logical identity and lineage are preserved without passing the OpenCode physical ID to PiRuntime

#### Scenario: Pi to OpenCode switch uses OpenCode context

- **WHEN** the logical Session is currently bound to Pi and the next serialized Action uses OpenCode
- **THEN** the migrated OpenCode adapter guarded-attaches a new OpenCode physical Session before its prompt
- **AND** it does not pass the Pi session-file path to OpenCodeRuntime

### Requirement: Workflow completion consumes Pi final text only after Action success

The Pi adapter SHALL return final assistant text as a Runner-private turn fact. The task executor SHALL apply `_output`, other `expect` rules, artifacts, and recovery only after the Action and required Session reporting succeed.

#### Scenario: Final text satisfies an output expectation

- **WHEN** a successful Pi turn returns final text containing an accepted promise marker
- **THEN** the task executor evaluates that final text through the existing `_output` rule

#### Scenario: Failed turn bypasses completion checks

- **WHEN** Pi execution, binding, timeout, cancellation, provider handling, or required Session reporting fails
- **THEN** the original Action error remains the work result
- **AND** the executor does not inspect output markers or files

### Requirement: Pi task output is limited to Workflow promise projection

The task-facing public output of a successful `mohist/pi` invocation SHALL use the same `null | { promise }` contract as `mohist/opencode`. Runtime identity, diagnostics, transcript, model, usage, provider text, and final assistant text SHALL NOT become public Action output fields. A check host SHALL use Action success/failure for pass/fail and SHALL NOT add a Pi-specific output interpretation.

#### Scenario: Successful turn has no accepted promise

- **WHEN** a Pi task succeeds and Workflow finds no accepted promise marker
- **THEN** public Action output is `null`

#### Scenario: Successful turn has an accepted promise

- **WHEN** a Pi task's Workflow completion accepts the final matching promise marker
- **THEN** public Action output is exactly `{ "promise": "<value>" }`

### Requirement: Pi Action failures expose stable recovery codes

The adapter SHALL map runtime and integration failures to stable kebab-case codes usable by Workflow recovery.

#### Scenario: Missing physical Session requests Reset

- **WHEN** the persisted Pi session-file path is absent or corrupt
- **THEN** the Action returns `runtime-session-missing` with Reset guidance
- **AND** it does not create replacement context

#### Scenario: Provider exhaustion fails the turn

- **WHEN** PiRuntime reports non-recoverable provider exhaustion
- **THEN** the Action returns `turn-failed`
- **AND** the current logical and physical binding remains unchanged

#### Scenario: Deadline maps to timeout

- **WHEN** PiRuntime returns `deadline-exceeded`
- **THEN** the Action returns `timeout` regardless of missing completion markers

#### Scenario: Required final reporting fails

- **WHEN** a successful Pi turn's required final facts or `session.closed` do not receive explicit AgentSession acceptance
- **THEN** the Action returns `session-reporting-failed`
- **AND** it does not report success in the background

#### Scenario: Failed turn still closes its Session round

- **WHEN** PiRuntime returns timeout, interruption, provider failure, or another runtime failure after prompt submission
- **THEN** the adapter reports reconciled final facts and `session.closed` with status `failed` through a dedicated bounded signal
- **AND** the mapped runtime error remains authoritative after terminal acceptance

#### Scenario: Runtime and terminal reporting both fail

- **WHEN** a runtime error is already fixed and the terminal batch is not accepted
- **THEN** the original runtime error remains primary with a sanitized `session-reporting-failed` diagnostic
- **AND** the adapter does not claim that the Session became terminal

### Requirement: Worktree cleanup continues the same Pi conversation

If the Workflow executor requires an agent-backed cleanup turn, it SHALL reinvoke the already-resolved `mohist/pi` Action under the same logical Session coordinator lease and physical binding.

#### Scenario: Cleanup reuses the task Session

- **WHEN** a successful Pi task leaves worktree changes requiring cleanup
- **THEN** cleanup uses the same logical AgentSession and Pi session-file path
- **AND** no binding rotation or new lineage entry occurs

#### Scenario: Same-session work cannot intervene before cleanup

- **WHEN** another task or check targets the same logical Session while cleanup is pending
- **THEN** it begins only after the original task and cleanup release their shared coordinator lease
