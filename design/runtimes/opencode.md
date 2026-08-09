# OpenCode Runtime

## Decision

`mohist/opencode` is a Runtime-specific Action implemented directly with
`@opencode-ai/sdk/v2`. See
[`agent-execution.md`](../agent-execution.md) for the Agent / Session ownership
model and its invariants, including Inline Agents, work ownership, and the rule
that a shared Runtime creates no dependency.

Remove the ACP adapter directly, with no fallback. Existing AgentJob execution
must also leave ACP. An Agent-owned executor uses the same `OpenCodeRuntime`
capability instead of depending on the Workflow Action contract. This design
does not define a `mohist/agent` Action and does not redesign the Mohist Agent
product.

OpenCode and Pi each implement an independent deep Runtime module. Do not add a
generic `AgentRuntime` interface. The stable boundaries are the Workflow Action
contract, AgentJob execution contract, and Session commands, not a speculative
cross-Runtime SDK wrapper.

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

Before invoking the Action, the Runner execution entry point renders `prompt`
to a non-empty string and passes it to OpenCode Runtime. This section defines
only the Action input shape and Runtime behavior; rendering authority belongs to
[`task-dispatch.md`](../workflow/task-dispatch.md). `model` uses OpenCode's
`providerID/modelID` form. An OpenCode model ID may itself contain `/`, so the
Runtime splits only at the first `/`. `variant` always remains a separate field
and must not be joined to the model ID.

There is no OpenCode `agent` input and no `kind` or `type`. Selecting
`uses: mohist/opencode` already chooses the Runtime. OpenCode's native
configuration remains authoritative for its default Agent, tools, plugins,
permissions, and automatic compaction policy.

Keys in `options` other than `model` and `variant` are ignored with a diagnostic
and do not fail execution. This lets persisted `vars.agent` values containing
legacy keys such as `type` or liveness settings remain bindable until their
write paths converge. If present, `model` and `variant` must be strings;
otherwise return invalid input.

The Action does not read Workflow variables. Template evaluation timing is
defined once by [`task-dispatch.md`](../workflow/task-dispatch.md): Server
dispatch no longer expands `with` or `expect`. The Runner execution entry point
renders raw `with` against the attempt snapshot before manifest validation and
Action input. This section describes only the input accepted by the OpenCode
Action and Runtime.

`variant` may accompany `model` or appear alone. Without `model`, OpenCode
applies it to the current or default model.

When creating a physical Session, pass an explicit model to Session creation
and to the first Prompt. When reusing a physical Session, each Prompt carries
the model and variant selected for that execution. The mature Session API
updates the Session selection while creating the user message, so no separate
switch call is needed. Omitting options preserves the current Session selection;
if there is no prior selection, OpenCode uses its defaults. Changing model or
variant does not rotate the physical Session.

The Runner Workflow task executor renders `with` against the attempt snapshot
and passes the manifest-validated result to the Action as
`OpenCodeActionInput`. The executor applies `expect` and artifact declarations
independently after the Action returns. Neither Action nor Runtime reads
Workflow Variables or the complete dispatch context. Only a matched promise is
exposed as Action output. The task executor synthesizes `{ promise }` from the
Workflow-owned `expect`; neither Action nor Runtime produces that field.
Runtime identity, transcript, model, usage, diagnostics, and expectation detail
remain in existing state and read models rather than Action output.

The normalized execution facts include final assistant text. The task executor
uses it to evaluate an expect marker at `path: _output`. This text is carried by
the Action result's execution facts, not Action output.

## SDK Surface

OpenCode 1.17.18 exports both the mature compatibility surface
`client.session.*` and the newer protocol surface `client.v2.*`. OpenCode's own
Web UI and TUI still use `client.session.*` for key Session operations. Although
generated `client.v2.session.wait()` and `client.v2.session.compact()` methods
exist, the current Server reports `operation unavailable` for them.

Mohist therefore uses:

| Capability | SDK operation |
|---|---|
| create a Session | `client.session.create()` |
| execute and await a Workflow / AgentJob Prompt | `client.session.prompt()` |
| submit a user Follow-up and return immediately | `client.session.promptAsync()` |
| interrupt execution | `client.session.abort()` |
| compact context | `client.session.summarize()` |
| read Session state | `client.session.get()`, `client.session.messages()`, `client.session.status()` |
| receive real-time events | `client.global.event()` |
| answer a one-time permission request | `client.permission.reply()` |
| release a directory Instance | `client.instance.dispose()` |

The dependency remains `@opencode-ai/sdk/v2`. Choosing the mature Session
namespace is an implementation decision hidden inside `OpenCodeRuntime`, not a
second product contract. Until the new V2 Session execution surface can replace
this table, Mohist does not call
`client.v2.session.prompt/wait/compact/interrupt`.

## Deep Module Boundary

`OpenCodeRuntime` is a deep module inside the Runner. It owns:

- OpenCode Server and Client lifecycles;
- readiness;
- directory Instance usage tracking, idleness decisions, and release;
- physical Session creation, lookup, reuse, and interruption;
- Prompt execution, Follow-up, Compact, and Reset;
- event subscription, message snapshot reconciliation, and event
  normalization;
- OpenCode errors and compatibility diagnostics.

The `mohist/opencode` Action, AgentJob execution adapter, and Session command
handler depend only on Mohist request/result types, never generated SDK types.
The Runtime receives fully assembled execution input and a Session binding. It
does not receive a Mohist Agent ID or name and does not load a Mohist Agent
definition. Model-string parsing, SDK DTO construction, call ordering,
reconnection, and OpenCode error interpretation all stay in this module.

This is not a method-by-method SDK wrapper. Callers request Mohist capabilities
such as execute Prompt, Follow-up, Compact, and Reset; the module decides which
SDK operations and state-reconciliation steps implement each capability.

## Process Topology and Readiness

Each Runner process owns one OpenCode Server and one Client, shared by all
OpenCode Sessions. Within that process, the OpenCode Server caches multiple
Instances by resolved directory. An Instance holds configuration, plugins,
LSP, MCP, and other runtime resources for that directory. It is not a Runner
Git Workspace, AgentSession, or physical Session.

Use the official `createOpencodeServer()` and `createOpencodeClient()` APIs.
Do not spawn or parse an OpenCode process directly. Pass the working directory
explicitly on every Session SDK call and enable `throwOnError` so all failures
enter one normalization boundary. Do not create a process per Action.

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

A WorkflowRun terminal state does not make the shared Server release its
directory Instance automatically. The Runner therefore reclaims OpenCode
Instances for terminated WorkflowRuns during existing periodic Workspace
maintenance. This is execution-plane resource governance, not a Workflow state
transition, and it does not delete the disk Workspace.

### Candidates and Cost

An OpenCode Server generation is the lifetime from one successful shared Server
start until its exit or shutdown. `OpenCodeRuntime` tracks every resolved
directory it actually accessed in the current generation. Any SDK operation
with a directory records it before entering OpenCode. A successful
`client.instance.dispose({ directory })` removes it from that generation's used
set; a later Runtime request records it again. Server exit, shutdown, or
completed rebuild clears all records for the old generation because those
Instances no longer exist. This usage set belongs only to Runner process memory,
not `WorkspaceRegistry`. Restarting the Runner loses both the old Server process
and the matching records, so nothing must be restored.

Periodic maintenance iterates only directories used but not successfully
released in the current generation. It resolves each path through
[`WorkspaceRegistry`](../runner.md#local-workspace-lifecycle). Only Workflow
Workspaces in `eligible` or `stuck` phase are candidates. Do not reclaim
`active` Workspaces, unregistered directories, ordinary AgentJob directories,
or the Runtime startup directory.

Do not scan all historical WorkflowRuns or every `eligible` registry entry on
each pass, and do not call dispose for a directory unused in this generation.
OpenCode creates an Instance by directory; blind probing or repeated disposal
could create a resource in the name of cleanup. After successful reclamation,
the directory has no periodic cost until a real new request records it again.

### Idleness and Concurrency

A WorkflowRun terminal state grants reclamation eligibility but does not prove
OpenCode is idle. `Stopped` does not imply that the Runner interrupted old work,
and a successfully accepted asynchronous Follow-up may still be running inside
OpenCode.

The Runtime serializes SDK operation admission and Instance disposal for one
directory. Reclamation holds that directory's exclusive boundary while it does:

```text
if directory has an admitted local operation:
  defer

statuses = client.session.status({ directory })
if statuses is missing, malformed, or contains busy / retry / unknown:
  defer

disposed = client.instance.dispose({ directory })
if disposed is not exactly true:
  defer

forget directory for this Server generation
```

Only an empty status map or a map containing exclusively `idle` allows disposal.
Keep the exclusive boundary until the dispose response is confirmed. New
Prompt, Follow-up, Cancel, Compact, Reset, or Session query operations enter
only after it ends. A new request records the directory again, so disposal does
not prevent future use permanently.

### Session and Deletion Boundary

Instance disposal releases only process memory for that directory. It does not
delete the OpenCode Session, AgentSession, current binding, transcript, or disk
Workspace, and does not change Session activity to `idle` or closed. A later
request uses the persisted binding. After OpenCode recreates the directory
Instance, existing Session resolution and missing-recovery rules continue.

One Runner Workspace-maintenance pass attempts Instance reclamation before disk
retention and budget policy. Its result promptly releases memory and excludes a
directory that was busy or failed at that moment; it does not authorize a later
deletion. A new Runtime request may record the directory again after the pass.

Every automatic or manual Workspace deletion must reacquire the directory's
removal fence. The fence shares the directory-admission boundary with ordinary
Runtime operations. It rechecks local operations, reads status, and disposes if
required, then remains held until the disk-deletion and registry-removal callback
finishes. New operations wait while the fence is held and cannot recreate an
Instance between disposal and deletion.

If the current generation has no usage record, the removal fence still creates
a temporary exclusive entry and invokes the deletion callback directly. It
must not call status or dispose, because blind confirmation could create an
Instance. If the directory is busy, status is unknown, or disposal fails when
the fence begins, do not call the callback; defer or fail deletion explicitly.
If the Runtime generation changes before the callback starts, old results
cannot authorize deletion. Once the callback starts, its removal fence survives
until callback settlement and generation reset cannot release waiting
operations early.

### Failure and Scope

If status read or dispose fails, retain the usage record for a later periodic
retry. Failure to reclaim one directory changes no WorkflowRun, TaskRun, or
AgentSession result, does not call `/global/dispose`, and does not interrupt
other directories. A transport failure still triggers Runtime rebuild under
the existing shared-Server rule; when that old generation ends, its Instances
and usage records disappear together.

The reclamation pass is single-flight; a still-running pass prevents the next
from starting. Log bounded candidate, busy, failed, and disposed counts plus
aggregate diagnostics, so one persistently failing directory cannot produce
unbounded logs.

This design does not promise that process RSS returns to the operating system
immediately after `instance.dispose`. It adds no TTL derived from per-directory
idle time, mtime, or Workflow history. If the shared Server continues growing
after per-directory disposal, process-level idle recycling is a separate future
guard; `/global/dispose` must not impersonate it.

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

## Prompt Execution

A Prompt requested by a Workflow Action adapter or AgentJob executor runs in
this order:

1. Parse the optional model string and construct the SDK model DTO inside the
   Runtime.
2. With no binding, create a physical Session. With a binding, verify it through
   `client.session.get()`, then apply shared missing-recovery rules to select the
   original ID or a newly rebound ID.
3. Wait until the Session confirms that the current binding is persisted.
4. Record and persist this input using the confirmed Runtime Session ID.
5. Call and await `client.session.prompt()` with Session ID, directory, Prompt
   parts, optional model, and optional variant.
6. Project the returned assistant message and received events to AgentSession.
7. If final transcript confirmation is needed, reconcile through
   `client.session.messages()`.
8. Return a normalized completion fact.

`client.session.prompt()` is the request that carries the completion result;
there is no second `wait()`. `OpenCodeRuntime` does not evaluate Workflow
expectations or decide AgentJob success. After Action success, the Workflow task
executor applies `expect`, artifacts, `failIf`, Action output, and recovery
semantics. On Action failure, cancellation, or timeout, it preserves the
original failure and does not read files or markers. The Agent-owned contract
validates and reports AgentJob results independently.

SSE silence is not failure, and an `idle` event is not completion authority.
The completed Prompt response decides when execution ends. Workflow task and
AgentJob executors declare their own execution deadlines. Without an explicit
value, one Prompt defaults to 60 minutes; an explicit value overrides it.
Closeout and termination follow Prompt Deadline and Two-Phase Closeout. After
removing ACP liveness probes, `OpenCodeRuntime` performs no silence or idleness
detection. Executor deadline covers a hung execution, while a provider error may
fail sooner based on `session.status` retry facts under Provider Error Failure
Policy.

The HTTP client used by `prompt()` against the local OpenCode Server must not set
a header or body timeout shorter than the executor deadline. The executor
AbortSignal is the single per-execution deadline authority. This setting belongs
only to the OpenCode Client and must not change the global dispatcher for other
Runner HTTP calls. Any transport failure first requests abort and confirms that
the current physical Session stopped before reporting failure. Never replay a
Prompt whose submission state is uncertain.

Startup and readiness operations may retry during the Runner lifecycle. Prompt
submission and any response with uncertain acceptance must not retry blindly.
Keep existing in-process dispatch deduplication. Redelivery can duplicate
execution inside the crash window; that limitation is accepted and does not
justify a deterministic Prompt ID or replay reconstruction.

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

## Session Commands

A Session command is a request/response operation from Web or CLI through Server
to Runner. The persisted Runtime binding is a routing fact; the Runner memory
cache is only an optimization.

Command results distinguish "definitely did not start" from "may have started,
result unknown." If Server cannot find the target Runner connection, the Runner
has not acquired the Runtime connection, or the command is rejected before
entering the Runtime, return `notStarted`. Server may end that reservation and
allow a later request to create a new operation. Once a Runtime call may have
started, timeout, connection loss, or an unconfirmed Runtime reply returns
`unavailable`. Server must retain the original operation so later delivery uses
the same operation ID. It must not abandon the reservation and guess that no
side effect occurred.

### Follow-up

- Call `client.session.promptAsync()` on the current physical Session with the
  Prompt and any selected current model / variant.
- While AgentSession is idle, perform shared binding preparation before
  accepting input; if the physical Session is definitely missing, create and
  persist a replacement first. While activity is active or unknown, do not
  replace it because the Follow-up still targets current execution.
- Return immediately after the endpoint accepts the request; completion
  continues through Session events.
- While active, the Follow-up joins current OpenCode execution. While idle, it
  begins processing immediately.
- Return routing or admission failure to the user. Never retry or replay it
  automatically.

### Compact

Compact is allowed only while the logical Session is idle. Active work returns
conflict under the same concurrency boundary as Reset. Read the current model
from the OpenCode Session, then call
`client.session.summarize({ sessionID, providerID, modelID })`. Compact neither
creates a physical Session nor falls back to a Mohist synthetic summary. If the
Session has no current model, return an actionable error rather than guessing.
Resulting Session and message events continue to project into the transcript.

### Reset

Reset is allowed only while the logical Session is idle. Read the current model
and variant if present, then create an empty OpenCode Session in the same working
directory. Replace the logical Session's current binding only after creation
succeeds. AgentSession retains no old binding. Its existing transcript remains,
while the new physical Session has empty context.

Every command carries the full expected current binding. Server applies a
returned replacement only if that binding is still current, preventing a stale
Reset result from overwriting a newer binding. A structured missing response
while reading the old Session does not block Reset: skip model/variant
inheritance and create with OpenCode defaults. Other read failures remain
explicit.

Neither Compact nor Reset rotates the AgentSession ID. Command responses return
the same stable `sessionId`; only Reset replaces the Runtime binding. API shape
and CLI text must not claim that these commands return a new Session ID.

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
and, after confirming it stopped, return `permission required`. Do not leave the
request blocked until the executor deadline and present it as `interrupted`.
OpenCode owns per-tool timeout and retry; Mohist owns only the execution deadline
and abort confirmation.

At the `OpenCodeRuntime` boundary, normalize SDK errors to the small set of
Mohist results: `invalid input`, `unavailable runtime`, `missing Session`,
`incompatible runtime`, `permission required`, `deadline exceeded`,
`interrupted`, and `execution-failed`. Provider-specific details remain
diagnostics and never become Action output fields. Do not create a global
Workflow error enum; each caller reports failure through its own TaskRun or
AgentJob contract.

Map known local transport codes, such as header/body timeout, to stable,
actionable failure text. Retain complete SDK/provider payloads only in
diagnostics to keep unreviewed external content out of TaskRun.

## Model Catalog

The model catalog belongs to `RunnerHost`, not `OpenCodeRuntime`. Before first
registration, Host runs `opencode models --verbose` on a best-effort basis.
`runtime/opencode-models.ts` parses model names and provider-defined variant keys
once and writes them directly to Host `coderModels` and `coderModelVariants`.
Normal process exit produces a complete snapshot. Parseable non-empty stdout
remaining after timeout produces an incomplete snapshot. Failure or an empty
result reports empty fields at first registration; a non-empty incomplete
snapshot may serve as the initial best-effort catalog. Neither prevents a
healthy Runtime from claiming work.

The command boundary uses asynchronous buffered execution without a shell and
parses stdout once, only after process termination. This preserves trailing
output written before exit without blocking the Runner event loop. One discovery
has a three-second deadline.

After first registration and startup convergence, Host registers an independent
periodic-discovery timer. The default interval is 30 minutes with a 60-second
minimum, and the first trigger occurs one interval after timer registration.
Periodic discovery does not inspect Runtime readiness. Empty or failed results
retain the last non-empty snapshot. A complete non-empty result replaces it. An
incomplete non-empty result may merge new models and variants into the old
snapshot but cannot delete old members. Only an actual change to the merged
model or variant sets replaces both fields and attempts one immediate heartbeat.
Host disposes the timer when the run loop ends.

The catalog assists Server and Web configuration; it is not final execution
authority. Omitting model uses the current OpenCode selection or default.
OpenCode validates selected model and variant at execution. `OpenCodeRuntime`
does not load, store, or refresh the catalog and calls neither SDK model/provider
list APIs nor CLI discovery. Catalog state does not affect Runtime readiness.

## Tests

Default tests must not start real OpenCode or use real process, network,
filesystem configuration, or clock. Runtime tests inject fake generated Client
and Server factories. Host model-discovery and periodic Workspace-maintenance
tests inject fake discovery and Runtime implementations and use fake timers to
drive events, snapshots, completion, process loss, reclamation ticks, and errors
deterministically.

Coverage includes at least:

- Action input expansion with no hidden `vars.agent` fallback;
- model strings containing multiple `/` characters, with variant independent;
- complete CLI model-discovery stdout, variant keys, failure recovery, periodic
  cadence, and change heartbeat;
- shared Runtime code for Workflow and AgentJob execution without shared work or
  Session identity;
- physical Session reuse and rotation invariants;
- no rotation on model / variant change;
- one create after structured 404 from `session.get()`, with binding persistence
  and input both preceding Prompt;
- no create on timeout, 5xx, permission failure, or malformed successful
  response, and no Prompt through a stale binding;
- a non-bound Runner neither probes nor replaces a Session, and its local 404
  does not trigger create;
- no create or replay after missing or transport failure once Prompt starts;
- global event routing, duplicate suppression, and snapshot reconciliation;
- Prompt completion, interruption, uncertain admission, and no replay;
- asynchronous Follow-up including idle missing recovery, native summarize,
  Reset including a missing old Session, restart routing, and stale-binding
  rejection;
- one-time permission reply, duplicate suppression, reply failure, missing
  Session, compatibility failure, and process-loss failure;
- directory Instance reclamation only for directories used in the current
  Server generation whose WorkflowRun is `Completed` or `Stopped`; deferral for
  busy, retry, unknown, or concurrent operations; no repeated dispose after
  success; tracking after later reuse; and old-generation clearing on rebuild;
- Instance reclamation before disk policy in a periodic pass; each automatic or
  manual deletion performing required dispose, disk deletion, and registry
  removal under one directory removal fence; identity retained when release is
  unconfirmed; and an untracked directory using a temporary fence without
  status or dispose;
- periodic cost independent of unrelated historical WorkflowRuns or already
  released directories;
- minimal `{ promise }` Workflow Action output with existing expectation
  semantics;
- two-phase closeout: exactly one fire-and-forget warning before deadline,
  immediate warning for deadlines under five minutes, abort at deadline, and no
  abort after an early normal end; all driven by a fake clock.

## Complete Replacement

Implementation removes these paths directly instead of retaining deprecated
forms:

- `@agentclientprotocol/sdk`;
- `mohist/acp-agent` and the ACP Action tree;
- shared ACP connection / Session management;
- ACP liveness probes and their configuration;
- OpenCode log scanning;
- ACP private compaction metadata and synthetic Session rebinding;
- `.opencode` lockfile cleanup;
- every `acpSessionId` wire, Server, and Web term.

Built-in Workflows switch atomically to `mohist/opencode` with
`options: ${{ vars.agent }}`. Existing AgentJob dispatch removes the hard-coded
`mohist/acp-agent` Action name. After Agent launch assembles the Agent snapshot
and Prompt, it carries an Agent-owned OpenCode execution request, and the
executor calls `OpenCodeRuntime` directly. This introduces no `mohist/agent`,
feature flag, compatibility alias, or ACP fallback.

### Transition Behavior for Existing Data and Configuration

Do not rewrite existing data. Transition behavior must be explicit:

- Existing AgentSession data converges to the current-binding structure without
  copying or retaining physical Session history. After replacement, an ACP-era
  current binding is treated as a missing current Runtime Session. A new
  independent input establishes an OpenCode binding through missing recovery.
  Compact / Cancel fail explicitly; Reset may create a new binding directly.
- Do not silently ignore or rewrite old Workflow Profile structures. A task
  with `uses: mohist/acp-agent` fails at dispatch with an actionable error
  because the Action was removed. The Action contract handles old input keys
  such as `with.expect` and `with.agent`; definition validation does not inspect
  inside `with`.
- Do not migrate a WorkflowRun already started before the switch. A later Agent
  task dispatch fails actionably, and the user reruns the affected Stage.
- Narrow Issue-level `agentConfig` to model / variant, removing `type` and ACP
  liveness fields from API, CLI, and Web. The Action input ignore-plus-diagnostic
  rule covers legacy keys in persisted `vars.agent`.

## Upstream Boundary

The dependency used when deciding this design was `@opencode-ai/sdk/v2`
1.17.18, but its two namespaces had different maturity. OpenCode Web UI and TUI
used `client.session.*` for create, Prompt, abort, summarize, and Session
synchronization. The newer V2 Session execution core still reported `wait` and
`compact` unavailable and did not yet provide complete completion and recovery.

Mohist follows those real internal call paths instead of assuming every
generated V2 method is usable. SDK access remains inside `OpenCodeRuntime`.
Moving to the complete V2 Session execution surface later changes one deep
module without changing the Workflow Action or Session product contract.

Before implementation, lock the SDK package version and smoke-test the used
surface against real OpenCode. If it has drifted, revise the table before
implementing. T-001 smoke-tested Session and global-event calls against a real
OpenCode 1.18.3 Server; see
[`sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json).
The listed `client.session.*` and `client.global.event()` calls worked, while
`client.v2.session.wait()` and `client.v2.session.compact()` still returned
`ServiceUnavailableError`, confirming they do not enter execution.

On 2026-07-31, another smoke test used locked
`@opencode-ai/sdk/v2` 1.18.3 and OpenCode CLI 1.18.10 with the Runner's
OS-assigned loopback Server factory in a temporary directory. It verified that
`client.global.health()` returned healthy,
`client.session.status({ directory })` returned an empty status map, and
`client.instance.dispose({ directory })` returned `data: true`. After finally
closing Server and dispatcher, the temporary directory had no residue and the
existing Server on port 4096 was unaffected. The actual locked SDK version is
recorded under Implementation Gaps. This completes smoke evidence for
`client.instance.dispose()`.

## Implementation Gaps

Prompt Deadline and Two-Phase Closeout will be implemented in
`OpenCodeRuntime` by a separate Issue. Today the deadline terminates execution
directly and gives the Agent no closeout opportunity.

Missing recovery is not implemented. A missing result from current
`client.session.get()` ends execution directly. Workflow and AgentJob do not
yet share the preparation sequence
`create candidate -> replace expected binding -> record input`. OpenCode
`SessionCommand` dispatch currently returns `unavailable` for both Compact and
Reset. The missing-recovery implementation Issue must make Reset reuse the same
expected-binding replacement. Compact remains an independent implementation
gap and is outside that Issue. The implementation Issue still needs to be
created from this specification.

T-001 actually locked `@opencode-ai/sdk@1.18.3`, matching the `opencode` CLI on
PATH, rather than 1.17.18. The earlier decision text retains 1.17.18 as its
point-in-time reference; T-002 and later implementation uses 1.18.3. The smoke
record is
[`sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json).
