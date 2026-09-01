# OpenCode Runtime

`OpenCodeRuntime` adapts Mohist execution and Session commands to OpenCode. It
keeps OpenCode's Server, event, directory, permission, and retry concepts inside
one deep module. [`agent-execution.md`](../agent-execution.md) remains
authoritative for AgentJob, AgentSession, and shared binding rules.

## Design Drivers

- The completed Prompt response is the only completion authority. Events may be
  delayed, duplicated, or disconnected.
- A transport failure after Prompt submission has an unknown effect. Mohist must
  not replay that Prompt.
- One shared OpenCode Server owns process health, the global event stream, and
  directory Instances for the Runner.
- SDK and operator CLI versions can differ. Provider incompatibility must become
  an actionable readiness or execution error, not a leaked SDK type.

Mohist uses an independent `OpenCodeRuntime` and the direct `mohist/opencode`
Action. It does not use ACP, a generic cross-Runtime interface, or a second
fallback protocol. AgentJob execution and Session commands are its stable
boundaries. The Runtime does not define `mohist/agent` or alter the Agent
product. OpenCode and Pi keep separate modules because their Session and
process semantics differ.

## Model

### Capability Boundary

```text diagram
+----------+
| Workflow ++
+----------+|                                           +-----------+
            |                                       +-->| Instances |
+----------+|   +---------+    +-----+    +--------+|   +-----------+
| AgentJob ++-->| Runtime +--->| SDK +--->| Server ++
+----------+|   +---------+    +-----+    +--------+|   +----------+
            |                                       +-->| Sessions |
+----------+|                                           +----------+
| Commands ++
+----------+
```

`OpenCodeRuntime` owns the OpenCode Server and Client, readiness, physical
Session creation and resolution, Prompt execution, Follow-up, interruption,
compaction, event and snapshot reconciliation, permission replies,
provider-error classification, and directory Instance release. Callers use
Mohist request and result types. SDK DTOs, provider call ordering,
reconnection, and provider diagnostics stay inside the module.

The module receives fully assembled execution input and a resolved Session
binding. It receives no Mohist Agent identity and never resolves an Agent
definition. Workflow expectation, artifacts, WorkResults, and Session ownership
remain with their existing authorities.

### Process Topology

Each Runner owns one OpenCode Server and one Client. The Server caches multiple
Instances by resolved directory. An Instance holds OpenCode configuration,
plugins, LSP, MCP, and related resources. It is not a Runner Workspace,
AgentSession, or physical Session.

Use the official embedded Server and Client factories. Do not add a child
process protocol or a Server per Action. Every provider request carries its
resolved working directory and enters one typed error-normalization boundary.

A physical Session directory is immutable. A changed directory requires a new
physical Session. Before the Runner registers or claims work, it must start the
Server, pass the health check, and establish the global event subscription.

If the Server exits, the Runner stops claiming work and rebuilds the Server,
Client, and event subscription. Affected execution fails and is never replayed
automatically. The Runner becomes ready when the replacement Server is healthy
and subscribed; model discovery is not a readiness prerequisite.

## Semantics

### Action Input and Output

The common Action contract is defined in
[`../workflow/actions.md`](../workflow/actions.md). `prompt` is rendered to
non-empty text by [`../workflow/task-dispatch.md`](../workflow/task-dispatch.md);
the Runtime does not read Workflow Variables or expand templates.

`model` uses `providerID/modelID` and splits at the first `/`; the model ID may
contain more slashes. `variant` is a separate model-variant value and may be
provided without `model`. Without an explicit model, use the OpenCode default;
variant still applies to the Prompt. Omitting either preserves the Session
selection or default. Changing model or variant does not replace the physical
Session. An explicit `reasoningEffort` is rejected as
`unsupported_execution_configuration` because OpenCode does not support it.

`uses: mohist/opencode` selects the Runtime. Input has no `agent`, `kind`, or
`type`. OpenCode owns native Agent, tool, plugin, permission, and
automatic-compaction settings. Present `model` and `variant` values must be
strings. Unknown `options` keys are ignored with a diagnostic. An explicit
`reasoningEffort` is rejected before Session or provider acquisition; it is not
folded into `model` or treated as `variant`.

Action output excludes Runtime identity, transcript, model, usage, diagnostics,
and expectation details. The work owner evaluates `expect` against normalized
final assistant text and creates `{ promise }` only for a matched promise. The
Runtime returns execution facts, not Workflow output.

### Directory Instance Reclamation

The OpenCode Server retains resources per resolved directory. A terminal
WorkflowRun makes a directory eligible for reclamation, but does not prove that
OpenCode is idle. Stopped work, an accepted Follow-up, and an uncertain Runtime
effect may still be active. Reclamation does not change Workflow state or delete
a Workspace.

A Server generation runs from one healthy Server start until exit or shutdown.
The Runtime tracks only directories used in that generation. Maintenance
considers tracked Workflow Workspaces in `eligible` or `stuck` state. It excludes
active or unregistered Workspaces, AgentJob directories, and the startup
directory. Server replacement clears the set. The bounded set avoids history
scans and cleanup calls that could create an Instance.

Reclamation and Runtime operations share one exclusive fence per directory:

```text diagram
    +---------------------+
    | operation admitted? |
    +----------+----------+
     +---------+----------+
     vyes                 vno
 +-------+  +--------------------------+
 | defer |  | status invalid, missing, |
 +-------+  | busy, retry, or unknown? |
            +-------------+------------+
                  +-------+--------+
                  vyes             vno: empty or idle
              +-------+   +-----------------+
              | defer |   | dispose = true? |
              +-------+   +--------+--------+
                        +----------+---------+
                        vno                  vyes
                   +--------+    +-----------------------+
                   | retain |    | forget for generation |
                   +--------+    +-----------------------+
```

The fence remains held through confirmed disposal. Prompt, Follow-up, Stop,
Compact, Reset, and Session queries cannot race it. Disposal releases process
resources only. It does not delete a physical Session, AgentSession, binding,
transcript, or Workspace.

Workspace deletion reacquires the same fence, rechecks local operations and
provider status, disposes when required, and holds the fence through disk and
registry removal. An untracked directory uses a temporary fence and is not
probed or disposed. Busy, unknown, or failed disposal prevents deletion. An
old Server generation cannot authorize an unstarted callback, and generation
reset cannot release operations after deletion starts.

A failed directory stays for a later bounded pass. It changes no Workflow,
TaskRun, or AgentSession result and does not trigger global disposal or an
unrelated interrupt. Passes are single-flight and emit aggregate counts.
Per-directory disposal does not promise an immediate RSS reduction; process
recycling is separate.

### Session Binding

The shared logical target, binding order, reuse, and missing-recovery rules live
in [`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery).
`OpenCodeRuntime` receives a resolved logical target and cannot create or change
its origin. The shared rules require a physical Session before binding
persistence, complete binding and Input identity before the first Prompt, and
current-binding resolution across tasks, retries, and Runner restarts.

Before new independent input, only the Runtime on the binding's `runnerId` may
verify it through `client.session.get()`. Another Runner must route to the bound
Runner or fail. A local 404 there is not missing. Only a structured
Session-not-found or HTTP 404 from OpenCode on the bound Runner is
`definitely-missing`. Network, timeout, authentication, permission, 5xx, and an
unexpected missing ID are not missing; the last case is SDK/Server
incompatibility.

After `definitely-missing`, create one Session in the same directory. Use the
model resolved for the input and apply `variant` to the Prompt. If creation
fails, the new Session is immediately missing, or a concurrent operation
changes the binding, fail without a second create.

Model and variant are execution parameters. They do not enter the Session cache
key, gate `resumeSession`, or replace a binding. Reuse applies them to the
existing physical Session before the Prompt.

A worktree-cleanup Follow-up invokes the original job's Action, not a
hard-coded cleanup Action, and preserves WorkflowRun, Session name, Work ID,
and working directory. It reaches the same Runtime and physical Session. It is
not Reset and does not replace a binding.

The owning AgentJob serializes work-originated Prompts within one logical
AgentSession. Different logical Sessions may run concurrently. A user
Follow-up is a Session command and may be accepted while work executes.

### Execution and Completion

The safety order is identity before effect:

```text diagram
        +-----------------+
        | resolve binding +--------------------------+
        +--------+--------+                          |
           +-----+----------+                        |
           vmissing         vunknown / incompatible  |
 +------------------+   +------+                     |
 | create candidate |   | stop |                     |
 +---------+--------+   +------+                     |
           +-+                                       |
             v                                       |
      +-------------+                                |
      | binding CAS |                                |
      +------+------+                                |
             |                                       |
             v                                       |
+------------------------+                   present |
| persist Input identity |<--------------------------+
+------------+-----------+
             |
             v
     +---------------+
     | submit Prompt |
     +---------------+
```

Create a new physical Session before persisting its binding. Submit the first
Prompt only after the complete binding and Input identity are durable. A stale
binding, failed replacement, or uncertain existence stops before submission.
OpenCode has no generally available replay identity.

The completion-bearing Prompt response is the sole execution-completion
authority. An SSE `idle` event, silence, or disconnected event stream cannot
declare success or failure. Events project facts quickly; snapshots reconcile
missing observations before final presentation. Workflow and AgentJob owners
interpret the normalized completion fact for their own results.

The work owner supplies the deadline, with a 60-minute default when none is
provided. The executor AbortSignal is the only deadline authority. Local HTTP
transport must not impose a shorter timeout. If transport fails after
submission may have started, request and verify interruption, then fail without
resubmission. Startup and readiness may retry; uncertain Prompt acceptance may
not. A process crash may still permit duplicate execution during the accepted
crash window. This does not justify replay from transcript state.

### Prompt Deadline

`OpenCodeRuntime` applies one two-phase closeout to every Prompt:

1. Five minutes before the deadline, call `client.session.promptAsync()` once
   to inject a closeout warning and return immediately without awaiting it. If
   the deadline is shorter than five minutes, inject it at execution start.
2. At the deadline, fix the result as `deadline-exceeded`, then call
   `client.session.abort()`. Abort and status verification add diagnostics only;
   a late Prompt response cannot change the result.

The warning is task-independent: stop new work, commit current changes, leave a
progress record, and end. It names no marker or file and does not repeat task
specific contracts such as `unfinished` or `progress.txt`. The injected message
is a user Follow-up and may wait for an iteration boundary after the current
model call and tool calls. A long tool call may delay it, but the deadline still
aborts. Warning and interruption remain in the transcript. If the Agent ends
normally after the warning, do not abort; evaluate its own completion contract.

Do not expose the deadline in the Prompt, commit or roll back residual work
automatically, or replace the binding after termination. Only explicit user
Reset replaces it. Housekeeping Follow-ups use the same warning and no new
execution category.

### Events and Reconciliation

The shared activity and transcript contract is defined in
[`agent-execution.md`](../agent-execution.md#activity-and-transcript). The
Runner maintains one `client.global.event()` subscription for the shared Server.
The Runtime routes events by Session ID and directory. Known typed events become
Mohist transcript, tool, usage, model, status, and compaction facts. Unknown
events become diagnostics only.

Events reduce display latency but are not a persistent execution protocol:

- Use OpenCode message ID and part ID for idempotent projection.
- Re-establish one global stream when it disconnects while subscribers remain.
- After reconnect, read `session.status()` and relevant
  `session.get/messages()` snapshots for each current execution.
- Consume retry facts only for the matching Session ID and directory.
- Reconcile messages after Prompt completion when events are missing or final
  presentation needs confirmation.

Do not persist a V2 history cursor, aggregate sequence, or event replay state.
Workflow executors still apply expectation, artifact, `failIf`, and recovery
semantics after the Action result. AgentJob independently decides AgentJob
completion.

### Provider Errors

OpenCode retries transient 429, 5xx, and network failures. Mohist fails only
when the error is unrecoverable. Retry facts come from `session.status` events
with `type:"retry"`, status snapshots after reconnection, and final Prompt
rejection. Do not scan logs. The retry event carries `attempt`, `message`,
`action`, and `next`.

Use structured `action.reason` when available. Otherwise match `message` against
quota, credit, billing, usage-limit, allowance, balance, and limit-reset
patterns. The default set covers common English and Chinese provider wording;
Runner configuration may add patterns. A plain rate-limit or too-many-requests
message does not fail on first sight. If recoverable errors reach threshold N
while execution remains incomplete, classify them as unrecoverable. N is five
by default and Runner-configurable.

OpenCode-classified unrecoverable errors, including auth, invalid request,
context overflow, and content policy, reject directly. A silent hang remains
covered by the deadline. Use the retry event's `attempt` directly. After restart
or stream reconnection, restore it from `session.status()` rather than keeping a
second counter.

`session.error` uses `{ name, data: { message, ... } }`. Use `data.message`
first, then retain top-level `message` and string forms only as compatibility
fallbacks. A cleanup or stop-confirmation failure is an additional diagnostic;
it must not replace a provider message.

On an unrecoverable decision, call the locked SDK abort with `throwOnError`.
Confirm only `data: true` plus a status snapshot that omits the Session or marks
it idle. Return the original provider message and keep AgentSession and binding
unchanged. If abort or confirmation fails, return `abort-unconfirmed` without
claiming that execution stopped. Attach that diagnostic to `deadline-exceeded`
when the deadline caused the abort. Do not modify OpenCode's retry behavior.

### Session Commands

Commands use the persisted binding; Runner memory is only a cache. A rejection
before provider acquisition returns `notStarted`, so Server may end the
reservation. Once a provider call may have started, timeout, connection loss,
or an unconfirmed reply returns `unavailable`. Server retains the operation
identity and must not replay or replace it.

- **Follow-up:** Use native asynchronous input. Active execution receives it at
  an iteration boundary; idle execution starts after binding preparation.
  Acceptance returns immediately and completion remains event-driven. Active or
  unknown state never replaces the binding.
- **Compact:** Allow only while idle. Use native compaction with the current
  model. Keep physical and logical Session identity; fail when no model is known.
- **Reset:** Allow only while idle. Create an empty Session in the same
  directory, then replace the complete expected binding. A definitely missing
  old Session may skip model inheritance; other read failures remain explicit.
- **Stop:** Request interruption. Success requires the same stop-confirmation
  rule as deadline and provider-error handling.

Every command carries the complete expected binding. Replace only when that tuple
is still current, so a stale Reset cannot overwrite newer state. Compact and
Reset preserve AgentSession ID and transcript; only Reset replaces the physical
binding and starts empty provider context.

### Permissions and Errors

OpenCode permission configuration is authoritative. Allowed operations execute;
explicit denials remain denied; `ask` delegates one operation to the caller.
For a `permission.asked` event belonging to the current headless execution,
reply `once` through the SDK.

Route the event by current physical Session ID and, when present, matching
`workDir`. Reply at most once per request ID in one execution. The reply changes
only that permission request. It does not write OpenCode configuration, Session
permission rules, or Workflow Approval.

If reply throws or does not confirm success, abort immediately. After stop
confirmation, return `permission-required`, not `interrupted`. OpenCode owns
per-tool timeout and retry; Mohist owns the execution deadline and abort
confirmation.

Normalize SDK errors to `invalid-input`, `unavailable-runtime`,
`missing-session`, `incompatible-runtime`, `permission-required`,
`deadline-exceeded`, `interrupted`, or `turn-failed`. Provider detail remains
redacted diagnostics and never becomes Action output. Do not create a global
Workflow error enum. Map local transport codes such as header/body timeout to
stable actionable text.

### Model Catalog

The catalog assists configuration. It is not execution or readiness authority.
`RunnerHost` discovers it from the operator-provided CLI through a bounded,
shell-free process. OpenCode validates model and variant when execution starts.
It reports `supportsReasoningEffort=false` and no reasoning-effort values;
an explicit effort remains an execution-configuration failure.

A complete non-empty result replaces the snapshot. A parseable non-empty result
from a timed-out process is incomplete: it may add models or variants but cannot
delete known values. Empty or failed refreshes retain the last non-empty
snapshot. Only a changed snapshot prompts an immediate registration heartbeat.
Catalog refresh never makes a healthy Runtime unready.

### Upgrade Boundary

Mohist locks `@opencode-ai/sdk` at 1.18.3; the operator supplies the OpenCode
CLI. Exact equality is not required. Health, Session completion, events,
interruption, compaction, and Instance disposal must remain compatible.
Mohist never installs or upgrades the CLI.

Use the mature Session surface for supported capabilities. Do not replace it
with the V2 surface merely because generated methods exist. A locked-version
smoke check must prove every used capability and preserve completion,
missing/unknown handling, abort confirmation, and no-replay semantics. Such a
change stays inside `OpenCodeRuntime` and cannot alter Workflow Action or
AgentSession contracts.
