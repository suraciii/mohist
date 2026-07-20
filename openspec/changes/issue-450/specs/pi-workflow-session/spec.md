### Requirement: Workflow Session names identify stable logical conversations

A Pi Workflow Action SHALL resolve its logical AgentSession by project, WorkflowRun, and session name. Tasks in the same WorkflowRun with the same explicit session name SHALL resolve to the same logical AgentSession; while its current physical binding remains unchanged, later tasks SHALL receive that binding's conversation context. A runtime switch SHALL preserve logical identity and lineage but SHALL start a new physical conversation without migrating old context. Different names, projects, or WorkflowRuns SHALL remain isolated even when prompt, model, variant, and working directory are identical. When `session` is omitted, the Action SHALL use the current Work ID as the session name.

#### Scenario: Same name shares one logical conversation

- **WHEN** two Pi tasks in the same project and WorkflowRun use the same session name without an intervening runtime rebind
- **THEN** both SHALL resolve to the same logical AgentSession
- **AND** the second task SHALL receive the first task's conversation context

#### Scenario: Omitted name isolates unrelated work

- **WHEN** two tasks omit `session` and have different Work IDs
- **THEN** each SHALL use its own Work ID as the session name
- **AND** their logical AgentSessions SHALL remain separate

### Requirement: The physical Pi binding is persisted before the first prompt

For an unbound Pi logical AgentSession, the Runner SHALL create a physical Pi Session in the authoritative working directory and SHALL persist its binding before submitting the first prompt. The binding SHALL record runtime `pi`, the owning Runner, immutable working directory, and the absolute Pi session-file path as `runtimeSessionId`; the Pi internal Session UUID SHALL remain diagnostic only. If physical creation does not yield a session-file path, the turn SHALL fail as `incompatible-runtime`. If binding persistence fails, the turn SHALL fail as `session-binding-failed` and MUST NOT submit the prompt.

#### Scenario: Binding precedes first prompt admission

- **WHEN** a Pi task targets an AgentSession with no physical binding
- **THEN** the Runner SHALL create the physical Pi Session and persist its absolute session-file path first
- **AND** it SHALL submit the first prompt only after persistence succeeds

#### Scenario: Failed binding persistence prevents execution

- **WHEN** the physical Pi Session is created but its logical binding cannot be persisted
- **THEN** the Action SHALL fail with `session-binding-failed`
- **AND** the first prompt MUST NOT be submitted

### Requirement: Current Pi bindings are reused without model-driven rotation

The current Pi binding SHALL be reused for the same logical AgentSession across Workflow tasks, task retries, cleanup turns, and Runner restarts. A model or variant change SHALL be applied to the existing physical Session and MUST NOT replace the session-file path, append lineage, or discard conversation context. After Runner restart, the runtime SHALL lazily restore the physical Session from the persisted absolute session-file path rather than creating a replacement. A Workflow change between Pi and another registered runtime in either direction SHALL preserve the logical AgentSession identity, establish a new physical binding under an expected-current guard, and append the new binding to lineage without migrating the previous runtime's context.

#### Scenario: Retry reuses the physical Session

- **WHEN** a Pi task is retried with the same logical session name
- **THEN** it SHALL use the current persisted Pi session-file path
- **AND** it MUST NOT create a new physical Session for the retry

#### Scenario: Model change preserves context and binding

- **WHEN** a later task on the same logical AgentSession changes `options.model` or `options.variant`
- **THEN** the runtime SHALL apply the selection to the current physical Pi Session
- **AND** it MUST NOT replace the binding or append lineage

#### Scenario: Runner restart restores from the binding

- **WHEN** a Runner restarts before a later task uses an already-bound Pi AgentSession
- **THEN** the runtime SHALL restore that Session from the persisted session-file path
- **AND** the later task SHALL continue with the prior conversation context

#### Scenario: Runtime change appends physical lineage

- **WHEN** a Workflow task selects Pi for a logical AgentSession whose current binding belongs to another runtime
- **THEN** Mohist SHALL keep the same logical AgentSession identity and establish a new Pi binding
- **AND** it SHALL append the Pi session-file path and runtime to the Session lineage without migrating old context

#### Scenario: Switching from Pi to another runtime uses the same guard

- **WHEN** a Workflow task selects another registered runtime for a logical AgentSession currently bound to Pi
- **THEN** Mohist SHALL keep the same logical AgentSession identity and establish the replacement binding only when the expected Pi binding is still current
- **AND** it SHALL append the replacement runtime binding to lineage without migrating Pi context

### Requirement: Invalid persisted bindings fail without silent replacement

Before submitting a prompt, the Runner SHALL verify that the authoritative working directory matches the logical AgentSession's immutable bound directory. A mismatch SHALL fail with `session-workspace-mismatch`. When a current Pi binding exists but its session file is missing, unreadable, corrupt, or otherwise cannot be restored, the task SHALL fail with `runtime-session-missing` and an explicit Reset instruction. The runtime MUST NOT create a replacement physical Session, change lineage, or submit the prompt in either case. A binding created before Pi wrote its first assistant message SHALL follow the same missing-file rule after a Runner restart.

#### Scenario: Missing session file requires Reset

- **WHEN** a task resolves a Pi binding whose session file no longer exists
- **THEN** the task SHALL fail with `runtime-session-missing` and an explicit Reset instruction
- **AND** the runtime MUST NOT create a replacement Session

#### Scenario: Workspace mismatch rejects before prompt

- **WHEN** a Pi task's authoritative working directory differs from the logical AgentSession's bound directory
- **THEN** the task SHALL fail with `session-workspace-mismatch`
- **AND** no prompt SHALL be submitted and no binding SHALL be replaced

#### Scenario: Pre-persistence crash follows missing-file semantics

- **WHEN** the first turn persisted a session-file binding but the Runner stopped before Pi created that file
- **THEN** a later restore SHALL fail with `runtime-session-missing` and a Reset instruction
- **AND** the runtime MUST NOT infer an empty replacement conversation

### Requirement: One Workflow work turn executes per logical AgentSession

Mohist SHALL admit at most one Workflow-initiated work turn at a time for a logical AgentSession, including when concurrent tasks select different Inline Agent runtimes. The Workflow executor SHALL enter the same runtime-neutral logical-Session serialization boundary before opening or rebinding the Session and SHALL retain one task lease through Prompt completion, successful completion checks, any worktree cleanup turn, and durable event persistence. Another work turn targeting that Session SHALL remain serialized until the current task lifecycle reaches a terminal outcome and the runtime confirms the physical execution stopped. An interruption-unconfirmed outcome SHALL quarantine both the physical Pi Session and the logical serialization key before the current operation leaves the boundary. Later work MUST NOT start a Prompt or runtime rebind on that logical AgentSession until stop is observed or Runner process restart makes prior in-process execution impossible. Different logical AgentSessions SHALL remain independently executable.

Existing Session commands SHALL NOT be allowed to start idle work on a binding while a Workflow task is preparing, rebinding, between its original and cleanup Prompts, or durably reporting. A guarded bind SHALL reject without mutation when Follow-up, Compact, or Reset command state is pending or active. When the Workflow lease wins first, the owning Runner SHALL reject command admission during preparation; only an existing OpenCode Follow-up MAY steer after the Runtime has synchronously reserved the physical Session for the active Prompt. Compact and Reset SHALL remain idle-only. These admission rules MUST NOT add Pi command routing in this issue.

#### Scenario: Concurrent tasks on one Session are serialized

- **WHEN** two Workflow tasks concurrently target the same logical Pi AgentSession
- **THEN** Mohist SHALL execute at most one of their work turns at a time
- **AND** the second turn MUST NOT overlap the first on the physical Pi Session

#### Scenario: Separate Sessions remain independent

- **WHEN** concurrent Pi tasks target different logical AgentSessions
- **THEN** serialization of one Session SHALL NOT serialize the other Session

#### Scenario: Concurrent runtime choices share one serialization boundary

- **WHEN** concurrent OpenCode and Pi Workflow tasks target the same logical AgentSession
- **THEN** both Actions SHALL enter the same logical-Session serialization boundary
- **AND** a runtime rebind or Prompt MUST NOT overlap the other Action's active turn

#### Scenario: Unconfirmed stop prevents overlap

- **WHEN** a turn ends with interruption unconfirmed and another Workflow task targets the same logical AgentSession
- **THEN** the later task SHALL fail as runtime unavailable rather than start on the quarantined physical Session
- **AND** no two Pi Prompts SHALL overlap on that physical Session

#### Scenario: Runtime switching cannot bypass an unconfirmed stop

- **WHEN** a Pi turn's interruption is unconfirmed and a later Workflow task selects OpenCode for the same logical AgentSession
- **THEN** the shared logical-Session boundary SHALL reject the later task as runtime unavailable before rebind
- **AND** no OpenCode Prompt SHALL start until Pi stop is observed or the Runner process restarts

#### Scenario: Observed stop clears the logical quarantine

- **WHEN** PiRuntime reports `cleared` for the generation that quarantined a physical path and logical Session key
- **THEN** the coordinator SHALL remove that matching logical quarantine and admit later work on the key
- **AND** it MUST NOT replay the failed Prompt

#### Scenario: Runner restart starts without execution quarantine

- **WHEN** the Runner process restarts after an unconfirmed in-process Pi interruption
- **THEN** the new PiRuntime and coordinator SHALL start with empty execution-quarantine state
- **AND** persisted reporting or missing-session state SHALL continue to apply independently

#### Scenario: A command reservation acquired first blocks Workflow bind

- **WHEN** Follow-up, Compact, or Reset reserves or starts work on the current binding before a Workflow runtime bind reaches the AgentSession authority
- **THEN** the bind SHALL reject without replacing the binding or sealing its Action stream
- **AND** the Workflow Action MUST NOT submit a Prompt

#### Scenario: Workflow preparation acquired first blocks idle commands

- **WHEN** a Workflow task holds the logical-Session lease but has not established an active Runtime Prompt slot, or is between work and cleanup Prompts
- **THEN** Follow-up, Compact, and Reset admission SHALL return a definite not-started or busy result
- **AND** no command SHALL start against the old or new physical binding

#### Scenario: Active OpenCode Follow-up has no pre-Prompt race

- **WHEN** an existing OpenCode Follow-up arrives after the Runtime has reserved the physical Session and the Workflow lease is in `prompt-active`
- **THEN** it MAY steer the already-active OpenCode turn under existing behavior
- **AND** there SHALL be no state in which the Follow-up can start an idle Prompt before the Workflow Prompt is admitted

### Requirement: Pi turn facts populate the existing Session audit record

The AgentSession authority SHALL issue one opaque stable Action stream identity for each physical Workflow binding and initialize its applied cursor to `0`; the Runner MUST NOT derive or choose this identity. Workflow open SHALL return current stream state and SHALL atomically backfill one stable identity at cursor `0` when a pre-change OpenCode binding has none, without changing binding or lineage. First attach and every successful runtime rebind SHALL return a fresh identity at cursor `0`; idempotent open/attach SHALL return the existing identity. An unbound Session SHALL have no stream until attach. Action events SHALL carry both this identity and the physical binding, use sequence `1` for the first event, and be accepted only while both values identify the current binding.

Every Workflow runtime binding SHALL have a durable Action event-stream manifest so Action input reporting, drain, and runtime rebind use one protocol in both directions; this issue does not require connecting OpenCode's full runtime-event observer. The sequenced Action route is distinct from existing Session-command and generic runtime-event routes, whose facts remain binding-validated but do not enter this completeness cursor. For every Pi Workflow turn, the Runner SHALL durably report the submitted prompt and required canonical Pi audit facts to the current logical AgentSession: final assistant and reasoning content retained by Pi, completed tool calls and results, resolved model observations, and final token usage including separate input, output, cache-read, cache-write, and thought token dimensions when Pi provides them. `cachedWriteTokens` SHALL remain distinct from `cachedReadTokens` through persisted Session state, runtime-event application, API/read models, and Web presentation. Live intermediate deltas MAY be reported but are provisional and SHALL NOT be required for restart completeness when Pi does not retain them. Before Prompt admission, the Runner SHALL durably create the binding's manifest with persisted projector fingerprints and an active-turn checkpoint, then `session.input` SHALL be durably accepted by the Session authority through the Action route. The Runner SHALL durably mark that checkpoint admitted before calling the Runtime. After admission, each required canonical fact and its updated fingerprints SHALL be one atomic Runner-local outbox state transition; the checkpoint SHALL close only after all required final facts are durable. Delivery SHALL retry across transport loss and Runner restart without replaying the Prompt. Each binding SHALL use one stable Action stream identity and monotonically increasing sequence. The Session authority SHALL atomically ignore an already-applied Action sequence before updating transcript, usage, or model state, reject a gap, and reject facts for a stale or sealed physical binding.

Before a runtime change, the shared logical-Session serialization boundary SHALL fence new work while the current owning Runner drains all locally issued events. The guarded bind SHALL carry that stream's final issued sequence; in the same Session transition, the authority SHALL require its applied cursor to equal that sequence, seal the old stream, and replace the binding, or SHALL reject without either mutation. A Runner MUST NOT attest another Runner's local stream. Pi message IDs and tool-call IDs SHALL suppress duplicate SDK projections before enqueue and SHALL be persisted with the stream manifest so restart reconciliation can append only missing required facts. Unknown Pi events SHALL be diagnostic only and MUST NOT change Workflow or AgentSession state. AgentSession events SHALL record execution facts and MUST NOT decide TaskRun completion or Workflow advancement.

If a durable outbox append fails after Prompt admission, the Action SHALL fix `session-reporting-failed`, request interruption when the turn is still active, and quarantine the physical Session from later Prompt or rebind admission until reporting repair completes. The Runner SHALL retain the fact in memory while alive. On restart, the pre-created manifest SHALL drive reconciliation from the persisted Pi Session messages through the saved projector fingerprints; required missing final facts SHALL be appended and drained before quarantine clears. A corrupt committed manifest SHALL remain preserved, SHALL quarantine only its logical Session with an actionable diagnostic, and MUST NOT block unrelated Sessions or AgentJob work. Failure to initialize/list the outbox root or provide atomic same-directory rename and file/directory sync SHALL instead make global Action reporting not-ready and SHALL prevent Runner registration or new work claiming. Repair MUST NOT replay the Prompt.

The guarded rebind's expected-current state SHALL include the authority-issued stream identity as well as the physical binding. In the same transition that verifies `drainedThroughSequence`, the authority SHALL remove the old stream from current admission and create the replacement binding's fresh stream at cursor `0`; a current stream with no events is valid at sequence `0` and MUST NOT require a synthetic event.

#### Scenario: Session view shows a completed Pi turn

- **WHEN** a Pi turn emits assistant text, tool execution, resolved model, and usage facts
- **THEN** the AgentSession transcript and usage read model SHALL expose those facts for the Session view
- **AND** the facts SHALL remain associated with the current Pi session-file binding

#### Scenario: First binding receives an authority-issued empty stream

- **WHEN** a Workflow AgentSession receives its first physical binding
- **THEN** the AgentSession authority SHALL create and return one opaque Action stream identity with applied cursor `0`
- **AND** the Runner SHALL use that identity with first event sequence `1`

#### Scenario: Legacy OpenCode binding is bootstrapped once

- **WHEN** Workflow open resolves a pre-change OpenCode physical binding with no Action stream state
- **THEN** the AgentSession authority SHALL atomically create one stream identity at cursor `0` without changing binding or lineage
- **AND** concurrent or repeated opens SHALL return that same identity

#### Scenario: Empty stream can be rebound without a synthetic event

- **WHEN** the owning Runner requests rebind of a current Action stream that has issued no events
- **THEN** it SHALL attest that stream's identity and drained-through sequence `0`
- **AND** successful rebind SHALL replace it with a fresh stream identity at cursor `0`

#### Scenario: Cache-write usage remains a distinct dimension

- **WHEN** Pi reports different cache-read and cache-write token counts
- **THEN** AgentSession state and API SHALL expose them as `cachedReadTokens` and `cachedWriteTokens` respectively
- **AND** the Web Session view SHALL render both supplied values without merging or dropping cache-write tokens

#### Scenario: Duplicate Pi events are idempotent

- **WHEN** the same Pi message or tool event is delivered more than once
- **THEN** the Session projection SHALL use its message ID or tool-call ID to avoid duplicate transcript or usage facts

#### Scenario: Post-admission transport failure is retried durably

- **WHEN** a Pi event cannot reach the Server after the Prompt has been admitted
- **THEN** the Runner SHALL retain the ordered event in its durable outbox and preserve the Action result
- **AND** it SHALL retry delivery without replaying the Prompt, including after Runner restart

#### Scenario: Post-admission local append failure quarantines reporting

- **WHEN** a required Pi fact cannot be atomically appended after the Prompt has been admitted
- **THEN** the Action SHALL fail with `session-reporting-failed` and the physical Session SHALL reject later Prompt and rebind admission
- **AND** the Runner SHALL interrupt an active turn and MUST NOT replay its Prompt

#### Scenario: Restart repairs an incomplete manifested turn

- **WHEN** the Runner restarts with a pre-created stream manifest whose turn did not durably append every required final fact
- **THEN** it SHALL reconcile the persisted Pi Session messages through the manifest's projector fingerprints
- **AND** it SHALL append and drain missing required facts before admitting later work on that Session

#### Scenario: Admitted checkpoint without a Pi file follows missing-session recovery

- **WHEN** restart finds an admitted checkpoint but the bound Pi session file was never created or is unreadable
- **THEN** recovery SHALL record the uncertain-submission diagnostic and leave the binding failed as `runtime-session-missing` with Reset guidance
- **AND** it MUST NOT create a replacement Session or replay the Prompt

#### Scenario: Corrupt committed outbox state is not discarded

- **WHEN** startup cannot decode a committed stream snapshot
- **THEN** that logical Session's reporting SHALL remain unavailable with an actionable diagnostic and the committed bytes SHALL be preserved
- **AND** no Prompt or runtime rebind for that Session SHALL be admitted
- **AND** unrelated Sessions and AgentJob work SHALL remain available

#### Scenario: Outbox root failure blocks global readiness

- **WHEN** startup cannot initialize or list the outbox root or cannot provide the required atomic replace operations
- **THEN** Action reporting SHALL be globally not-ready and the Runner SHALL NOT register or claim new work
- **AND** it SHALL expose a credential-redacted actionable storage diagnostic

#### Scenario: Ambiguous delivery does not duplicate usage or transcript

- **WHEN** the Server applies an event but the acknowledgement is lost and the Runner sends the same stream sequence again
- **THEN** the Session authority SHALL acknowledge the repeated sequence without applying it again
- **AND** transcript text, tool facts, and usage totals SHALL remain unchanged by the duplicate

#### Scenario: Event sequence gaps are rejected

- **WHEN** the Session authority receives a current-binding event whose sequence skips an unapplied predecessor
- **THEN** it SHALL reject that event without changing Session state
- **AND** the Runner SHALL retain and resend the missing ordered outbox entries

#### Scenario: Runtime rebind atomically replaces the drained stream

- **WHEN** the owning Runner requests a guarded runtime rebind with the old stream's expected identity and final issued sequence
- **THEN** the Session authority SHALL replace the binding only when both equal the current stream identity and applied cursor
- **AND** it SHALL remove the old stream from current admission and create a fresh current stream in the same transition so later old-binding events are rejected

#### Scenario: Stale binding events are rejected

- **WHEN** a runtime event identifies a physical Session that is no longer the logical AgentSession's current binding
- **THEN** the Session authority SHALL reject that event
- **AND** it MUST NOT alter the current transcript, usage, or Workflow state

#### Scenario: Session facts do not complete Workflow work

- **WHEN** AgentSession receives a Pi end event or final assistant text
- **THEN** it SHALL record the facts for audit and display
- **AND** it MUST NOT complete the TaskRun or advance the Workflow
