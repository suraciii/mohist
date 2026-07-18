### Requirement: A work-owned turn resolves the physical Session and runs one awaited prompt

A Workflow Inline Agent turn SHALL execute in this order: (1) resolve or create the current physical Session via `client.session.create()`; (2) parse the optional model string and construct the SDK model DTO inside the runtime; (3) call and await `client.session.prompt()` passing the Session ID, working directory, prompt parts, and the optional model and variant; (4) project the returned assistant message and received events onto the AgentSession; (5) when final transcript confirmation is needed, read `client.session.messages()` to reconcile; (6) return the normalized completion facts to the caller. `client.session.prompt()` SHALL be the single request that carries the completion result; there SHALL be no second `wait()` call.

#### Scenario: A turn runs and returns normalized facts

- **WHEN** the Workflow Action adapter requests a turn on a logical AgentSession with a resolved prompt and optional model/variant
- **THEN** the runtime SHALL resolve or create the physical Session via `client.session.create()`
- **AND** SHALL call and await `client.session.prompt()` with the Session ID, working directory, prompt parts, and optional model and variant
- **AND** SHALL return normalized completion facts to the caller without issuing a second `wait()`

### Requirement: Turn completion follows the awaited prompt response, not events

The awaited `client.session.prompt()` response SHALL be the sole completion authority for a work turn. An `idle` event or SSE silence SHALL NOT be treated as completion or as failure. Transient event loss or event duplication MUST NOT cause the Workflow to complete early or to display a turn twice. Live events SHALL only reduce display latency. The runtime MUST NOT call `client.v2.session.wait()`.

#### Scenario: Event loss does not complete the turn early

- **WHEN** live events for an in-progress turn are briefly lost or duplicated before the prompt response arrives
- **THEN** the Workflow SHALL NOT complete the turn before the `client.session.prompt()` response arrives
- **AND** the turn SHALL NOT be displayed more than once

#### Scenario: An idle event is not completion

- **WHEN** an `idle` event arrives while `client.session.prompt()` has not yet returned
- **THEN** the runtime SHALL NOT treat the turn as complete
- **AND** SHALL continue to await the prompt response

### Requirement: The executor-owned deadline is the backstop for a silently hanging turn

The caller's abort signal SHALL be the execution deadline for a work turn. When no explicit deadline is supplied, a single prompt SHALL default to a 60-minute deadline that an explicit deadline MAY override. On deadline the runtime SHALL call `client.session.abort()` and return an `interrupted` result, and the work SHALL fail. The runtime MUST NOT run an ACP-style liveness probe or quiet-threshold detector. The deadline backstops a turn that hangs without producing provider-error retry events; provider errors that do produce retry events fail earlier per the provider-error failure policy.

#### Scenario: A deadline aborts a hanging turn

- **WHEN** a work turn's executor deadline is reached before the prompt response arrives
- **THEN** the runtime SHALL call `client.session.abort()`
- **AND** SHALL return an `interrupted` result
- **AND** the work SHALL fail

#### Scenario: No silent liveness detection runs

- **WHEN** a turn produces no visible activity for an extended period but stays within its deadline
- **THEN** the runtime SHALL NOT send a liveness probe or classify a quiet threshold
- **AND** SHALL continue until the prompt response arrives or the executor deadline aborts the turn

### Requirement: A provider error fails the turn only when judged non-recoverable

A provider error SHALL fail a work turn only when judged non-recoverable; a recoverable error (transient 429, 5xx, network jitter) SHALL be left to OpenCode to retry and SHALL NOT be failed by the runtime. The runtime SHALL judge recoverability from `session.status` retry events (`type: "retry"`, carrying `attempt`, `message`, `next`) and MUST NOT scan log files. Non-recoverability resolves to abort-and-fail in two cases: (a) non-recoverable by nature — the retry event `message` matches a non-recoverable pattern set (quota/credit/billing and equivalent wording, including non-English), matched on first occurrence; (b) non-recoverable by evidence — a recoverable error retries until `attempt` reaches threshold N (default 5) without the turn completing. A recoverable error that completes the turn within N SHALL continue without failing. Provider errors OpenCode itself judges non-recoverable (authentication, invalid-request, context-overflow, content-policy) reach the caller via the awaited prompt rejection and need no runtime override. On a non-recoverable judgement the runtime SHALL call `client.session.abort()` and return a `turn failed` result carrying the provider message as diagnostics. The non-recoverable pattern set and N SHALL be runner-level configurable with defaults covering common providers.

#### Scenario: A quota-exhausted error fails on the first retry event

- **WHEN** a `session.status` retry event's `message` matches a non-recoverable (quota/credit/billing) pattern
- **THEN** the runtime SHALL call `client.session.abort()` and fail the turn on the first occurrence
- **AND** SHALL surface the provider message as diagnostics

#### Scenario: A recoverable transient error is retried, not failed

- **WHEN** a transient provider error occurs and the turn completes within the consecutive-retry threshold N
- **THEN** the runtime SHALL NOT fail the turn
- **AND** SHALL let OpenCode retry until the turn completes

#### Scenario: Consecutive failures are judged non-recoverable

- **WHEN** a recoverable error retries and `attempt` reaches threshold N (default 5) without the turn completing
- **THEN** the runtime SHALL call `client.session.abort()` and fail the turn
- **AND** SHALL surface the provider message as diagnostics

#### Scenario: Provider-error detection reads retry events, not logs or quiet detectors

- **WHEN** the runtime judges provider-error recoverability
- **THEN** it SHALL read `session.status` retry events only
- **AND** SHALL NOT scan OpenCode log files or run a quiet-threshold/liveness detector

### Requirement: The turn supplies the final assistant text as a private turn fact

The runtime SHALL include the turn's final assistant text in the normalized turn fact supplied to the Workflow task executor, so the executor can evaluate a `path: _output` expect marker against it. The final text SHALL travel via the Action result's turn fact and MUST NOT be placed in Action Output. The runtime MUST NOT evaluate Workflow expectations and MUST NOT synthesize a `{ promise }` output; the Workflow task executor applies `expect`, `failIf`, Action Output projection, and recovery semantics after the Action returns.

#### Scenario: `_output` evaluates against the private final text

- **WHEN** an OpenCode turn completes and the Workflow declares a `path: _output` expect marker
- **THEN** the runtime SHALL supply the turn's final assistant text as a private turn fact
- **AND** the Workflow task executor SHALL evaluate the marker against that text
- **AND** the text MUST NOT appear in Action Output

#### Scenario: The runtime does not synthesize the promise output

- **WHEN** an OpenCode turn matches a configured promise marker
- **THEN** the runtime SHALL NOT produce the `{ promise }` object
- **AND** the Workflow task executor SHALL synthesize `{ "promise": "<value>" }` from the matched marker

### Requirement: Uncertain prompt admission is not blindly retried

Prompt submission and any response whose outcome is uncertain MUST NOT be blindly retried. The runtime SHALL preserve the existing in-process dispatch deduplication. Redelivery within a crash window MAY cause a duplicate turn; this is an accepted limitation, and the runtime MUST NOT add a deterministic Prompt ID or replay reconstruction to mask it.

#### Scenario: An uncertain response is not retried

- **WHEN** a prompt submission response is uncertain (for example the connection drops before the result is confirmed)
- **THEN** the runtime SHALL NOT automatically resubmit the prompt
- **AND** SHALL rely on in-process dispatch deduplication for any redelivery

### Requirement: Physical Session reuse is governed by binding, runtime, and directory only

Whether a physical Session is reused SHALL be determined solely by the logical AgentSession's current binding, its runtime, and its working directory. The same WorkflowRun and session name SHALL resolve to the current binding across tasks, retries, and Runner restarts. A runtime change, a working-directory change, or a Reset SHALL create a new physical binding and append lineage without migrating context. Compact and model or variant changes MUST keep the same physical Session ID. Model and variant are turn-execution parameters: they SHALL NOT enter the Session cache key, SHALL NOT gate whether the session is resumed, and SHALL NOT trigger a binding replacement or lineage entry. When a persisted binding exists but the runtime cannot restore that physical Session, the turn SHALL fail with a Reset hint and MUST NOT implicitly call `create` to fabricate continuous context.

#### Scenario: Same session name reuses the current binding across tasks

- **WHEN** two tasks in the same WorkflowRun use the same logical session name
- **THEN** both SHALL resolve to the AgentSession's current binding
- **AND** the second task SHALL reuse the same physical Session ID

#### Scenario: A model or variant change does not rotate the session

- **WHEN** a task supplies a different `options.model` or `options.variant` than the previous task on the same logical session
- **THEN** the runtime SHALL apply the new model/variant on the existing physical Session
- **AND** SHALL NOT create a new physical Session, replace the binding, or append lineage

#### Scenario: A directory change creates a new physical session

- **WHEN** a logical session's working directory changes
- **THEN** the runtime SHALL create a new physical Session in the new directory
- **AND** SHALL append a lineage entry without migrating the prior session's context

#### Scenario: An unrestorable persisted binding fails with a Reset hint

- **WHEN** a persisted binding exists but the runtime cannot restore that physical Session
- **THEN** the turn SHALL fail with an explicit error prompting a Reset
- **AND** the runtime MUST NOT implicitly call `create` to fabricate continuous context

### Requirement: Each logical AgentSession runs at most one work prompt at a time

A logical AgentSession SHALL run at most one work-initiated prompt concurrently, regardless of whether the owner is a TaskRun or an AgentJob. Different logical AgentSessions MAY run in parallel. A user Follow-up is a Session command and MAY be received while a work turn is active.

#### Scenario: Concurrent work prompts on one session are not allowed

- **WHEN** two work-initiated prompts target the same logical AgentSession concurrently
- **THEN** at most one SHALL execute at a time

#### Scenario: A user follow-up is accepted during a work turn

- **WHEN** a user Follow-up is sent to a logical AgentSession that currently has an active work turn
- **THEN** the Follow-up SHALL be receivable as a Session command without ending the work turn

### Requirement: Restart and reconnect reconcile from the persisted binding and a snapshot

On Runner restart or event-stream reconnect, the runtime SHALL continue routing from the persisted AgentSession binding and SHALL reconcile state by reading `client.session.status()` together with the relevant `client.session.get()` / `client.session.messages()` snapshot. After a prompt completes, if events are missing or the final visible transcript must be confirmed, the runtime SHALL reconcile against `session.messages()`. The runtime MUST NOT maintain a V2 history cursor, an aggregate sequence, or any event replay state, and MUST NOT add a deterministic Prompt ID for reconciliation.

#### Scenario: Reconnect reconciles from a snapshot

- **WHEN** the event stream reconnects after a drop
- **THEN** the runtime SHALL read `client.session.status()` and the relevant `session.get()` / `session.messages()` snapshot
- **AND** SHALL resume routing from the persisted binding

#### Scenario: No replay state is maintained

- **WHEN** the runtime reconciles state after restart or reconnect
- **THEN** it SHALL NOT maintain a V2 history cursor, aggregate sequence, or event replay state
- **AND** it SHALL NOT add a deterministic Prompt ID
