# OpenCode Runtime

## Design Drivers and Decision

OpenCode is an external execution system with its own Server lifecycle, event
transport, directory-scoped resources, Sessions, permissions, and retry policy.
Mohist needs its capabilities without making those provider concepts part of the
Workflow, AgentJob, or AgentSession contracts. The shared ownership model remains
authoritative in [`agent-execution.md`](../agent-execution.md).

Four pressures shape this adapter:

- **Completion must have one authority.** Events improve observability but can be
  delayed, duplicated, or disconnected. The completed Prompt response, not an idle
  event, decides whether execution ended.
- **External effects can be unknown.** A transport failure after submission does not
  prove that OpenCode rejected the Prompt. Mohist must not replay uncertain work.
- **The Server is shared.** Process health, the global event stream, and
  directory-scoped Instances outlive one Action call and need Runner-level
  governance.
- **SDK and CLI versions can drift independently.** Provider compatibility belongs
  behind one deep module and must fail as an actionable readiness or execution
  error, not leak generated SDK types to callers.

### Options and rationale

An ACP adapter would provide a common protocol surface, but it adds another
stateful integration layer and obscures OpenCode's native completion, permission,
and recovery semantics. A generic cross-Runtime `AgentRuntime` interface would
similarly encode a least-common-denominator lifecycle that neither OpenCode nor Pi
actually has. Both options move provider uncertainty into callers.

Mohist therefore removes ACP with no fallback and implements `mohist/opencode`
directly through an independent `OpenCodeRuntime` deep module. Workflow Actions,
AgentJob execution, and Session commands are the stable boundaries. The module
does not define a `mohist/agent` Action or redesign the Agent product.

Pi uses a separate in-process SDK and file-backed binding, while OpenCode uses a
shared local Server, a reconnectable event stream, and directory Instances. Those
differences are why the two modules intentionally share no SDK-shaped interface.

## Action Input and Output Contract

```ts
type OpenCodeActionInput = {
  prompt: PromptSpec
  session?: string
  options?: {
    model?: string
    variant?: string
  }
}

type OpenCodeActionOutput = null | {
  promise: string
}
```

`prompt` is rendered upstream to non-empty text under the authority of
[`task-dispatch.md`](../workflow/task-dispatch.md). The Action and Runtime do
not read Workflow variables or expand templates independently.

`model` uses OpenCode's `providerID/modelID` form and splits only at the first
`/`, because the model ID may contain additional slashes. `variant` remains a
separate execution parameter and may be supplied with or without `model`.
Changing either selection does not rotate a physical Session; omission preserves
the Session selection or OpenCode default.

Selecting `uses: mohist/opencode` already selects the Runtime, so the input has no
`agent`, `kind`, or `type`. OpenCode remains authoritative for native Agent, tool,
plugin, permission, and automatic-compaction configuration. Unknown `options`
keys are ignored with a diagnostic so persisted Agent options can converge without
turning unrelated legacy keys into execution failure. Present `model` and
`variant` values must be strings.

Action output deliberately excludes Runtime identity, transcript, model, usage,
diagnostics, and expectation detail. The work owner evaluates `expect` against
normalized final assistant text and synthesizes `{ promise }` only for a matched
promise; the Runtime itself produces execution facts, not that Workflow output.

## Capability Boundary

```text diagram
Workflow Action ----+
AgentJob executor ---+--> OpenCodeRuntime --> official SDK --> shared Server
Session commands ----+                                      |
                                                            +--> directory Instances
                                                            +--> physical Sessions
```

`OpenCodeRuntime` owns Server and Client lifecycle, readiness, physical Session
creation and resolution, Prompt execution, Follow-up, interruption, native
compaction, event and snapshot reconciliation, permission replies, provider-error
classification, and directory Instance release. Callers ask for those Mohist
capabilities and receive Mohist request/result types; generated SDK DTOs, call
ordering, reconnection, and provider diagnostics do not cross the boundary.

The module receives fully assembled execution input and a resolved Session binding.
It receives no Mohist Agent identity and never resolves an Agent definition.
Workflow expectation evaluation, artifacts, work results, and Session ownership
remain with their existing authorities.

The locked OpenCode dependency exposes a mature Session surface and a newer V2
surface whose generated methods are not all operational. Mohist uses the
smoke-verified mature capabilities for Session creation, completion-bearing Prompt
execution, asynchronous input, interruption, compaction, reads, events,
permissions, and Instance disposal. That namespace choice is private to this
module; the criteria for replacing it are defined under Upgrade Boundary.

## Process Topology and Readiness

Each Runner process owns one OpenCode Server and one Client, shared by all
OpenCode Sessions. Within that process, the OpenCode Server caches multiple
Instances by resolved directory. An Instance holds configuration, plugins,
LSP, MCP, and other runtime resources for that directory. It is not a Runner
Git Workspace, AgentSession, or physical Session.

Use the official embedded Server and Client factories rather than introducing a
second child-process protocol. Every provider request carries its resolved working
directory and enters one typed error-normalization boundary. A process per Action
would break Session reuse and duplicate readiness ownership, so the shared Server
is the only supported topology.

Mohist treats a physical Session's directory as immutable. A changed working
directory creates a new physical Session rather than moving the existing one.

Before the Runner registers or claims work, it must:

1. start the shared OpenCode Server;
2. pass the OpenCode health check;
3. establish the global event subscription.

After the OpenCode Server exits, the Runner stops claiming new work and rebuilds
the Server, Client, and global event subscription. Affected execution fails
directly and is never replayed automatically. Once the replacement Server is
healthy and the event subscription exists, the Runner is ready again without
waiting for model discovery.

Mohist locks the SDK package version; the operator supplies the OpenCode CLI.
Mohist does not install or upgrade the CLI or require it to match the SDK
exactly. Server/SDK incompatibility must produce an actionable readiness error.
CLI model-discovery incompatibility records diagnostics under best-effort
semantics. Native Workspace configuration and plugins load normally; do not use
`--pure` or remove the `.opencode` lockfile. If plugin-held resources keep the
CLI alive past its deadline but stdout already contains a parseable, non-empty
catalog, mark the snapshot incomplete with a diagnostic. Do not present it as a
normal completed discovery.

## Directory Instance Reclamation

The shared OpenCode Server retains configuration, plugin, LSP, MCP, and other
resources per resolved directory. A terminal WorkflowRun makes a directory
eligible for reclamation, but it does not prove that OpenCode is idle: stopped
work, an accepted Follow-up, or an uncertain Runtime effect may still be active.
Reclamation is therefore Runtime resource governance, not a Workflow transition
or permission to delete a Workspace.

A Server generation lasts from one healthy Server start until exit or shutdown.
The Runtime tracks only directories actually used in that generation. Periodic
maintenance considers tracked Workflow Workspaces whose registry state is
`eligible` or `stuck`; it excludes active or unregistered Workspaces, AgentJob
directories, and the Runtime startup directory. Server replacement clears the
old in-memory set because both the Instances and their owning process are gone.
This bounded set avoids scanning history and avoids blind provider calls that
could create an Instance in the name of cleanup.

Reclamation and ordinary Runtime operations share one exclusive boundary per
directory:

```text diagram
local operation admitted?                 yes -> defer
        | no
        v
status missing, malformed, busy, retry,
or unknown?                               yes -> defer
        | no; empty or all idle
        v
dispose confirmed exactly true?           no  -> retain and retry later
        | yes
        v
forget directory for this Server generation
```

The boundary remains held through confirmed disposal, so a Prompt, Follow-up,
Stop, Compact, Reset, or Session query cannot race it. A later real request
records the directory again. Disposal releases process resources only: it does
not delete the physical Session, AgentSession, binding, transcript, or disk
Workspace, and it does not synthesize idle or closed Session state.

Workspace deletion is a stronger operation and reacquires the same directory
fence. It rechecks local operations and provider status, performs disposal when
required, and keeps the fence until disk deletion and registry removal settle.
An untracked directory uses a temporary fence but does not probe or dispose.
Busy, unknown, or failed disposal prevents deletion. Results from an old Server
generation cannot authorize a callback that has not started; once deletion has
started, generation reset cannot release waiting operations early.

A failed directory is isolated: retain it for a later bounded pass, change no
Workflow, TaskRun, or AgentSession result, and do not call global disposal or
interrupt unrelated directories. Passes are single-flight and emit aggregate
counts rather than unbounded per-directory logs. Per-directory disposal makes no
promise about immediate process RSS reduction; process recycling is a separate
policy and must not be simulated with global disposal.

## Session Binding

See [`agent-execution.md`](../agent-execution.md) for AgentSession ownership and
origin and [`conventions.md`](../conventions.md) for Runtime identity field
names. `OpenCodeRuntime` receives a resolved logical Session target and cannot
create or change its origin. The shared rules for logical target resolution,
binding creation ordering, reuse invariants, and missing recovery are
authoritative in
[`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery).
Those rules create the physical Session first, submit the first Prompt only
after idempotent binding persistence succeeds, resolve to the current binding
across tasks, retries, and Runner restarts, reject working-directory changes
before Prompt submission, and arbitrate expected binding replacement. This
section defines only OpenCode-specific behavior.

Before submitting a new independent input, only the `OpenCodeRuntime` on the
current binding's `runnerId` may verify the persisted binding through
`client.session.get()`. A request on another Runner must route back to the bound
Runner or fail explicitly; a local 404 there is not evidence that the binding
is missing. Only a structured Session-not-found / HTTP 404 from OpenCode on the
bound Runner produces a `definitely-missing` fact. Network failure, timeout,
authentication or permission failure, 5xx, and a successful response without
the expected ID are not missing. An absent ID is evidence of SDK/Server
incompatibility and must fail rather than create a replacement.

After `definitely-missing`, call `client.session.create()` in the same
directory. Creation uses the model resolved for this input. Without an explicit
model, use the OpenCode default; variant is still applied to the Prompt. If the
new Session is immediately missing, creation fails, or a concurrent operation
changes the binding, fail this execution and do not attempt a second create.

Model and variant are execution parameters. They do not enter the Session cache
key, gate `resumeSession`, or trigger binding replacement. Reuse applies the
current model / variant on the existing physical Session before executing the
Prompt.

A worktree-cleanup Follow-up is subsequent execution of the original task. The
executor invokes the original resolved Action again and preserves WorkflowRun,
Session name, Work ID, and working directory so it reaches the same Runtime and
physical Session. It must not hard-code cleanup to another Action or ACP
fallback. Cleanup is not Reset and does not replace a binding for housekeeping.

At most one work-originated Prompt may run in a logical AgentSession at once,
whether TaskRun or AgentJob owns the work. Different logical Sessions may run
concurrently. A user Follow-up is a Session command and may be accepted while
work is executing.

## Execution and Completion Authority

The safety order is identity before effect:

```text diagram
resolve current binding
        |
        +-- present ----------+
        +-- definitely missing -> create candidate -> binding CAS
        +-- unknown/incompatible --------------------> stop
                              |
                              v
                    persist Input identity
                              |
                              v
                         submit Prompt
```

A new physical Session is created before its binding is persisted, and the first
Prompt is submitted only after the complete binding and Input identity are
durable. A stale binding, failed replacement, or uncertain existence stops before
submission. This ordering matters because a provider Prompt has no generally
available replay identity.

The completion-bearing Prompt response is the sole execution-completion
authority. An SSE `idle` event, silence, or a disconnected event stream cannot
declare success or failure. Events project transcript and activity quickly;
snapshots reconcile missing observations after reconnect and before final
presentation. Workflow and AgentJob owners independently interpret the normalized
completion fact and remain authoritative for their own result.

The work owner supplies the execution deadline; one Prompt defaults to 60 minutes
when no explicit value is supplied. The executor AbortSignal is the single
deadline authority. The local HTTP transport must not impose a shorter timeout.
If transport fails after submission may have started, the Runtime requests
interruption and verifies stop before reporting failure, but it never resubmits the
Prompt. Startup and readiness may retry because they precede work effects;
uncertain Prompt acceptance may not.

A process crash can still permit duplicate execution if the work owner redelivers
inside the accepted crash window. That limitation is explicit; it does not justify
inventing a provider Prompt ID or reconstructing replay from transcript state.

## Prompt Deadline and Two-Phase Closeout

The executor declares the deadline. `OpenCodeRuntime` applies a two-phase
closeout protocol to every Prompt with a deadline. The clock scope is one Prompt
execution, not a TaskRun or Stage.

1. Five minutes before the deadline, call `client.session.promptAsync()` on the
   current physical Session to inject one closeout warning, then return
   immediately without awaiting it. If the entire deadline is under five
   minutes, inject the warning when execution begins.
2. At the deadline, the Runner immediately fixes the result as
   `deadline-exceeded`, then calls `client.session.abort()` for closeout. Abort
   and status verification add diagnostics only; they cannot change the timeout
   result. A late Prompt response cannot reverse it.

The warning is task-independent, with implementation-owned wording equivalent
to: interruption will occur in about five minutes; stop new work immediately,
commit current changes, leave a record in the task's progress channel, and end.
It names no marker or file. Task-specific Prompt contracts define `unfinished`,
progress.txt, or other closeout artifacts; the warning does not repeat them.

The injected message enters the Session message stream as a user Follow-up and
is picked up at the current execution's next iteration boundary, after the
current model call and its tool calls. This is the same path as a user Follow-up
under Session Commands / Follow-up. A long-running tool call can delay receipt;
the deadline still aborts, so the worst case degrades to termination without a
warning. Both warning and interruption project into the transcript and remain
visible in the UI.

Warn once per Prompt execution. If the Agent ends normally after the warning,
do not abort. Evaluate its result under the task's own completion contract; for
example, an `unfinished` report fails under existing retry semantics, while the
workspace remains committed and recorded.

Do not:

- expose the deadline value in the Prompt. An Agent has no reliable clock; a
  static number is not actionable. Deliver the actionable "termination is
  imminent" signal when needed;
- commit or roll back residual work automatically after termination; existing
  workspace handling remains authoritative;
- replace, clear, or rebuild the Runtime Session binding after termination.
  Only explicit user Reset intentionally replaces it. Later independent input
  still prepares the binding under missing-recovery rules;
- create a separate execution-category concept for housekeeping Prompts such as
  a worktree-cleanup Follow-up. The warning is compatible with instructions to
  commit or restore and applies uniformly.

## Events and State Reconciliation

The shared activity and transcript contract is authoritative in
[`agent-execution.md`](../agent-execution.md#activity-and-transcript). This
section defines only how OpenCode signals become those canonical facts.

The Runner maintains one `client.global.event()` subscription for the shared
OpenCode Server. `OpenCodeRuntime` routes events by Session ID and directory.
Known typed events normalize into stable Mohist transcript, tool, usage, model,
status, and compaction facts. Unknown OpenCode events enter diagnostics only and
do not change Workflow or Session state.

Real-time events reduce display latency but are not a persistent execution
protocol:

- use OpenCode message ID and part ID for idempotent projection;
- if the event stream disconnects while subscribers remain, re-establish one
  global event stream;
- after a new stream connects, each current execution reads
  `session.status()` for its own Session ID and directory and reconciles with
  relevant `session.get/messages()` snapshots;
- one execution consumes only retry facts for its Session ID; another Session's
  events cannot affect its provider-error decision;
- after Prompt completion, reconcile messages when events are missing or the
  final user-visible transcript needs confirmation.

Mohist does not persist a V2 history cursor, aggregate sequence, or event replay
state. The Workflow task executor applies Mohist expectation, artifact,
`failIf`, and recovery semantics after the Action result. The AgentJob executor
decides AgentJob completion independently.

## Provider Error Failure Policy

A provider error fails execution only when it is classified unrecoverable.
OpenCode retries recoverable errors such as transient 429, 5xx, or network
failure; Mohist does not fail early. Failure signals come from
`session.status` events with `type:"retry"` carrying `attempt`, `message`,
`action`, and `next`, from status snapshots after reconnection, and from final
Prompt rejection. Do not scan logs. Two unrecoverable decisions both normalize
to aborting and failing the current execution:

OpenCode's `session.error` event uses the SDK error DTO shape
`{ name, data: { message, ... } }`. The Runner uses `data.message` as the
primary failure reason, retaining the legacy top-level `message` and string
forms only as compatibility fallbacks. A cleanup or stop-confirmation failure
is an additional diagnostic and must not replace a provider message already
reported by this event.

- Intrinsically unrecoverable: prefer structured `action.reason` from retry
  status. Without a usable classification, match `message` against quota,
  credit, billing, usage limit, allowance, balance, and limit-reset patterns.
  The default set covers common provider wording in English and Chinese, and
  Runner configuration may add patterns. A plain rate-limit / too-many-requests
  message does not fail on first sight through this fallback.
- Unrecoverable by evidence: if recoverable errors continue until `attempt`
  reaches threshold N, five by default and Runner-configurable, while execution
  remains incomplete, reclassify them as unrecoverable and abort.

If a recoverable error clears within N attempts and execution completes,
continue without failure. Errors OpenCode already classifies as unrecoverable,
including auth, invalid request, context overflow, and content policy, reject
the Prompt directly and need no additional Mohist rule. A silent hang with no
retry event remains covered by the executor deadline.

Use the retry event's `attempt` field directly. OpenCode resets it per Prompt
execution. After Runner restart or event-stream reconnection, restore from a
`session.status()` snapshot instead of maintaining a second counter. On a
classification or threshold match, use the locked SDK's typed call:
`client.session.abort({ sessionID, directory }, { throwOnError: true })`.
Stopping is confirmed only when abort returns exactly `data: true` and the same
directory's status snapshot either omits the Session or marks it idle. Then
return a failure fact containing the original provider message. Keep the
AgentSession and physical Session binding unchanged and do not suggest Reset.

If abort throws, does not confirm success, or status remains busy/retry, return
an `abort-unconfirmed` diagnostic without claiming execution stopped. For a
Runner deadline, attach that diagnostic to `deadline-exceeded` rather than
overriding the timeout result. OpenCode is a third-party dependency; Mohist does
not modify its retry implementation. Until structured classification is
complete, retain both the message fallback and Mohist retry ceiling.

## Session Command Semantics

Session commands are external effects routed by the persisted binding; Runner
memory is only a cache. A rejection before the Runtime can acquire the command
returns `notStarted`, so Server may end that reservation. Once the provider call
may have started, timeout, connection loss, or an unconfirmed reply returns
`unavailable`; Server retains the original operation identity and must not replay
or replace it with a new reservation.

| Command | OpenCode-specific contract |
|---|---|
| Follow-up | Uses native asynchronous input. Active execution receives it at an iteration boundary; idle execution starts after binding preparation. Acceptance returns immediately, and completion remains event-driven. Active or unknown state never triggers binding replacement. |
| Compact | Allowed only while idle. Uses native compaction with the current model, keeps the physical Session and logical Session identity, and fails actionably when no current model is known. |
| Reset | Allowed only while idle. Creates an empty physical Session in the same directory, then replaces the complete expected binding. A definitely missing old Session may skip model inheritance; every other read failure remains explicit. |
| Stop | Requests interruption of the current execution. Success requires the same stop-confirmation rule used by deadline and provider-error handling; request acceptance alone does not prove execution stopped. |

Every command carries the complete expected binding. A replacement is applied only
if that tuple is still current, so a stale Reset cannot overwrite newer state.
Compact and Reset preserve the AgentSession ID and transcript; only Reset replaces
the physical binding and starts empty provider context.

## Permissions and Errors

OpenCode native permission configuration is authoritative. Allowed operations
execute directly and explicit denials remain denied. `ask` delegates one
operation's choice to the caller. For a `permission.asked` belonging to the
current headless execution, `OpenCodeRuntime` replies with
`client.permission.reply({ requestID, directory, reply: "once" })`.

The reply affects only that permission request. It does not write OpenCode
configuration or Session permission rules and does not create a Workflow
Approval. Route the event by current physical Session ID; when the event carries
a directory, it must also match current workDir. Reply at most once to a request
ID in one execution.

If reply throws or does not confirm success, immediately abort current execution
and, after confirming it stopped, return `permission-required`. Do not leave the
request blocked until the executor deadline and present it as `interrupted`.
OpenCode owns per-tool timeout and retry; Mohist owns only the execution deadline
and abort confirmation.

At the `OpenCodeRuntime` boundary, normalize SDK errors to the small set of
Mohist results: `invalid-input`, `unavailable-runtime`, `missing-session`,
`incompatible-runtime`, `permission-required`, `deadline-exceeded`,
`interrupted`, and `turn-failed`. Provider-specific details remain
diagnostics and never become Action output fields. Do not create a global
Workflow error enum; each caller reports failure through its own TaskRun or
AgentJob contract.

Map known local transport codes, such as header/body timeout, to stable,
actionable failure text. Retain complete SDK/provider payloads only in
diagnostics to keep unreviewed external content out of TaskRun.

## Model Catalog Boundary

The model catalog assists configuration; it is not execution or readiness
authority. `RunnerHost`, not `OpenCodeRuntime`, discovers it from the
operator-provided CLI using a bounded, shell-free process. OpenCode still validates
the selected model and variant when execution starts.

A complete non-empty result replaces the snapshot. A parseable non-empty result
from a timed-out process is marked incomplete and may add models or variants but
cannot delete previously known values. Empty or failed refreshes retain the last
non-empty snapshot. Only a changed snapshot prompts an immediate registration
heartbeat. This best-effort path never makes an otherwise healthy Runtime
unready.

## Verification Boundary

Default verification replaces the Server, Client, event stream, model-discovery
process, Workspace registry, and clock with deterministic fakes. It proves
readiness gating, binding-before-effect, missing versus unknown classification,
no replay after uncertain submission, completion authority, two-phase closeout,
permission handling, event reconciliation, and directory fencing. A real smoke
test is upgrade evidence for the locked external surface; it is not a substitute
for these state and failure assertions.

## Legacy Boundary

ACP is removed as an execution path, dependency, wire term, and fallback. Built-in
Workflows select `mohist/opencode`, and AgentJob execution calls the same deep
Runtime capability without going through the public Workflow Action contract.
There is no compatibility alias, feature flag, synthetic ACP compaction, liveness
probe, log-scanning fallback, or lockfile cleanup.

Existing durable data is not rewritten into a physical Session history. An ACP-era
current binding is treated as unavailable to the new Runtime: a later independent
input may establish a new OpenCode binding through the canonical missing-recovery
contract, while commands that require the old provider fail explicitly and Reset
may intentionally create a new binding. A persisted Workflow that still names the
removed Action fails actionably; an already running WorkflowRun is not silently
migrated to different execution semantics.

## Upgrade Boundary

Mohist locks `@opencode-ai/sdk` at 1.18.3, while the operator supplies the
OpenCode CLI. Exact version equality is not required, but health, Session
completion, event subscription, interruption, compaction, and Instance disposal
must remain compatible. Incompatibility is an actionable readiness or execution
failure; Mohist never installs or upgrades the CLI.

The SDK currently exposes both mature Session capabilities and a newer V2 surface.
Real smoke evidence showed the mature path working while V2 wait and compact were
unavailable, so generated method presence is not sufficient evidence for an
upgrade. Moving surfaces requires a locked-version smoke that proves every used
capability and preserves completion, missing/unknown, abort-confirmation, and
no-replay semantics. The change remains inside `OpenCodeRuntime` and cannot alter
the Workflow Action or AgentSession contracts.

The reference smoke record is
[`sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json).
