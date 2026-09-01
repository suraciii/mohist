# Pi Runtime

`PiRuntime` adapts Mohist execution and Session commands to Pi's in-process SDK
and file-backed Sessions. It keeps Pi service assembly, trust, credentials,
and event details inside one deep module. [`agent-execution.md`](../agent-execution.md)
remains authoritative for AgentJob, AgentSession, and shared binding rules.

## Design Drivers

- The awaited Prompt result is the completion authority. Events are observations,
  not an acceptance-only completion protocol.
- In-process execution avoids a child process per Session but couples active Pi
  Prompts to Runner process failure. Persisted Session files survive; uncertain
  work is not replayed.
- Pi restores by absolute Session-file path, not diagnostic Session UUID. Binding
  identity must use the path that can reopen provider state.
- Pi can load executable project resources and execute tools with Runner
  permissions. Unattended execution must disable project-level Pi configuration.

Mohist implements `mohist/pi` directly through an independent `PiRuntime`. It
does not add ACP, RPC, or a generic cross-Runtime lifecycle. OpenCode and Pi
remain peer modules because their process and Session semantics differ.

## Model

### Capability Boundary

```text diagram
+----------+
| Workflow ++
+----------+|                                           +--------------------+
            |                                       +-->| Provider and tools |
+----------+|   +------------+    +----------------+|   +--------------------+
| AgentJob ++-->| Pi Runtime +--->| in-process SDK ++
+----------+|   +------------+    +----------------+|   +------------------------+
            |                                       +-->| Physical Session files |
+----------+|                                           +------------------------+
| Commands ++
+----------+
```

`PiRuntime` owns SDK service assembly, readiness, model catalog, physical
Session creation and restoration, in-memory Session caching, Prompt completion,
Follow-up, interruption, compaction, event projection, provider-error
classification, project trust, credential redaction, and compatibility
diagnostics. Callers use Mohist request and result types with `runtime: "pi"`;
SDK objects and provider payloads do not cross the boundary.

The module receives fully assembled execution input and a resolved Session
binding. It receives no Mohist Agent identity and never loads an Agent
definition. Workflow expectation, artifacts, WorkResults, and Session ownership
remain with their existing authorities.

Pi may expand file-based Prompt templates, but Mohist renders Workflow Prompts
before the Runtime. Submit plain text with provider template expansion disabled.
A leading `/` reaches the model unchanged.

### Process Topology and Readiness

Each Runner owns one `PiRuntime`. Each active physical Session has one
in-process `AgentSession` instance, created and cached on first use and lazily
restored from its binding after restart. The Runtime does not create a process
per Action or recreate a Session instance for each execution.

Mohist treats the logical AgentSession working directory and physical Session
directory as immutable. A changed directory is rejected; use a new logical
Session identity. Pi stores Session files by current working directory under
`~/.pi/agent/sessions/<encoded-cwd>/` by default. Mohist adds no separate
Session-directory setting.

Before the Runner registers or claims work, `PiRuntime` must assemble SDK
services and load the model catalog. A successful catalog load makes it ready.
An empty catalog warns but does not block readiness. If assembly or loading
fails, the Runtime is not ready and the Runner stops claiming work while it
rebuilds.

Runner termination ends every active Pi Prompt and its callback. Persisted
JSONL Sessions survive. After restart, the Runtime restores them only for a
later admitted input or Session command. It does not adopt a terminated Prompt
or infer its result. The work owner applies `runner-lost` and retry semantics.

### Trust and Credentials

Pi has no per-tool approval mechanism or sandbox. Configured tools execute with
Runner-process permissions, so `permission-required` is not a Pi result.

Set project trust to false. Repository `.pi/` settings, extensions, Skills,
and Prompt templates cannot change unattended Runner behavior. Root `AGENTS.md`
and `CLAUDE.md` remain model context, not executable Pi configuration. The
Runner user's global Pi configuration still loads.

Provider credentials remain under Pi's environment and auth-store mechanisms;
the SDK authentication manager is their only reader. Mohist requests, results,
events, registration, and smoke artifacts contain no credential fields. Redact
SDK and provider text before logs or diagnostics, and serialize SDK objects only
through allowlists.

## Semantics

### Action Input and Output

The common Action contract is defined in
[`../workflow/actions.md`](../workflow/actions.md). Pi uses the same input
shape, expansion timing, and output projection as `mohist/opencode`; see
[`opencode.md`](opencode.md).

- `model` uses `provider/model` and splits at the first `/`. Pi decides whether
  the model is valid.
- `reasoningEffort` uses `off`, `minimal`, `low`, `medium`, `high`, `xhigh`, or
  `max`. Pi maps it privately to its thinking level and reports the canonical
  effort in execution evidence.
- `variant` is independent of reasoning effort and never joins the model ID.
  Pi validates the model and variant at execution.
- Unknown `options` keys are ignored with a diagnostic. This includes a
  persisted `runtime` key or legacy keys; `options` itself carries no Runtime.
  Workflow selects the backend through `uses`.

### Session Binding

The shared target resolution, binding order, reuse, and missing recovery rules
are authoritative in
[`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery).
They require a physical Session before binding persistence, complete binding and
Input identity before the first Prompt, current-binding resolution across tasks,
retries, and Runner restarts, and rejection of a changed working directory.

The physical `runtimeSessionId` is the absolute path in `session.sessionFile`.
Session creation must return that absolute path; an absent or relative value is
`incompatible-runtime`. `SessionManager.open()` restores by path and has no
open-by-UUID operation. `session.sessionId` is diagnostic only.

Only PiRuntime on the binding's `runnerId` may restore from cache or file. A
request on another Runner must route to the bound Runner or fail. A missing
local file on that other Runner is not missing. On a cache miss, the bound
Runner uses `SessionManager.open()` to restore messages, model, and thinking
level. Only an explicitly absent bound path is `definitely-missing` and permits
replacement before a new independent input. Corrupt JSONL, permission failure,
and unclassified SDK errors are unknown and fail explicitly.

Pi writes its Session file only after the first assistant message. A crash
before that point may leave no file. Recovery applies the missing-file rule and
does not replay the original Prompt. At most one unfinished execution with
uncertain submission may be lost within the accepted crash window.

A Runtime change or Reset creates a new physical Session and atomically replaces
the binding without migrating context. Compact and model or variant changes
keep the same file. Model and thinking level apply before the Prompt.

A worktree-cleanup Follow-up uses the original job's Action, not a hard-coded
cleanup Action, with the same Runtime, physical Session, and binding. It does
not replace the binding.

### Execution and Completion

Identity is persisted before effect:

```text diagram
    +-------------------+
    | restore or create |
    |      Session      |
    +---------+---------+
              |
              v
   +---------------------+
   | persist binding and |
   |        Input        |
   +----------+----------+
              |
              v
+--------------------------+
| apply model and thinking |
+-------------+------------+
              |
              v
      +---------------+
      | submit Prompt |
      +---------------+
```

Prepare the binding and report Input identity before the execution deadline
starts. Submit no Prompt through an unpersisted or stale binding.

Resolution of the awaited Prompt means the Agent run, including tool loops and
provider retries, ended. It is the sole completion decision. Events such as
`agent_end` project observations; they cannot decide the result. Reconcile final
assistant text from Session messages. Workflow expectation and AgentJob success
remain with their work owners.

Workflow execution supplies a fixed 60-minute duration through Runner-private
context; AgentJob execution supplies its own deadline. Immediately before
Prompt submission, create the absolute deadline from an injected clock. There
is no separate transport timeout. At the deadline, fix the result as
`deadline-exceeded` before interruption; a late Prompt resolution cannot reverse
it.

An uncertain submission is never replayed automatically. Runner redelivery may
duplicate execution inside the accepted crash window. The JSONL transcript is
Session context, not an execution-outcome ledger.

### Prompt Deadline and Closeout

The deadline protocol has two phases:

1. Five minutes before the deadline, call `session.steer(text)` once. If the
   deadline is shorter than five minutes, call it at execution start. The
   message reaches the current execution at an iteration boundary after the
   current model call and tool calls.
2. At the deadline, fix `deadline-exceeded`, call `await session.abort()`, and
   confirm stop through Session events and `isStreaming`. A late Prompt result
   cannot change the timeout result.

The warning says to stop new work, commit current changes, leave a progress
record, and end. It does not expose the deadline, name a marker or file, or
repeat task-specific contracts. A long tool call may delay the warning; the
deadline still aborts. If the Agent ends normally after the warning, do not
abort; evaluate its own completion contract.

`abort()` has no Boolean result. Its Promise confirms only that the interrupt
request was handled. If events or `isStreaming` do not confirm stop, return an
interruption-unconfirmed diagnostic without claiming safe termination. Do not
commit or roll back residual work automatically or replace the binding after
termination. Only explicit Reset replaces it. Housekeeping Follow-ups use the
same warning.

### Events and Reconciliation

The shared activity and transcript contract is defined in
[`agent-execution.md`](../agent-execution.md#activity-and-transcript). Each
cached physical Session has one in-process subscription. Message and tool
lifecycle signals become idempotent transcript and tool facts. Assistant usage
becomes input, output, cache-read, cache-write, thought, and cost facts.
Compaction and provider-retry signals become their canonical facts. Pi message
and tool-call IDs provide projection identity. Unknown events become diagnostics
only.

Pi callbacks have no remote snapshot cursor or OpenCode-style reconnect stream.
Final state is reconciled from the completed Prompt and Session messages. A
Runner restart must not project the terminated Prompt as active or infer a
result that Pi did not confirm.

### Provider Errors

Pi retries recoverable errors. `auto_retry_start`, with `attempt`,
`maxAttempts`, `delayMs`, and `errorMessage`, is the sole retry-fact source; do
not scan logs.

An `errorMessage` matching quota, credit, billing, usage limit, allowance,
balance, or limit-reset patterns is intrinsically unrecoverable. The default
patterns cover common English and Chinese provider wording; Runner
configuration may add patterns. A plain rate-limit message does not fail on
first sight. If recoverable errors reach threshold N while execution remains
incomplete, classify them as unrecoverable. N is five by default and
Runner-configurable.

Pi-classified unrecoverable errors such as auth, invalid request, or context
overflow end retry and resolve `prompt()` with `stopReason: "error"`. Read the
last assistant message's error information and normalize it to `turn-failed`.

For an unrecoverable error, call `session.abort()` and confirm stop as above.
Return the original provider message. Keep AgentSession and physical binding
unchanged. An unconfirmed abort returns an interruption-unconfirmed diagnostic
and never claims that execution stopped safely.

### Errors

Normalize failures to `invalid-input`, `unavailable-runtime`, `missing-session`,
`incompatible-runtime`, `deadline-exceeded`, `interrupted`, `turn-failed`, or
`conflict`. Provider detail remains redacted diagnostics and never becomes
Action output.

### Session Commands

Commands route by the persisted binding, not an in-memory Session object. A
rejection before Runtime acquisition returns `notStarted`. After a provider
call may have started, timeout, connection loss, or an unconfirmed result
returns `unavailable`; the operation identity is retained and never replayed
automatically.

- **Follow-up:** Active execution receives native steer at an iteration
  boundary. Idle execution starts a Prompt whose preflight callback is the
  acceptance authority. Apply model and thinking level first. Active or unknown
  state never replaces the binding.
- **Compact:** Allow only while idle. Use native compaction with the current
  model. Keep Session-file and AgentSession identity; never use a Mohist summary.
- **Reset:** Allow only while idle. Create an empty Session in the same work
  directory, then replace the complete expected binding. A definitely absent
  old file may skip selection inheritance; corruption, permission failure, and
  unclassified reads remain explicit failures.
- **Stop:** Request interruption. Provider acceptance does not prove stop;
  events and `isStreaming` must confirm it.

A stale expected binding cannot overwrite a newer one. Compact and Reset
preserve AgentSession ID and transcript. Only Reset replaces the physical
binding and starts empty provider context.

### Model Catalog

Pi's catalog contains only models whose providers have configured credentials.
It assists configuration but is not execution authority. Pi publishes native
thinking levels as per-model `reasoningEfforts` and true model variants in its
`variants` map. Pi validates model, effort, and variant at execution. An empty
catalog does not fall back to OpenCode.

Runtime selection and canonical reasoning effort are fixed in the Agent
execution snapshot and current Session binding. Server routes launch and
Session commands by those values; Runner dispatches to the corresponding
Runtime. Shared projections carry normalized transcript, model, applied effort,
cost, and usage facts, including cache-read and cache-write tokens, without Pi
SDK types.

### Upgrade Boundary

Mohist locks `@earendil-works/pi-coding-agent` at 0.80.10 and requires its
compatible Node runtime. Keep all SDK access inside `PiRuntime`.

Every upgrade must smoke-test the locked package for service assembly, catalog
loading, Session creation and restoration, Prompt completion, active and idle
Follow-up acceptance, interruption and stop confirmation, compaction,
model/thinking selection, event payloads, and credential redaction. Generated
method presence is not evidence that completion or failure semantics still
satisfy Mohist.
