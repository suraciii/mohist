### Requirement: Each Runner runs a single shared OpenCode Server, Client, and event subscription

A Runner process SHALL own exactly one OpenCode Server, one Client, and one `client.global.event()` subscription, shared by every OpenCode Session it owns. The Runner SHALL obtain these through the official `createOpencodeServer()` and `createOpencodeClient()` APIs and MUST NOT spawn or parse an OpenCode process directly, MUST NOT pass `--pure`, and MUST NOT clean up a `.opencode` lockfile. Each SDK call SHALL pass the target Session's working directory explicitly; the Runner MUST NOT fork a separate process per Action or per Session.

#### Scenario: One server backs many sessions

- **WHEN** a Runner owns several concurrent Workflow Inline Agent turns on different logical AgentSessions
- **THEN** all turns SHALL be served by the same shared OpenCode Server, Client, and global event subscription
- **AND** no additional OpenCode process SHALL be spawned for the additional turns

#### Scenario: Native workspace configuration loads untouched

- **WHEN** the Runner starts the shared OpenCode Server
- **THEN** native OpenCode workspace configuration, plugins, tools, and permissions SHALL load normally
- **AND** the Runner SHALL NOT pass `--pure` or remove any `.opencode` lockfile

### Requirement: `OpenCodeRuntime` is a deep module that isolates SDK types

The Runner SHALL expose an `OpenCodeRuntime` module that owns OpenCode Server/Client lifecycle, readiness, model-catalog access, physical Session create/query/reuse/interrupt, Prompt execution, Follow-up, Compact, Reset, event subscription, message-snapshot reconciliation, and OpenCode error and compatibility interpretation. Callers (`mohist/opencode`, the AgentJob execution adapter, and the Session-command handler) SHALL depend only on Mohist-defined request/result types and MUST NOT receive generated SDK DTOs. The module SHALL receive already-assembled turn input and a resolved logical Session binding; it MUST NOT receive a Mohist Agent ID or name and MUST NOT load Mohist Agent definitions. Model-string parsing, SDK DTO construction, call ordering, reconnect, and error interpretation SHALL be encapsulated inside the module.

#### Scenario: Callers consume Mohist-owned types

- **WHEN** the `mohist/opencode` Action adapter or a Session-command handler invokes the runtime
- **THEN** the request and result SHALL use Mohist-owned shapes only
- **AND** no generated SDK type SHALL cross the runtime boundary into the caller

#### Scenario: The runtime is not a passthrough wrapper

- **WHEN** a caller requests a Mohist capability such as "run turn", "follow up", "compact", or "reset"
- **THEN** the runtime SHALL decide which SDK operations and state-reconciliation steps are required to satisfy that capability
- **AND** the caller SHALL NOT select individual SDK operations

### Requirement: The Runner gates new work on OpenCode readiness before claiming it

Before registering or claiming any work, the Runner SHALL (1) start the shared OpenCode Server, (2) pass an OpenCode health check, and (3) successfully load the model catalog. If OpenCode is unhealthy or the catalog cannot be read, the Runner SHALL stop claiming new work and SHALL emit an actionable readiness diagnostic identifying the failure and how to recover. On OpenCode Server exit, the Runner SHALL stop claiming new work, rebuild the Server, Client, and global event subscription, and resume `ready` only after the health check and catalog load both re-pass. In-flight turns affected by the loss SHALL fail and MUST NOT be automatically replayed. During the transition this readiness gate SHALL also cover AgentJob work that still runs on ACP until issue #410.

#### Scenario: Work is claimed only when OpenCode is ready

- **WHEN** the Runner starts and OpenCode passes health check and the model catalog loads
- **THEN** the Runner SHALL become eligible to register and claim new work

#### Scenario: Unhealthy OpenCode stops new work with a diagnostic

- **WHEN** OpenCode is unhealthy or its model catalog cannot be read
- **THEN** the Runner SHALL stop claiming new work
- **AND** SHALL emit an actionable readiness diagnostic identifying the failure and recovery steps

#### Scenario: Server exit fails in-flight turns and prevents auto-replay

- **WHEN** the shared OpenCode Server exits while a Workflow turn is running
- **THEN** the affected turn SHALL fail
- **AND** the Runner SHALL NOT automatically replay that turn
- **AND** the Runner SHALL NOT claim new work until health check and catalog load re-pass after rebuild

### Requirement: The SDK call surface is pinned and smoke-verified

The Runner SHALL depend on a pinned `@opencode-ai/sdk/v2` version and SHALL implement execution using the mature `client.session.*` namespace (`create`, `prompt`, `promptAsync`, `abort`, `summarize`, `get`, `messages`, `status`), `client.global.event()` for live events, and the read-only `client.v2.model.list()` / `client.v2.provider.list()` for the catalog. The Runner MUST NOT call the currently-unavailable `client.v2.session.wait()` or `client.v2.session.compact()`. Before implementation proceeds, the asserted call surface SHALL be smoke-verified against a real OpenCode, and any drift SHALL be reconciled into the design before implementation continues.

#### Scenario: Execution uses the mature session namespace

- **WHEN** the runtime creates a session, runs a turn, summarizes, aborts, or reconciles state
- **THEN** it SHALL use `client.session.*` operations and `client.global.event()`
- **AND** it MUST NOT call `client.v2.session.wait()` or `client.v2.session.compact()`

#### Scenario: The dependency is pinned and verified

- **WHEN** the runtime is implemented
- **THEN** `@opencode-ai/sdk/v2` SHALL be pinned to a fixed version
- **AND** the asserted call surface SHALL have a recorded smoke verification against a real OpenCode

### Requirement: SDK errors normalize to a small set of actionable Mohist results

At the runtime boundary, SDK errors SHALL be normalized into a small set of Mohist results: `invalid input`, `unavailable runtime`, `missing Session`, `incompatible runtime`, `permission required`, `interrupted`, and `turn failed`. Provider-specific detail SHALL be carried only as diagnostic information and MUST NOT become an Action Output field. When permissions are unsatisfied, the Runtime Session is missing, OpenCode is incompatible, or the process exits, the user SHALL see a clear error with an actionable recovery suggestion. The runtime MUST NOT establish a global Workflow error enum; each caller reports failure through its own TaskRun or AgentJob contract.

#### Scenario: A provider error is normalized as diagnostics

- **WHEN** an OpenCode turn fails with a provider-specific error such as a quota or authentication failure
- **THEN** the runtime SHALL return a `turn failed` result carrying the provider detail as diagnostics only
- **AND** the provider detail SHALL NOT appear as an Action Output field

#### Scenario: An incompatible runtime yields an actionable readiness error

- **WHEN** the installed OpenCode is incompatible with the pinned SDK surface
- **THEN** the runtime SHALL surface an `incompatible runtime` error with an actionable recovery suggestion
- **AND** the Runner SHALL NOT claim new work until the incompatibility is resolved

### Requirement: OpenCode permissions are authoritative and never auto-approved

OpenCode's native permission configuration SHALL be authoritative. The runtime MUST NOT automatically approve OpenCode permission requests and MUST NOT translate an OpenCode permission prompt into a Workflow Approval. When a headless turn encounters an interactive permission request it cannot satisfy, the runtime SHALL abort the current turn and return an actionable `permission required` error.

#### Scenario: An unsatisfiable interactive permission aborts the turn

- **WHEN** a headless Workflow turn encounters an interactive permission request that cannot be satisfied
- **THEN** the runtime SHALL abort the turn
- **AND** SHALL return a `permission required` error with an actionable recovery suggestion
- **AND** SHALL NOT auto-approve the request or create a Workflow Approval

### Requirement: Default tests do not start a real OpenCode

Default Runner tests SHALL NOT start a real OpenCode process and SHALL NOT use real process, network, filesystem configuration, or clock. Tests SHALL inject a fake `OpenCodeRuntime` or a fake generated Client/Server factory that deterministically drives events, snapshots, completion state, process loss, and errors.

#### Scenario: Tests drive the runtime through a fake

- **WHEN** default Runner tests exercise runtime behavior
- **THEN** they SHALL inject a fake `OpenCodeRuntime` or fake SDK factory
- **AND** no real OpenCode process, network call, filesystem configuration, or wall clock SHALL be used
