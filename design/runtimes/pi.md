# Pi Runtime

## Design Drivers and Decision

Pi exposes coding-agent capabilities as an in-process SDK with file-backed
Sessions. Mohist needs direct completion and typed control without letting Pi's
service assembly, trust model, credentials, or event vocabulary escape into
Workflow, AgentJob, or AgentSession. The shared ownership contract remains
authoritative in [`agent-execution.md`](../agent-execution.md).

Four pressures shape this adapter:

- **Completion must be direct.** The result of the awaited Prompt, not an event
  inferred from an acceptance-only protocol, decides when execution ends.
- **Isolation has a real cost.** In-process execution avoids a child process per
  Session and preserves typed SDK access, but a Runner crash terminates every
  active Pi Prompt. Persisted Session files survive; uncertain work is not replayed.
- **Binding identity follows restoration.** Pi restores by absolute Session-file
  path, not by its diagnostic Session UUID. The binding must use the identity that
  can actually reopen provider state.
- **Repository trust must be explicit.** Pi can load executable project resources
  and runs tools with Runner permissions, so unattended execution must disable
  project-level Pi configuration and keep credentials inside the SDK boundary.

### Options and rationale

ACP is rejected because Pi has no native ACP surface; adding a community bridge
would introduce a second moving protocol without improving completion authority.
RPC mode would isolate crashes, but its Prompt response confirms only acceptance,
completion must be inferred from events, and concurrent Sessions require managed
child processes. A generic `AgentRuntime` interface would then force Pi and
OpenCode into one artificial lifecycle.

Mohist therefore implements `mohist/pi` directly with the in-process SDK inside an
independent `PiRuntime` deep module. If crash isolation becomes a hard product
requirement, RPC may replace the private transport only after it preserves the
same completion and effect contracts; no Workflow Action or Session contract may
change.

Unlike OpenCode, Pi ships as a version-locked Runner dependency and needs no
operator-installed CLI or external Server readiness loop. That simpler topology
is balanced by stronger Runner-process failure coupling and Session-file recovery
rules.

## Action Input and Output Contract

```ts
type PiActionInput = {
  prompt: PromptSpec
  session?: string
  options?: {
    model?: string
    reasoningEffort?: "none" | "minimal" | "low" | "medium" | "high" | "xhigh" | "max"
    variant?: string
  }
}

type PiActionOutput = null | {
  promise: string
}
```

The input shape, expansion timing, and output projection are identical to
`mohist/opencode`; see the Action input and output contract in
[`opencode.md`](opencode.md). `model` uses Pi's `provider/model` form and is
likewise split only at the first `/`. `reasoningEffort` is the cross-Runtime
value `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`; Pi's native
mapping may apply it as a thinking level. `variant` remains a separate
Pi-specific setting and is never an effort alias or part of the model ID.

`options` is a closed object: its only accepted keys are `model`,
`reasoningEffort`, and `variant`. Unknown keys, empty or non-string Model/Variant
values, and an invalid effort reject with `invalid_execution_configuration`
before Session or provider work. A well-formed selection absent from the accepted
static catalog rejects with `unsupported_execution_configuration`. `options` does
not carry `runtime`; the Workflow path selects its backend through `uses`. A
catalog model with an invalid effort/Variant combination rejects with
`incompatible_execution_configuration`; an unavailable catalog rejects with
`execution_catalog_unavailable`. No input is silently ignored, deferred to Pi,
or guessed from provider credentials or a current Session.

## Capability Boundary

```text diagram
Workflow Action ----+
AgentJob executor ---+--> PiRuntime --> in-process SDK --> provider and tools
Session commands ----+                    |
                                         +--> physical Session files
```

`PiRuntime` owns SDK service assembly, readiness, the static capability catalog, physical
Session creation and restoration, in-memory Session caching, Prompt completion,
Follow-up, interruption, native compaction, event projection, provider-error
classification, project trust, credential redaction, and compatibility
diagnostics. Callers use Mohist request/result types with `runtime: "pi"`; SDK
objects and provider payloads never cross the boundary.

The module receives fully assembled execution input and a resolved Session binding.
It receives no Mohist Agent identity and never loads an Agent definition. Workflow
expectation evaluation, artifacts, work results, and Session ownership remain with
their existing authorities.

Pi can expand file-based Prompt templates, but Mohist has already rendered the
Workflow Prompt under its own authority. The Runtime therefore submits plain text
with provider template expansion disabled. A leading `/` reaches the model
unchanged. This prevents repository-local Pi templates from silently changing the
meaning of a validated Workflow input.

## Process Topology and Readiness

Each Runner process owns one `PiRuntime`, shared by all Pi Sessions. Every
active physical Session has one in-process `AgentSession` instance. It is
created and cached on first use of a binding and lazily restored from the
persisted binding after a Runner restart. The Runtime does not create a process
per Action or recreate the Session instance for each execution.

Mohist treats both the logical AgentSession workDir and the physical Session
directory as immutable. If the working directory changes, reject the execution;
the caller must use a new logical Session identity rather than create a
replacement binding on the existing AgentSession. Pi naturally stores Session
files by cwd, under `~/.pi/agent/sessions/<encoded-cwd>/` by default. Mohist does
not add a separate session-directory setting.

Before the Runner registers or claims work, it must:

1. assemble SDK services successfully;
2. load its static, versioned capability catalog successfully.

A valid catalog makes the Runtime ready independently of configured provider
credentials. If service assembly or catalog loading fails, `PiRuntime` is not
ready and the Runner stops claiming new work while it rebuilds. This matches the
`OpenCodeRuntime` readiness gate; both Runtime readiness states participate in
work admission. Mohist never probes Pi or a provider to discover whether a model
or effort is supported.

Pi's different topology has one important consequence: it runs inside the
Runner, so terminating the Runner process terminates every running Pi Prompt.
There is no independent Server exit, rebuild, or event-stream reconnection. The
persisted physical Sessions, stored as JSONL files, survive. The Runner restores
them by binding after restart, but it does not replay terminated execution;
the work owner's redelivery semantics decide what follows.

## Session Binding

See [`agent-execution.md`](../agent-execution.md) for AgentSession ownership and
origin and [`conventions.md`](../conventions.md) for Runtime identity field
names. The shared rules for logical Session target resolution, binding creation
ordering, reuse, and missing recovery are authoritative in
[`agent-execution.md`](../agent-execution.md#runtime-session-missing-recovery).
Those rules require creating the physical Session before persisting the binding,
submitting the first Prompt only after idempotent persistence succeeds,
resolving to the current binding across tasks, retries, and Runner restarts, and
rejecting a changed working directory before Prompt submission with an
actionable error. This section defines only Pi-specific behavior.

The physical binding `runtimeSessionId` persists the **absolute path to the Pi
Session file**, `session.sessionFile`. Session creation must return that absolute
path identity; an absent or relative value is `incompatible-runtime`. The SDK
restoration entry point
`SessionManager.open()` is keyed by file path and has no open-by-UUID surface.
The Session UUID, `session.sessionId`, is diagnostic only.

Physical Session restoration is lazy. Only the PiRuntime on the binding's
`runnerId` may restore from its in-process cache or the bound Session-file path.
A request reaching another Runner must route back to the bound Runner or fail
explicitly; a missing local file on that other Runner is not evidence that the
binding is missing. On a cache miss, the bound Runner uses
`SessionManager.open()`, which restores messages, model, and Pi's native
reasoning state. That native state is derived from the saved reasoning-effort
mapping, not from Variant.
Only an explicitly absent binding path yields a `definitely-missing` fact and
permits automatic replacement before a new independent input is submitted. If
the file exists but cannot be opened, its JSONL is corrupt, permission fails, or
the SDK error cannot be classified, existence is unknown rather than missing.
Fail the work instead of hiding data or compatibility failure with an empty
Session.

Pi does not write its Session file until the first assistant message appears.
A Runner crash during the first Prompt can therefore leave a binding whose file
never existed. Restoration after restart treats that condition under the
missing-file rule above. The work owner still decides the original Prompt's
submission state and the Runtime never replays it automatically. Only a later,
independent input may establish a replacement. At most one unfinished execution
with already-uncertain submission state is lost, matching the accepted
redelivery duplicate-execution limitation.

A Runtime change or Reset creates a new physical Session and atomically replaces
the current binding without migrating context. Compact and changes to model,
reasoning effort, or variant keep the same Session file. Those are execution
parameters applied to the existing physical Session before the Prompt; they do
not trigger binding replacement.

Worktree-cleanup Follow-up behaves exactly as in `OpenCodeRuntime`: the executor
invokes the original task's resolved Action again, using the same Runtime and
physical Session, without replacing the binding.

## Execution and Completion Authority

Pi follows the same identity-before-effect rule as OpenCode, but restoration uses
the bound Session-file path:

```text diagram
restore or create physical Session
               |
               v
persist complete binding and Input identity
               |
               v
apply model, reasoning effort, and variant
               |
               v
submit Prompt
```

No Prompt is submitted through an unpersisted or stale binding. Preparation and
Input reporting occur before the execution deadline starts, because they establish
the identity needed to interpret any later provider effect.

Resolution of the awaited Prompt means the agent run, including tool loops and
provider retries, has ended. It is the sole completion decision. Events such as
`agent_end` project observations but cannot decide the result; final assistant
text is reconciled from the Session messages. Workflow expectation evaluation and
AgentJob success remain with their respective work owners.

Workflow execution supplies a fixed 60-minute duration through Runner-private
context; it is not an Action input. AgentJob execution supplies its own deadline.
Immediately before Prompt submission, the Runtime creates the absolute deadline
from an injected clock. There is no separate transport timeout for the in-process
call. At the deadline, the result is fixed as `deadline-exceeded` before
interruption begins, and a late Prompt resolution cannot reverse it.

A failure with uncertain submission state is never replayed automatically. Runner
redelivery can duplicate execution inside the accepted crash window; that explicit
limitation is safer than reconstructing a fictitious provider replay identity.

## Prompt Deadline and Two-Phase Closeout

The deadline protocol matches the corresponding OpenCode protocol in
[`opencode.md`](opencode.md): inject one task-independent closeout warning five
minutes before the deadline, or immediately at execution start when the total
deadline is under five minutes; at the deadline, first fix
`deadline-exceeded`, then interrupt for closeout. Only the Pi channels differ:

- Warning injection uses `session.steer(text)`. The current execution receives
  the steer message at an iteration boundary, after the current model call and
  its tool calls, matching OpenCode `promptAsync` injection. A long-running tool
  call can delay receipt; the deadline still aborts.
- Termination uses `await session.abort()`, then confirms stop through Session
  events and `isStreaming`. If confirmation fails, return an
  interruption-unconfirmed diagnostic without claiming execution stopped
  safely.

Version 0.80.10 has neither a separate stop-confirmation operation nor a
Boolean `abort()` result. The Promise from `abort()` means only that the
interrupt request was handled. Stop confirmation must observe `isStreaming`
and the event sequence.

## Events and State Reconciliation

The shared activity and transcript contract is authoritative in
[`agent-execution.md`](../agent-execution.md#activity-and-transcript). Pi events
are observations used for low-latency projection, not a completion protocol.

Each cached physical Session has one in-process subscription. Message and tool
lifecycle signals become idempotent transcript and tool facts; assistant usage
becomes input, output, cache-read, cache-write, thought, and cost facts; compaction
and provider-retry signals become their corresponding canonical facts. Pi message
IDs and tool-call IDs provide projection identity. Unknown events enter diagnostics
only.

Because the channel is an in-process callback, it has no OpenCode-style reconnect
or remote snapshot cursor. Final state is reconciled from the completed Prompt and
Session messages. Runner termination ends both the callback and current execution;
restart must not project the old Prompt as still active or infer a result that Pi
never confirmed.

## Provider Error Failure Policy

The decision rules match the OpenCode provider error policy in
[`opencode.md`](opencode.md): Pi retries recoverable errors; an unrecoverable
error aborts and fails the current execution. Pi provides these signals:

- `auto_retry_start`, carrying `attempt`, `maxAttempts`, `delayMs`, and
  `errorMessage`, is the sole retry-fact source. Do not scan logs.
- Intrinsically unrecoverable: if `errorMessage` matches quota, credit, billing,
  usage limit, allowance, balance, or limit-reset patterns, abort and fail. The
  default pattern set matches OpenCode's and covers common English and Chinese
  provider wording; Runner configuration may add patterns. A plain rate-limit
  message does not fail on first sight through this fallback.
- Unrecoverable by evidence: if recoverable errors reach consecutive retry
  threshold N, five by default and Runner-configurable, while execution remains
  incomplete, abort and fail. Consume `attempt` directly instead of storing a
  second counter.
- Errors Pi itself classifies as unrecoverable, such as auth, invalid request,
  or context overflow, end automatic retry and resolve `prompt()` normally with
  `stopReason: "error"`. The Runtime reads the last assistant message's error
  information and normalizes it to `turn-failed`.

When either unrecoverable rule matches, call `session.abort()`, confirm stop as
above, and return a failure fact containing the original provider message. Keep
the AgentSession and physical Session binding unchanged and do not suggest
Reset.

## Session Command Semantics

The generic external-effect rules match
[`opencode.md`](opencode.md#session-command-semantics): a command rejected before
Runtime acquisition is `notStarted`; once a provider call may have started, an
unconfirmed result is `unavailable`, retains the original operation identity, and
is never replayed automatically. Every command routes by the persisted complete
binding, not by an in-memory Session object.

| Command | Pi-specific contract |
|---|---|
| Follow-up | Active execution receives a native steer at an iteration boundary. Idle execution starts a Prompt whose preflight callback is the acceptance authority. Model, reasoning effort, and variant are applied first. Preflight rejection is definitive; active or unknown state never triggers binding replacement. |
| Compact | Allowed only while idle. Uses native compaction with the current model, keeps the Session-file binding and AgentSession identity, and never falls back to a Mohist-generated summary. |
| Reset | Allowed only while idle. Creates an empty Session in the same work directory, then replaces the complete expected binding. A definitely absent old file may skip selection inheritance; corruption, permission failure, and unclassified reads remain explicit failures. |
| Cancel | Requests interruption. Provider acceptance of the request does not prove execution stopped; events and `isStreaming` provide stop confirmation. |

Compact and Reset preserve the AgentSession ID and transcript. Only Reset replaces
the physical Session-file binding and begins empty provider context. A stale
expected binding can never overwrite a newer one.

## Trust, Credentials, and Errors

Pi has no per-tool approval mechanism or sandbox. Configured tools execute with
Runner-process permissions, so `permission-required` is not a Pi result and there
is no equivalent of OpenCode's one-time permission reply.

Pi can also load executable settings, extensions, skills, and Prompt templates
from a repository's `.pi/` directory. Mohist always sets project trust to false,
so repository-local Pi resources cannot alter unattended Runner behavior. Root
`AGENTS.md` and `CLAUDE.md` remain model context rather than executable Pi
configuration, and the Runner user's global Pi configuration still loads. This is
a fixed determinism boundary, not a user option.

Provider credentials remain under Pi's environment and auth-store mechanisms. The
SDK authentication manager is their only reader; Mohist requests, results, events,
registration, and smoke artifacts have no credential fields. SDK and provider
text is redacted before it enters logs or diagnostics, and structured boundaries
use allowlists rather than serializing SDK objects.

The Runtime normalizes failures to `invalid-input`, `unavailable-runtime`,
`missing-session`, `incompatible-runtime`, `deadline-exceeded`, `interrupted`,
`turn-failed`, or `conflict`. Provider detail remains redacted diagnostics and
never becomes Action output.

## Versioned Capability Catalog and Integration Boundary

The Pi integration owns static catalog content and mapping format published with
the Runtime release. Server owns registry acceptance and the versioned entry
used to validate Agent create/edit, readiness, and launch resolution. Each
accepted entry names `catalogVersion`, Model, supported and default
ReasoningEffort values, whether the Model has a Variant dimension, supported
Variant values, its nullable `defaultVariant`, and the complete non-secret
native mapping for Pi. `defaultVariant` is null only when the Model has no
Variant dimension; otherwise it is one supported non-empty Variant. Runner only
declares which accepted versions and mapping formats its adapter can apply.
Provider credentials and live availability do not alter catalog support.

Runtime selection is frozen into the Agent execution snapshot and current Session
binding. Server routes launch and Session commands by that value; Runner dispatches
to the corresponding independent deep module using the saved catalog version and
native mapping. A Job waits with `exact_execution_unavailable` when no available
adapter can apply that exact configuration; it never falls back to a different
model, effort, variant, or catalog. Shared Session projections carry normalized
transcript, model, cost, and usage facts, including cache-read and cache-write
tokens, without exposing Pi SDK types. This capability boundary replaces
component-by-component Pi special cases in Server and Web.

## Verification Boundary

Default verification replaces SDK services, Sessions, provider events, storage,
and time with deterministic fakes. It proves readiness gating, binding-before-effect,
absolute-path restoration, missing versus corrupt or unknown classification,
completion authority, no replay, active versus idle Follow-up, native Compact and
Reset semantics, project-trust exclusion, provider retry policy, and two-phase
closeout. A real smoke test is upgrade evidence for the locked SDK surface; it is
not a substitute for these state and failure assertions.

## Upgrade Boundary

Mohist locks `@earendil-works/pi-coding-agent` at 0.80.10 and requires the
compatible Node runtime. Pi is a rapidly evolving 0.x dependency whose breaking
changes concentrate in service assembly, Session creation/restoration, and type
shapes. All SDK access remains inside `PiRuntime` so upgrade drift changes one
deep module rather than the Workflow Action or AgentSession contracts.

Every upgrade reads the upstream breaking-change notes and smoke-tests the exact
locked package against real service assembly, catalog loading, Session create and
restore, Prompt completion, active and idle Follow-up acceptance, interruption and
stop confirmation, compaction, model/reasoning-effort/variant selection, event payloads, and
credential redaction. A method existing in generated types is not evidence that
its completion or failure semantics still satisfy Mohist.

Upgrade evidence follows the redacted artifact shape demonstrated by
[`sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json).
