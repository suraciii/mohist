# Codex Runtime

`CodexRuntime` adapts Mohist AgentJob execution and AgentSession commands to
Codex app-server v2. It keeps the child process, JSON-RPC protocol, Thread,
Turn, item, permission, and provider details inside one deep Runner module.
[`agent-execution.md`](../agent-execution.md) remains authoritative for
AgentJob, AgentSession, binding, fencing, and Runner-generation rules.

## Design Drivers

- Codex app-server is the supported product-embedding boundary. Mohist uses it
  directly instead of placing ACP or another agent protocol between the Runner
  and Codex.
- A Codex Thread is durable conversation context. A Codex Turn is one provider
  execution inside that Thread. Neither becomes a Mohist domain identity.
- App-server requests may have taken effect when a response or process is lost.
  Mohist must preserve uncertainty and never replay an unconfirmed submission.
- Runner restart ends ownership of active execution. Persisted Codex history may
  support later input, but cannot prove or complete the old AgentTurn.
- App-server protocol and generated schemas change with the Codex CLI. Mohist
  locks only the stable methods and shapes it uses; protocol incompatibility
  must fail readiness before the Runner claims Codex work.
- Codex can request approvals or user input during a Turn. Mohist v1 has no
  interactive Runtime-approval product, so headless execution must fail closed.

Mohist implements one independent `CodexRuntime`. It does not add a generic
Runtime interface, generic ACP harness, or `mohist/codex` Workflow Action.
AgentJob execution and Session commands are the stable boundaries. OpenCode,
Pi, and Codex remain peers because their process, completion, approval, and
physical Session semantics differ.

## Model

### Capability Boundary

```text diagram
+----------+
| Workflow ++
+----------+|                                      +----------------+
            |                                  +-->| Threads / Turns |
+----------+|   +--------------+   +----------+|   +----------------+
| AgentJob ++-->| CodexRuntime +-->| app-server++
+----------+|   +--------------+   +----------+|   +----------------+
            |                                  +-->| Models / items  |
+----------+|                                      +----------------+
| Commands ++
+----------+
```

`CodexRuntime` owns process startup and shutdown, protocol initialization,
schema compatibility, readiness, model catalog, physical Thread creation and
resumption, Turn submission and completion, event projection, interruption,
compaction, approval and user-input rejection, provider-error normalization,
managed Codex state, and credential redaction.

Callers use Mohist request and result types with `runtime: "codex"`.
App-server request IDs, Thread and Turn DTOs, item payloads, and raw provider
errors do not cross the module. The module receives fully assembled execution
input and a resolved AgentSession binding. It receives no Mohist Agent identity
and does not load an Agent definition. Workflow expectations, artifacts,
WorkResults, and Session ownership remain with their existing authorities.

`codex` is a Runtime value, not a new domain concept. A Codex Thread ID is stored
only as the existing opaque `runtimeSessionId`. The current Codex Turn ID is
volatile Runtime correlation used to route events and interrupt the exact Turn;
it is not added to AgentSession, AgentTurn, or their persistence models.

### Process Topology and Generation

Each Runner process owns one `CodexRuntime` and one long-lived
`codex app-server --stdio` child process. The process serves multiple Threads
and concurrent AgentSessions. Do not start one app-server per AgentJob, Thread,
or working directory.

The Runtime launches app-server without a shell, bounds startup and shutdown,
and reads one JSON-RPC message per line from stdout. Stderr is diagnostic only.
Malformed stdout, duplicate response IDs, an unexpected server request, or
child exit crosses one protocol-failure boundary; it cannot be interpreted as
a Turn result.

After spawn, send `initialize` once with no experimental client capabilities,
require a compatible response whose `codexHome` is the exact managed state
path, then send `initialized`. No other request is admitted before
initialization completes.
Readiness requires all of the following:

- A configured compatible Codex CLI is executable.
- app-server starts and completes initialization within the startup budget.
- The CLI version is within the supported range, and the locked compatibility
  smoke test has proved every stable v2 method and event shape Mohist uses.
- Managed Codex authentication is present.
- Model discovery has completed successfully.

A missing executable, missing authentication, or incompatible protocol makes
Codex not ready and stops this Runner from claiming Codex work. An empty catalog
is a visible setup gap, not permission to use another Runtime. A temporarily
offline provider after readiness is an execution failure, not a different
Runtime selection.

The Runner process generation owns the app-server process and every active
request. If either process exits, the Runtime rejects active calls, discards
volatile Turn correlation, stops claiming Codex work, and starts a fresh
app-server generation. The Server closes work owned by the lost Runner
generation as `runner-lost`. A replacement generation never adopts an active
old Codex Turn, reports its completion, or reconstructs its result from Thread
history.

### Managed State and Trust

Codex executes unattended with the same operating-system authority as the
Runner. Start every Thread with approval policy `never` and sandbox
`danger-full-access`; there is no interactive approval channel and no hidden
fallback to a more restrictive mode that would hang awaiting input. This trust
boundary matches other Runner execution: the Runner host and Workspace
isolation are the security boundary.

Launch app-server with `CODEX_HOME` fixed to
`<runnerRoot>/.mohist/codex`. Its configuration, authentication, logs, and
Thread history must not read or write a person's default Codex home.
Installation and authentication are operator setup; Mohist never copies
personal credentials or installs or upgrades Codex. The managed path is not an
Agent, Workspace, or Runtime Session identity.

Repository instructions and skills remain model context through Codex's normal
loading rules. Mohist assembles Agent Instructions, resolved Skills, task input,
context, and attachments before the Runtime and submits that assembled input as
the Turn user input. It does not encode Agent identity in app-server metadata or
mutate global Codex configuration for one Agent.

App-server can still issue server-initiated approval, permission, user-input,
MCP elicitation, or dynamic-tool requests. Mohist v1 does not answer them on a
user's behalf. Reject the request when the protocol defines a denial response,
interrupt the exact active Turn, and return `permission-required` only after
the Turn reaches a terminal state. If denial or interruption is unconfirmed,
return the existing unknown or interruption-unconfirmed diagnostic and keep the
AgentSession binding unchanged. Never create a Workflow Approval Point.

## Semantics

### Model Catalog and Execution Configuration

Use every page of app-server `model/list` after initialization and on the
existing bounded catalog-refresh schedule. Publish models under
`runtimeCatalogs.codex` with the intersection of reasoning efforts reported for
each model and Mohist's canonical effort values. Unknown native effort values
remain diagnostics. Publish no variants in v1. Model IDs remain opaque Codex
IDs; do not split them as OpenCode `provider/model` values.

A complete non-empty catalog replaces the Codex snapshot. Empty or failed
refreshes retain the last complete snapshot while readiness and diagnostics
show the current failure. Only a changed snapshot triggers an immediate
registration heartbeat. The configured model and Reasoning Effort are
validated again at `turn/start`; catalog presence is configuration evidence,
not execution authority.

`variant` is rejected as `unsupported_execution_configuration`. Unknown
execution options are ignored with a diagnostic. A Codex failure never falls
back to OpenCode or Pi.

### Session Binding

The shared target resolution, binding order, reuse, and missing-recovery rules
are authoritative in
[`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery).
For Codex, `runtimeSessionId` is the app-server Thread ID returned by
`thread/start` or confirmed by `thread/resume`.

For a new physical Session:

1. Call `thread/start` with the immutable working directory, managed trust
   settings, selected model, and non-ephemeral history.
2. Validate the response and obtain its Thread ID.
3. Persist the complete AgentSession binding with the normal compare-and-swap.
4. Persist or confirm the SessionInput and AgentTurn identities.
5. Only then call `turn/start`.

`thread/start` has no caller-provided idempotency key. If its response is lost
after creation may have occurred, the candidate is unknown: do not retry
creation, guess a Thread from history, persist a binding, or submit the input.
An unbound empty Thread may remain for bounded Runtime housekeeping, but it is
not adopted later.

For a persisted binding, call `thread/resume` with `excludeTurns: true` on the
bound Runner before new independent input when the Thread is not already known
in this app-server generation. Only a structured not-found result from that
bound managed state is `definitely-missing`. Timeout, process exit,
authentication, permission, protocol, storage, or unclassified failures remain
unknown and cannot replace the binding.

After `definitely-missing`, create one replacement Thread only when the shared
AgentSession rules permit missing recovery and no old input is uncertain. Reset
also creates a new Thread and atomically replaces the binding. Neither path
replays the previous transcript into Codex.

### Turn Execution and Completion

After the binding and Input identity are durable, call `turn/start` with the
exact Thread ID and assembled user input. Set the selected Model and Reasoning
Effort through supported v2 fields. `clientUserMessageId` carries the Mohist
SessionInput ID for correlation only; it is not treated as a provider
idempotency guarantee.

The `turn/start` response identifies the current Codex Turn. Route subsequent
events by the exact Thread and Turn IDs. Item events project assistant text,
reasoning summaries, command and tool activity, file changes, usage, and
diagnostics through the existing transcript and activity boundary. Unknown item
types are diagnostics only and never change execution state.

`turn/completed` for the exact active Thread and Turn is the sole provider
terminal signal. Accept its terminal status once, reconcile final assistant
text from the Turn items, and resolve one normalized Runtime result. `completed`
is successful Runtime completion, `failed` is normalized from the structured
Turn error, and `interrupted` confirms interruption unless a previously fixed
deadline or permission result owns the outcome. Thread status events, item
completion, silence, EOF, or a successful interrupt response cannot complete an
AgentJob.

If the `turn/start` response is lost after submission may have occurred, the
effect is unknown. Do not send the same Input again, even with the same
`clientUserMessageId`. Attempt interruption only when the exact Turn ID is
known; otherwise preserve unknown until the Runner generation is fenced or an
operator uses the existing force-reset path.

The work owner supplies one absolute deadline from injected time. A closeout
warning may use `turn/steer` on the exact active Turn. At the deadline, fix the
result as `deadline-exceeded`, send `turn/interrupt`, and await the terminal
interrupted event within a bounded confirmation budget. A late completion does
not reverse the fixed deadline result. Unconfirmed interruption remains
unknown; it does not authorize replay or binding replacement.

### Follow-up and Session Commands

Commands use the persisted binding. Runner memory is only a cache. Every
operation carries the expected binding and enters the same per-Session fence.

- **Follow-up:** If the current Turn is steerable, `turn/steer` with the frozen
  `expectedTurnId` admits the Input into that Turn. Otherwise the accepted Input
  queues and later starts a new `turn/start` on the same Thread. Neither path
  creates an AgentJob.
- **Compact:** Allow only while idle and use `thread/compact/start`. Keep Thread
  and AgentSession identity. Its matching compaction Turn must reach
  `turn/completed` and emit a `contextCompaction` item; the deprecated
  `thread/compacted` notification is not completion authority.
- **Reset:** Allow only while idle. Create an empty Thread in the same working
  directory, then compare-and-swap the complete binding. Preserve logical
  transcript and AgentSession identity.
- **Stop:** Send `turn/interrupt` with the frozen Thread and Turn IDs. The RPC
  response means only that the request was accepted; only the matching terminal
  event confirms interruption.

After Runner restart, a later independently accepted Follow-up may resume the
same Thread and starts a new AgentTurn. It does not continue, adopt, or report
the old generation's Turn. If old submission remains uncertain, the Session
stays blocked until existing reconciliation or force-reset rules permit a new
context.

### Errors and Diagnostics

Normalize app-server and provider failures to existing Mohist kinds:
`invalid-input`, `unavailable-runtime`, `missing-session`,
`incompatible-runtime`, `permission-required`, `deadline-exceeded`,
`interrupted`, or `turn-failed`. Preserve structured Codex error information in
redacted diagnostics. Do not add Codex protocol names to domain models or
create a global Workflow error enum.

Authentication failure is a readiness gap when discovered before claiming
work and `unavailable-runtime` if credentials become invalid during execution.
Unsupported methods, invalid event shapes, or schema mismatch are
`incompatible-runtime`. Context overflow, usage limits, policy failures, and
provider exhaustion are terminal `turn-failed` details unless an existing
stable error kind is an exact match. Transport failure after submission stays
unknown regardless of provider retry hints.

## Upgrade Boundary

Mohist locks a reviewed app-server v2 contract derived from the official
`generate-ts` and `generate-json-schema` outputs without committing the full
generated protocol tree. The checked-in subset contains only methods, events,
server requests, fields, and terminal values used by `CodexRuntime`. An update
script generates the official non-experimental outputs into temporary storage
and proves that the reviewed subset still matches them.

The first supported CLI range is `>=0.153.0 <0.154.0`. The operator supplies
the CLI; Mohist does not install or upgrade it. Readiness validates the version,
initialization response, managed `codexHome`, authentication, and Model catalog.
Widening the range requires the compatibility smoke proof and a reviewed
contract update; an operator upgrade cannot silently accept a new protocol.

Use stable v2 methods only. Experimental APIs, including history surfaces used
to enumerate or recover Turns, are not execution dependencies. A Codex upgrade
must prove initialization, model discovery, Thread start/resume, Turn start and
completion, event routing, steering, interruption confirmation, compaction,
approval rejection, process loss, and no-replay behavior against fakes and the
locked compatibility smoke test.

## Rejected Alternatives

- **`codex-acp` or generic ACP:** adds translation and loses app-server-specific
  completion, approval, item, and compatibility semantics without a current
  second consumer.
- **One app-server per AgentJob:** repeats startup and authentication cost and
  makes Thread continuity depend on short-lived processes.
- **Expose Thread or Turn as product concepts:** duplicates AgentSession and
  AgentTurn identity and leaks provider lifecycle into the domain.
- **Adopt a completed old Turn after restart:** lets a new Runner generation
  assert work it did not own and can duplicate external effects.
- **Replay from Thread history:** conversation history is not an execution
  ledger or an idempotency proof.
- **Interactive Codex approvals in v1:** creates a second approval product with
  no Web, CLI, Slack, timeout, or ownership contract.
- **Codex-native subagents, Apps, connectors, realtime, and review Turns in
  v1:** none is required for the AgentJob and AgentSession critical path, and
  each adds a separate capability and product contract.
- **Persist Codex Turn ID in the domain:** the ID is useful only while one live
  Runtime generation routes events and Stop; cross-generation adoption is
  forbidden.

## Implementation Gaps

- `CodexRuntime`, app-server process supervision, and generated protocol types
  do not exist.
- Runner registration and Server Agent configuration accept only the currently
  implemented Runtime set.
- AgentJob dispatch, Session command routing, transcript projection, and
  compatibility tests have no Codex branch.
- Managed Codex setup and readiness diagnostics are not exposed by CLI or Web.

Official protocol reference: [Codex App Server](https://developers.openai.com/codex/app-server/).
