### Requirement: The runner has no ACP SDK dependency

The runner SHALL NOT declare `@agentclientprotocol/sdk` as a dependency. The runner source SHALL NOT import any symbol from `@agentclientprotocol/sdk` (including `ClientSideConnection`, `ndJsonStream`, `PROTOCOL_VERSION`, `SessionNotification`, `RequestPermissionRequest`, or `RequestPermissionResponse`). No transitive workaround (vendored copy, fork, or shim) SHALL reintroduce the SDK.

#### Scenario: The runner package omits the ACP SDK

- **WHEN** the runner `package.json` is inspected
- **THEN** the dependencies SHALL NOT include `@agentclientprotocol/sdk`
- **AND** SHALL retain `@opencode-ai/sdk`

#### Scenario: The runner source has no ACP SDK imports

- **WHEN** the runner source tree is inspected for `@agentclientprotocol/sdk` imports
- **THEN** no file SHALL import from that package

### Requirement: The mohist/acp-agent Action and the ACP Action tree are absent

The runner SHALL NOT register a `mohist/acp-agent` Action. The runner source tree SHALL NOT contain an `actions/acp-agent.ts` entry or an `actions/acp/` subtree. The deleted ACP modules — process spawning, liveness probing, compaction metadata, model resolution via ACP, session-event normalization for ACP, agent-config resolution for ACP, and the ACP session strategies — SHALL NOT reappear under any other name. The `actions/opencode.ts` Action SHALL NOT depend on helpers located under `actions/acp/`; any helpers it still needs (prompt-loader context, session-name derivation) SHALL live outside the deleted subtree.

#### Scenario: The Action registry has no ACP entry

- **WHEN** the runner's default Action registry is built
- **THEN** it SHALL register `mohist/opencode`
- **AND** SHALL NOT register `mohist/acp-agent`

#### Scenario: The ACP source tree is gone

- **WHEN** the runner source tree is inspected
- **THEN** there SHALL be no `actions/acp-agent.ts`
- **AND** there SHALL be no `actions/acp/` directory
- **AND** the OpenCode Action SHALL NOT import any helper from either location

### Requirement: The runner has no shared ACP runtime connection

The runner source tree SHALL NOT contain `runtime/acp-connection.ts`, `runtime/acp-command.ts`, or `runtime/acp-session-command.ts`. The types `SharedAcpConnection`, `AcpSessionManager`, and the ACP-flavored `SessionTarget`/`FollowupTarget` discriminator SHALL NOT exist. The runner host SHALL NOT spawn an `opencode acp` child process, SHALL NOT initialize a shared ACP connection, and SHALL NOT maintain an in-memory ACP session-entry cache. The runner SHALL NOT read the `MOHIST_AGENT_ARGS` environment variable; that surface disappears entirely.

#### Scenario: No ACP runtime source files exist

- **WHEN** the runner source tree is inspected
- **THEN** there SHALL be no `runtime/acp-connection.ts`, `runtime/acp-command.ts`, or `runtime/acp-session-command.ts`
- **AND** no surviving file SHALL export or import `SharedAcpConnection`, `AcpSessionManager`, or an ACP-flavored `SessionTarget`

#### Scenario: The runner host does not spawn an ACP process

- **WHEN** the runner host starts up
- **THEN** it SHALL NOT spawn `opencode acp`
- **AND** SHALL NOT initialize a shared ACP connection
- **AND** SHALL NOT register ACP session handlers

#### Scenario: MOHIST_AGENT_ARGS is not read

- **WHEN** the runner process environment is inspected for `MOHIST_AGENT_ARGS` reads
- **THEN** no runner source file SHALL reference that variable

### Requirement: The ActionContext and runner handlers have no ACP fields or coupling

The `ActionContext` type SHALL NOT expose `acpSessionManager` or `acpConnection` fields. The `WorkExecutor` SHALL NOT hold an `AcpSessionManager` or `SharedAcpConnection`, and SHALL NOT thread them through `baseContext`. The session-command and runtime-event handlers (Follow-up, Cancel, followup-failure-outbox, session-target resolution) SHALL NOT reference a live `ClientSideConnection`; they SHALL target the shared OpenCode runtime. The `system/timeout-signal` module SHALL NOT reference `mohist/acp-agent`.

#### Scenario: ActionContext carries no ACP fields

- **WHEN** the `ActionContext` type is inspected
- **THEN** it SHALL NOT declare `acpSessionManager` or `acpConnection`
- **AND** it SHALL continue to expose the OpenCode runtime handle

#### Scenario: The work executor threads no ACP state

- **WHEN** the `WorkExecutor` constructor and `baseContext` helper are inspected
- **THEN** they SHALL NOT accept or set any ACP session manager or connection
- **AND** SHALL pass the OpenCode runtime handle uniformly across owner kinds

#### Scenario: Follow-up, Cancel, and failure-outbox target the OpenCode runtime

- **WHEN** the Follow-up, Cancel, and followup-failure-outbox handlers route a session command
- **THEN** they SHALL resolve the target through the OpenCode runtime
- **AND** SHALL NOT call `ClientSideConnection.prompt`, `ClientSideConnection.cancel`, or any other ACP RPC

### Requirement: The server has no recognition of mohist/acp-agent

The server's inline-agent classifiers (`IsInlineAgentUses` in the workflow item translator and YAML serializer) SHALL recognize `mohist/opencode` only. The `TaskRun` classification SHALL NOT branch on the `acp-agent` literal. Recovery-task fixtures SHALL NOT hard-code `mohist/acp-agent` as the Action. The `AgentJobGrain.BuildDispatch` fallback for a blank `Uses` SHALL NOT select `mohist/acp-agent`. The `AgentLauncher` SHALL NOT pass `mohist/acp-agent` on the AgentJob input.

#### Scenario: The inline-agent classifier accepts OpenCode only

- **WHEN** the workflow item translator or YAML serializer classifies a task's `uses` value
- **THEN** it SHALL treat `mohist/opencode` as an inline agent
- **AND** SHALL NOT treat `mohist/acp-agent` as an inline agent

#### Scenario: TaskRun classification does not branch on the ACP literal

- **WHEN** `TaskRun.DeriveClassification` derives a classification from a `uses` value
- **THEN** the classification logic SHALL NOT consult the substring `acp-agent`

#### Scenario: The AgentJob dispatch no longer carries an ACP fallback

- **WHEN** the `AgentJobGrain` builds a WorkDispatch for a blank `Uses`
- **THEN** the dispatch SHALL NOT select `mohist/acp-agent`
- **AND** the AgentJob pipeline SHALL NOT depend on a Workflow Action name to drive execution

#### Scenario: Recovery tasks do not name the ACP Action

- **WHEN** the server builds a recovery task (e.g. rebase-conflict recovery)
- **THEN** the recovery task's `uses` SHALL NOT be `mohist/acp-agent`

### Requirement: Web surfaces carry no ACP terminology

The web milestone classifier SHALL NOT branch on the `'mohist/acp-agent'` literal; agent-task detection SHALL be based on the OpenCode runtime/Action identity or an equivalent runtime-neutral signal. The web source tree SHALL NOT contain the identifier `acpSessionId` in production code. User-visible strings, page content, and developer-facing doc-comments SHALL NOT name ACP, the ACP action, or ACP liveness.

#### Scenario: The milestone classifier is runtime-neutral or OpenCode-based

- **WHEN** the web classifies a task as an agent task for milestone rendering
- **THEN** the classifier SHALL NOT compare `uses` against `'mohist/acp-agent'`
- **AND** SHALL resolve agent-task status from the OpenCode runtime/Action identity or a runtime-neutral signal

#### Scenario: No ACP wire field appears in production web code

- **WHEN** the web production source tree is inspected for `acpSessionId`
- **THEN** no production file SHALL reference that identifier

### Requirement: The runner readiness gate is unified across owner kinds

The runner SHALL apply a single readiness gate to all dispatches: a dispatch SHALL be claimed only when the OpenCode runtime reports ready and the model catalog is readable. The runner SHALL NOT carry a transitional caveat that pauses AgentJob work separately on the grounds that it still runs on ACP. On OpenCode Server exit or unreadable catalog, the runner SHALL stop claiming new work of any owner kind and SHALL emit actionable diagnostics, resuming only after both re-pass.

#### Scenario: The readiness gate covers both owner kinds

- **WHEN** the runner host evaluates whether to claim new work
- **THEN** the gate SHALL apply the same OpenCode-readiness and catalog checks to Workflow and AgentJob work
- **AND** SHALL NOT retain a separate AgentJob-on-ACP caveat

#### Scenario: Readiness failure stops all new work with actionable diagnostics

- **WHEN** the OpenCode Server exits or the model catalog is unreadable
- **THEN** the runner SHALL stop claiming new work of any owner kind
- **AND** SHALL emit actionable diagnostics and resume only after both checks re-pass

### Requirement: Legacy ACP-bound AgentSessions fail Session operations with a Reset hint

An AgentSession whose persisted runtime binding is not `opencode` SHALL be treated as having no current Runtime Session. Session operations (Follow-up, Compact, Reset, Cancel) on such a session SHALL fail explicitly with a `RuntimeSessionMissing`-style error whose message instructs the user to Reset the session to establish a new binding. Legacy AgentSessions, historical Compact/Reset rotation records, and any persisted `acpSessionId`-style fields SHALL remain queryable and auditable. The server SHALL NOT rewrite stored data to migrate legacy bindings.

#### Scenario: A legacy ACP-bound session rejects Session operations with a Reset hint

- **WHEN** a Session command targets an AgentSession whose runtime binding is not `opencode`
- **THEN** the operation SHALL fail with a missing-runtime-session error
- **AND** the error message SHALL instruct the user to Reset the session to establish a new binding

#### Scenario: Reset rebinds a legacy session to the OpenCode runtime

- **WHEN** a user issues Reset against a legacy ACP-bound AgentSession
- **THEN** the AgentSession SHALL accept the Reset under the expected-binding guard
- **AND** SHALL replace the binding with a fresh OpenCode Runtime Session while preserving the stable `sessionId`

#### Scenario: Legacy session history stays queryable

- **WHEN** a caller queries or audits a legacy ACP-bound AgentSession
- **THEN** the session, its transcript, and its lineage SHALL remain readable
- **AND** the server SHALL NOT rewrite the historical records

### Requirement: Pre-cutover WorkflowRuns fail subsequent agent task dispatch with a rerun-recoverable error

A WorkflowRun started before cutover whose persisted tasks reference `mohist/acp-agent` SHALL NOT be auto-migrated to `mohist/opencode`. Subsequent agent task dispatches on such a run SHALL fail with an actionable error that names the removed Action and points the user to rerun the affected stage. The user SHALL recover by rerunning the affected stage using a profile that uses `mohist/opencode`. A run that does not reference `mohist/acp-agent` SHALL continue to dispatch normally.

#### Scenario: A pre-cutover agent task dispatch fails actionably

- **WHEN** a pre-cutover WorkflowRun dispatches a task whose persisted `uses` is `mohist/acp-agent`
- **THEN** the dispatch SHALL fail with an actionable error that names the removed Action
- **AND** the error SHALL point the user to rerun the affected stage with a `mohist/opencode` profile

#### Scenario: The affected stage is recoverable via rerun

- **WHEN** the user reruns the affected stage of a pre-cutover WorkflowRun using a profile that uses `mohist/opencode`
- **THEN** the rerun SHALL dispatch successfully through the OpenCode runtime
- **AND** SHALL NOT carry forward the removed Action identity

#### Scenario: A run without the removed Action is unaffected

- **WHEN** a pre-cutover WorkflowRun whose tasks do not reference `mohist/acp-agent` continues dispatching
- **THEN** the dispatches SHALL succeed or fail per their normal contract
- **AND** SHALL NOT be failed solely because of cutover

### Requirement: Custom profiles naming the removed Action fail at load or dispatch

A custom workflow profile that names `uses: mohist/acp-agent` SHALL fail at profile load or at the latest at task dispatch with an actionable error that names the removed Action. The failure SHALL NOT be silent, SHALL NOT fall back to another Action, and SHALL NOT be masked as a generic "no action found" message.

#### Scenario: A custom profile naming the removed Action is rejected

- **WHEN** a custom profile is loaded whose task declares `uses: mohist/acp-agent`
- **THEN** loading or dispatch SHALL fail with an actionable error that names the removed Action
- **AND** SHALL NOT fall back to another Action or be masked as a generic "no action found" message
