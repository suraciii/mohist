### Requirement: `mohist/pi` is a Workflow Inline Agent Action

A Workflow task with `uses: mohist/pi` SHALL execute one Pi-backed Inline Agent turn owned by its TaskRun. The Action SHALL be classified as UserFacing and SHALL participate in normal Workflow dispatch, retry, recovery, artifact, and worktree-cleanup behavior. It MUST NOT resolve a Mohist Agent, create an AgentJob, or route through `mohist/opencode`.

#### Scenario: Workflow dispatch recognizes Pi

- **WHEN** a Workflow task declares `uses: mohist/pi`
- **THEN** the task SHALL be dispatched as a UserFacing Inline Agent task
- **AND** it SHALL execute through the Pi runtime instead of failing as an unknown Action

#### Scenario: Pi Action does not launch a named Agent

- **WHEN** the Runner executes a `mohist/pi` task
- **THEN** the TaskRun SHALL remain the work owner
- **AND** the Action MUST NOT resolve a Mohist Agent or create an AgentJob

### Requirement: Pi Action input is explicit and recursively expanded

The Action input SHALL consist of required `prompt`, optional `session`, and optional `options`. `prompt` SHALL resolve to non-empty text. `options`, when present, SHALL be an object whose `model` and `variant` values are strings or null. A present model SHALL have non-empty provider and model portions separated by the first `/`. Unknown option keys, including `runtime`, SHALL be ignored and recorded as diagnostics without affecting execution. The recursively expanded Action input SHALL be the only turn configuration; the Action MUST NOT read `vars.agent` or another hidden fallback unless the Workflow explicitly binds that value into `options`. Legacy `with.agent`, `with.kind`, and `with.type` inputs SHALL be rejected before execution with an actionable error.

#### Scenario: Explicit variable binding preserves an options object

- **WHEN** the entire `options` value is `${{ vars.agent }}` and expansion produces an object
- **THEN** the Action SHALL receive that object as structured options
- **AND** it SHALL use only its explicit `model` and `variant` members

#### Scenario: No hidden agent-variable fallback exists

- **WHEN** `vars.agent` exists but the task omits `with.options`
- **THEN** the Action MUST NOT read `vars.agent`
- **AND** Pi SHALL use the current Session selection or Pi default

#### Scenario: Unknown options are diagnostic only

- **WHEN** `options` contains valid `model` or `variant` values plus unknown keys such as `runtime`
- **THEN** the Action SHALL ignore and diagnose the unknown keys
- **AND** those keys MUST NOT fail or alter the turn

#### Scenario: Invalid input prevents a turn

- **WHEN** the prompt resolves to empty text, options is not an object, model or variant has an invalid type, or model lacks a non-empty provider/model split
- **THEN** the Action SHALL fail with `invalid-input`
- **AND** no physical Session SHALL be created and no prompt SHALL be submitted

### Requirement: Workflow completion evaluates Pi's final assistant text

After a successful Pi turn, the Action SHALL provide the final assistant text to the Workflow executor as a private turn fact. The Workflow executor SHALL evaluate task-level `expect`, `failIf`, artifact, and recovery rules against the same surfaces used by other Actions, including `_output` for final assistant text. The Pi runtime and Action MUST NOT evaluate Workflow completion policy themselves. When the Pi turn fails, times out, or is interrupted, that original Action failure SHALL become the task result and the executor MUST NOT replace it by evaluating files, markers, or missing promises.

#### Scenario: Final text satisfies an output expectation

- **WHEN** a successful Pi turn's final assistant text contains the task's `_output` expectation
- **THEN** the Workflow executor SHALL evaluate that expectation against the private final text
- **AND** the task SHALL continue through its remaining completion checks

#### Scenario: Failed turn bypasses completion checks

- **WHEN** the Pi turn fails, times out, or is interrupted before successful completion
- **THEN** the task SHALL retain the Action's original error
- **AND** the executor MUST NOT evaluate files, markers, `failIf`, or promise absence to replace that error

### Requirement: Pi Action output is limited to a promise projection

The public output of a successful `mohist/pi` task SHALL be `null` unless Workflow completion matches a promise marker. When exactly one promise marker is matched, the Workflow executor SHALL project `{ "promise": "<value>" }`. Final assistant text, physical Session identity, model, variant, usage, diagnostics, and provider details MUST NOT appear in Action output. The Action and runtime MUST NOT synthesize the promise object before Workflow completion evaluation.

#### Scenario: Successful turn without a promise has null output

- **WHEN** a Pi turn succeeds and no promise marker is matched
- **THEN** the task's public Action output SHALL be `null`
- **AND** runtime and transcript facts MUST NOT be exposed through that output

#### Scenario: Matched promise is projected by Workflow

- **WHEN** Workflow completion matches one configured promise marker in the final assistant text
- **THEN** the Workflow executor SHALL output `{ "promise": "<value>" }`
- **AND** no other Action-owned fields SHALL be included

### Requirement: Pi Action failures expose stable recovery codes

The Action SHALL expose these stable failure codes for Workflow recovery matching: `invalid-input`, `runtime-unavailable`, `runtime-session-missing`, `session-workspace-mismatch`, `session-binding-failed`, `incompatible-runtime`, `timeout`, `interrupted`, and `turn-failed`. Runtime `unavailable-runtime`, `missing-session`, and `deadline-exceeded` results SHALL map to `runtime-unavailable`, `runtime-session-missing`, and `timeout` respectively. Human-readable messages SHALL remain diagnostic text and MUST NOT be required for recovery matching.

#### Scenario: Missing physical Session has a stable code

- **WHEN** the current Pi binding cannot be restored because its physical Session is missing or unreadable
- **THEN** the Action SHALL fail with `runtime-session-missing`
- **AND** the message SHALL tell the user that Reset is required

#### Scenario: Provider exhaustion has a stable code

- **WHEN** the Pi runtime reports a non-recoverable provider quota or billing failure
- **THEN** the Action SHALL fail with `turn-failed`
- **AND** recovery matching SHALL NOT depend on provider-specific message text

### Requirement: Worktree cleanup continues the same Pi conversation

When a successful Pi task leaves changes that the Workflow requires to be committed or reverted, the executor SHALL invoke the task's already-resolved `mohist/pi` Action for the cleanup turn. The cleanup turn SHALL target the same logical AgentSession and current physical Pi Session as the original turn, and MUST NOT create a replacement binding or require a Reset. Subsequent tasks using that logical Session SHALL retain the cleanup turn in their conversation context.

#### Scenario: Cleanup reuses the task's Pi Session

- **WHEN** worktree enforcement requests a cleanup turn after a Pi task
- **THEN** the executor SHALL run cleanup through `mohist/pi` on the task's current logical and physical Session
- **AND** the cleanup turn MUST NOT rotate the binding
