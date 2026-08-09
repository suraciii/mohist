# Pi Runtime

## Decision

`mohist/pi` is a Runtime-specific Action implemented **in process** with the
`@earendil-works/pi-coding-agent` SDK. See
[`agent-execution.md`](../agent-execution.md) for the Agent / Session ownership
model and its invariants, including Inline Agents, work ownership, and the rule
that a shared Runtime creates no dependency. `PiRuntime` and
`OpenCodeRuntime` are parallel deep modules: they share no interface and wrap
neither one around the other.

Integration choices:

- **No ACP.** Pi has no native ACP support. Every existing ACP path is a
  community adapter, either bridging to `pi --mode rpc` or embedding the SDK.
  Adding one would put another third-party moving part above the SDK and would
  conflict with the existing decision to remove `mohist/acp-agent` without an
  ACP fallback.
- **No `--mode rpc`.** The RPC `prompt` response confirms only acceptance;
  completion must be inferred from the event stream. One RPC process holds only
  one active Session, so concurrent Sessions require child-process management,
  and the interface is not type-safe. SDK `session.prompt()` awaits execution
  completion directly, matching the `OpenCodeRuntime` rule that the prompt
  response is the sole completion decision. If process isolation later becomes
  a hard requirement, for example to prevent a Pi crash from terminating the
  Runner, RPC can be reconsidered. That change would stay inside `PiRuntime`
  and would not affect the Workflow Action or Session product contract.
- **No generic `AgentRuntime` interface.** The stable boundaries are the
  Workflow Action contract, AgentJob execution contract, and Session commands.
  `PiRuntime` deliberately owns a separate set of boundary types with
  `runtime: "pi"`. Their shape parallels `runtime/opencode/types.ts`; that is
  intentional duplication, not a missing abstraction.

The responsibility difference from OpenCode is that Pi is an npm dependency of
the Runner, shipped and version-locked with it. The operator need not install a
Pi CLI. Provider credentials follow Pi's own environment-variable and auth
storage mechanisms; Mohist does not manage API keys. The SDK authentication
manager is the only reader of credential values. Mohist request/result types,
events, registration, and smoke artifacts contain no credential fields.

## Action Input and Output Contract

```ts
type PiActionInput = {
  prompt: PromptSpec
  session?: string
  options?: {
    model?: string
    variant?: string
  }
}

type PiActionOutput = null | {
  promise: string
}
```

The input shape, expansion timing, and output projection are identical to
`mohist/opencode`; see the Action input and output contract in
[`opencode.md`](opencode.md). There are only two differences:

- `model` uses Pi's `provider/model` form and is likewise split only at the
  first `/`. Pi remains the final authority on whether the model is valid.
- `variant` maps to a Pi thinking level: `off`, `minimal`, `low`, `medium`,
  `high`, `xhigh`, or `max`. It remains a separate field and must not be joined
  to the model ID. Pi rejects invalid values, which Mohist normalizes as an
  execution failure; Mohist does not prevalidate the set.

Keys in `options` other than `model` and `variant` are ignored with a diagnostic
and do not fail execution. This allows persisted `vars.agent` values containing
a `runtime` key, used by the Mohist Agent path, or legacy keys to remain
bindable to this Action. `options` does not carry `runtime`; the Workflow path
selects its backend through `uses`.

## SDK Surface

The dependency is `@earendil-works/pi-coding-agent`; its npm scope migrated from
`@mariozechner/*` to `@earendil-works/*`. It requires Node >= 22.19. The SDK is
purely in process: LLM requests and built-in tool execution occur inside the
Runner process, with no separate Server process.

| Capability | SDK operation |
|---|---|
| create a physical Session | `SessionManager.create(cwd, sessionDir?)`, then `createAgentSession({ sessionManager, modelRuntime, settingsManager, resourceLoader, ... })` |
| restore a physical Session | `SessionManager.open(sessionFile)`, then create `AgentSession` with the same explicit services |
| execute and await a Workflow / AgentJob Prompt | `await session.prompt(text, { expandPromptTemplates: false })` |
| inject a closeout warning during execution | `session.steer(text)` |
| submit a user Follow-up during execution | `session.steer(text)` |
| submit a user Follow-up while the Session is idle | `session.prompt(text)` without awaiting completion |
| interrupt execution | `await session.abort()`; confirm stop through `session.isStreaming`, not the abort return value |
| compact context | `session.compact()` |
| apply execution model and reasoning level | `session.setModel()`, `session.setThinkingLevel()` |
| read Session state and messages | `session.sessionId`, `session.sessionFile`, `session.messages`, `session.isStreaming` |
| receive real-time events | `session.subscribe(listener)` |
| read the model catalog | create `ModelRuntime` with `ModelRuntime.create({ ... })`, then `await modelRuntime.getAvailable()` |

Before implementation, lock the SDK package version and smoke-test every SDK
claim in this table against a real Pi installation, including event payload
shapes. If the surface has drifted, revise this table before implementing. The
real 0.80.10 verification is stored at
`openspec/changes/issue-450/sdk-smoke-verification.json`, following the artifact
practice demonstrated by
[`sdk-smoke-verification.json`](../../openspec/changes/archive/2026-07-18-issue-409/sdk-smoke-verification.json).

## Deep Module Boundary

`PiRuntime` is a deep module inside the Runner. It owns:

- SDK service assembly (`ModelRuntime`, `SettingsManager`, and
  `DefaultResourceLoader`) and the model catalog;
- readiness and compatibility diagnostics;
- physical Session creation, restoration by binding, instance caching, and
  interruption;
- Prompt execution, Follow-up, Compact, and Reset;
- event subscription and normalized projection;
- Pi errors and version-compatibility diagnostics.

Its boundary follows the same rule as `OpenCodeRuntime`: the `mohist/pi`
Action, AgentJob execution adapter, and Session command handler depend only on
Mohist request/result types in `runtime/pi/types.ts`, whose `runtime` literal is
`"pi"`. They expose no SDK types. The Runtime receives fully assembled execution
input and a Session binding. It receives no Mohist Agent ID or name and does not
load a Mohist Agent definition. Model-string parsing, `Model` object creation,
call ordering, instance caching, and Pi error interpretation remain inside the
module.

This is not a method-by-method SDK wrapper. Callers request Mohist capabilities
such as execute Prompt, Follow-up, Compact, and Reset. The module decides which
SDK operations implement each capability.

Execution input is submitted as plain text. `PiRuntime` does not load Prompt
templates or expand slash commands; a Workflow Prompt beginning with `/` must
reach the model unchanged. In 0.80.10, `prompt()` expands file-based Prompt
templates by default, so every Workflow call must explicitly pass
`{ expandPromptTemplates: false }`.

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
2. load the model catalog successfully.

A successful catalog load makes the Runtime ready. An empty catalog, meaning no
provider with configured credentials, emits a warning diagnostic but does not
block readiness; Pi remains the final model-validity authority at execution.
If service assembly or catalog loading fails, `PiRuntime` is not ready and the
Runner stops claiming new work while it rebuilds. This matches the
`OpenCodeRuntime` readiness gate; both Runtime readiness states participate in
work admission.

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
Session file**, `session.sessionFile`. A path must exist for a Session created by
`SessionManager.create()`; absence of the file path is
`incompatible-runtime`. The SDK restoration entry point
`SessionManager.open()` is keyed by file path and has no open-by-UUID surface.
The Session UUID, `session.sessionId`, is diagnostic only.

Physical Session restoration is lazy. Only the PiRuntime on the binding's
`runnerId` may restore from its in-process cache or the bound Session-file path.
A request reaching another Runner must route back to the bound Runner or fail
explicitly; a missing local file on that other Runner is not evidence that the
binding is missing. On a cache miss, the bound Runner uses
`SessionManager.open()`, which restores messages, model, and thinking level.
Only an explicitly absent binding path yields a `definitely-missing` fact and
permits automatic replacement before a new independent input is submitted. If
the file exists but cannot be opened, its JSONL is corrupt, permission fails, or
the SDK error cannot be classified, fail the work instead of hiding data or
compatibility failure with an empty Session.

Pi does not write its Session file until the first assistant message appears.
A Runner crash during the first Prompt can therefore leave a binding whose file
never existed. Restoration after restart treats that condition under the
missing-file rule above. The work owner still decides the original Prompt's
submission state and the Runtime never replays it automatically. Only a later,
independent input may establish a replacement. At most one unfinished execution
with already-uncertain submission state is lost, matching the accepted
redelivery duplicate-execution limitation.

A Runtime change or Reset creates a new physical Session and atomically replaces
the current binding without migrating context. Compact and changes to model or
variant keep the same Session file. Model and thinking level are execution
parameters: when reusing a Session, call `setModel()` and
`setThinkingLevel()` on the existing physical Session before the Prompt. They
do not trigger binding replacement.

Worktree-cleanup Follow-up behaves exactly as in `OpenCodeRuntime`: the executor
invokes the original task's resolved Action again, using the same Runtime and
physical Session, without replacing the binding.

## Prompt Execution

A Prompt requested by a Workflow Action adapter or AgentJob executor runs in
this order:

1. Parse the optional model string.
2. With no binding, create a physical Session. With a binding, restore it from
   cache or `SessionManager.open()`, then apply shared missing-recovery rules to
   choose either the original path or a newly rebound path.
3. Wait until the Session confirms that the current binding is persisted.
4. Record and persist this input using the confirmed Runtime Session ID.
5. Apply this execution's model and thinking level to the Session.
6. Call and await `session.prompt(text)`.
7. Project received events into AgentSession.
8. Read final text from the last assistant message in `session.messages`.
9. Return a normalized completion fact.

Resolution of `session.prompt()` means the entire Agent run, including tool
loops and automatic retries, has ended. It is the sole completion decision;
`agent_end` projects state but is not authoritative. `PiRuntime` does not
evaluate Workflow expectations or decide AgentJob success. The caller must
declare execution duration. For issue #450, the Workflow task executor supplies
a fixed 60 minutes through Runner-private Action context, invisible to and not
overridable by `mohist/pi` Action input. After open/bind, input reporting, and
model/thinking application, the Action passes the duration to `executePrompt`.
Immediately before `session.prompt()`, the Runtime reads the injected clock and
creates an absolute deadline. Queueing, binding, and input reporting consume no
Prompt budget. A cleanup Prompt is a separate execution with a fresh 60-minute
duration. The AgentJob executor's deadline belongs to its own Issue.

There is no transport timeout for this in-process call. The executor AbortSignal
and declared deadline are the single execution-deadline authority. At deadline,
the Runtime fixes the result as `deadline-exceeded` and then calls
`session.abort()` to close out. A late resolution of `prompt()` cannot reverse
that result. No failure automatically replays a Prompt with uncertain
submission state. Redelivery can duplicate execution inside the crash window;
this is the same accepted limitation as OpenCode.

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
[`agent-execution.md`](../agent-execution.md#activity-and-transcript). This
section defines only how Pi signals become those canonical facts.

`PiRuntime` maintains one `session.subscribe()` subscription for each active
`AgentSession` instance. Known events normalize into stable Mohist transcript,
tool, usage, model, status, and compaction facts:

- `message_start`, `message_update` (`text_delta`, `thinking_delta`,
  `toolcall_start` / `delta` / `end`), and `message_end` become transcript and
  tool facts;
- `tool_execution_start` / `update` / `end` become tool-execution facts
  correlated by `toolCallId`;
- assistant-message `usage`, including input, output, cacheRead, cacheWrite,
  thought, and cost, becomes usage facts;
- `compaction_start` and `compaction_end` become compaction facts;
- `auto_retry_start` and `auto_retry_end` become provider-retry facts as
  described below.

Projection is idempotent by Pi message ID and `toolCallId`. Unknown events enter
diagnostics only and do not change Workflow or Session state.

The event channel is an in-process callback, so it has no OpenCode-style
transport reconnection or snapshot reconciliation. Final execution state comes
from the resolved `prompt()` value and `session.messages`. Terminating the
Runner process terminates the event channel and current execution together; a
restart does not reconstruct a fiction that execution is still active.

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
  information and normalizes it to `execution-failed`.

When either unrecoverable rule matches, call `session.abort()`, confirm stop as
above, and return a failure fact containing the original provider message. Keep
the AgentSession and physical Session binding unchanged and do not suggest
Reset.

## Session Commands

The generic Session-command rules, including the distinction between
`notStarted` and `unavailable`, expected-current-binding checks, and stable
AgentSession IDs, match the Session Commands contract in
[`opencode.md`](opencode.md). Pi maps them to these channels.

### Follow-up

- While executing, use `session.steer(text)` to inject into the current
  execution. While idle, call `session.prompt(text, { preflightResult })`.
  The preflight callback is the point at which Pi confirms acceptance, and its
  RPC mode uses the same hook. Return preflight rejection, such as a missing
  model or credentials, to the user as command failure. Return immediately
  after acceptance; completion continues through Session events.
- Apply any selected current model / variant to the Session with `setModel()` /
  `setThinkingLevel()` before injection. Do not rotate the physical Session.
- An idle AgentSession performs shared binding preparation before accepting the
  input; if the binding path is definitely absent, create and persist a
  replacement first. An active or unknown AgentSession must not replace it.
- Routing or admission failure is returned to the user and is never retried or
  replayed automatically.

### Compact

Compact is allowed only while the logical Session is idle and shares the Reset
concurrency boundary. `session.compact()` performs native Pi compaction with
the Session's current model. It does not create a physical Session, change the
Session-file identity, or fall back to a Mohist synthetic summary. A Pi
compaction failure is explicit. Resulting compaction events continue to project
into the transcript.

### Reset

Reset is allowed only while the logical Session is idle. Read the current model
and thinking level if available, then call `SessionManager.create(cwd)` to
create an empty Pi Session in the same working directory. Replace the logical
Session's current binding with the new Session-file path only after creation
succeeds. AgentSession does not retain old bindings. The existing transcript
remains, while the new physical Session starts with empty context. If the old
path is already absent, skip model/thinking inheritance and continue creation;
other read failures remain explicit.

### Cancel

Call `session.abort()` for the current execution. `cancelled: true` means only
that the Runtime accepted and executed the interrupt request. Pi decides when
execution actually stops, and the Runtime reports the attempt exactly.

## Permissions, Project Trust, and Errors

Pi has no per-tool approval mechanism and provides no sandbox. Configured tools
execute directly with Runner-process permissions, with no interactive prompt in
headless mode. Pi therefore has no equivalent of OpenCode's
`permission.asked` -> one-time reply path, and `permission-required` is not a Pi
normalized error.

Pi's only approval-like concept is project trust: whether it loads project-level
`.pi/` resources such as settings, extensions, skills, and prompts from the
working directory. `PiRuntime` always constructs its `SettingsManager` with
`SettingsManager.create(cwd, agentDir, { projectTrusted: false })` and passes
that same manager plus explicit `cwd` and `agentDir` to
`DefaultResourceLoader` and `createAgentSession`. Executable resources under a
repository's `.pi/` directory never enter execution, so a work repository
cannot alter Runner behavior by carrying Pi configuration. Root `AGENTS.md` and
`CLAUDE.md` files are unrelated to project trust and remain model context, as in
OpenCode: they affect Prompt context, not Runner execution configuration. The
Runner user's global configuration under `~/.pi/agent` loads normally. This is
not configurable; it is a determinism guarantee for unattended execution.

The Pi boundary reuses Runner credential masking. Before SDK or provider text
enters a task log, diagnostic, or Runtime event, it is redacted. Structured
requests/results and Runner registration use Mohist field allowlists rather
than serializing SDK objects. Action output contains no diagnostic. A real
smoke artifact records only versions, operation names, Boolean results, and a
redacted field-name/type summary. It does not record environment values, auth
files, raw provider responses, Prompts, or message bodies.

At the `PiRuntime` boundary, normalize SDK errors to the small set of Mohist
kebab-case wire results: `invalid-input`, `unavailable-runtime`,
`missing-session`, `incompatible-runtime`, `deadline-exceeded`, `interrupted`,
and `execution-failed`. Provider-specific details remain diagnostics and never
become Action output fields.

## Model Catalog

Load the model catalog through `modelRuntime.getAvailable()`. It returns only
models from providers with configured credentials, which is exactly the
configuration-assistance meaning required. Each catalog model's variants are
Pi thinking levels. Runner registration reports the Pi and OpenCode catalogs
side by side, grouped by Runtime in Server and Web. When model is omitted, use
the Session's current choice or Pi default; Pi still makes the final validity
decision for a selected model.

## Server and Web Touchpoints

Pi is the second Runtime, so these existing single-Runtime assumptions must be
generalized without changing product contracts:

- Server Runtime registry: `AgentSessionGrain` method `IsRuntimeRegistered`
  registers `"pi"`; Reset fallback for an unregistered historical Runtime stays
  unchanged.
- Agent launch: `AgentLauncher` reads the execution backend from Agent
  configuration instead of hard-coding `"opencode"`; the backend is fixed into
  AgentJob input with the Agent snapshot.
- AgentJob executor: dispatch by the Runtime carried in dispatch to
  `OpenCodeRuntime` or `PiRuntime`. Both paths share Session infrastructure but
  not Runtime instances.
- Runner open / attach write-back: use the Runtime resolved by the caller
  instead of hard-coding one.
- Session usage: add independent `cachedWriteTokens` across
  `AgentUsageSummary`, grain state and surrogate, Runtime event parser, API/read
  model, and shared Web types. Append new Orleans field IDs without reordering;
  absent values mean null/0 and are accumulated separately from
  `cachedReadTokens`.
- TaskRun classification: classify `mohist/pi` as UserFacing, like
  `mohist/opencode`.
- Model catalog API: generalize the OpenCode-only route to query by Runtime, or
  add a parallel Pi route.
- Session command handlers for Follow-up, Cancel, Compact, and Reset: route by
  the AgentSession's current binding Runtime.
- Runner host: construct and start `PiRuntime`; inject execution capability into
  the Workflow Action through the manifest's `agent-execution` capability and
  inject the Runtime into `AgentJobExecutor`; drive promise projection by
  capability. If #450 lands before capability-narrowing issue #447, it will
  temporarily retain the current Runtime-bearing `ActionContext` and named
  projection mechanism. That is an implementation gap explicitly owned by
  #447, not the target interface in this design.
- Web: add execution backend to Mohist Agent editing and Issue model selection;
  list models for the selected backend from the OpenCode or Pi catalog.

## Tests

Default tests must not start real Pi or use a real process, network, filesystem
configuration, or clock. All SDK dependencies remain inside the `PiRuntime`
module and are replaced through a factory seam with a fake `PiRuntime` or fake
SDK factory. Tests drive events, completion state, process termination, and
errors deterministically.

Coverage includes at least:

- Action input expansion with no hidden `vars.agent` fallback;
- ignored and diagnosed unknown `options` keys, including `runtime`, without
  execution failure;
- model strings containing multiple `/` characters, with variant independent
  and mapped to thinking level;
- shared Runtime code for Workflow and AgentJob execution without shared work
  or Session identity;
- physical Session reuse and rotation invariants, including no rotation on
  model or thinking-level change;
- binding restoration through cache hit, lazy open, and exactly one create for
  a definitely absent path, with expected-binding replacement persisted before
  input or Prompt;
- no create for file corruption, permission failure, or unclassified open
  error, and no Prompt submission through a stale binding;
- a non-bound Runner neither opens nor replaces the Session file, and its local
  absence does not trigger create;
- Prompt completion, interruption, uncertain submission, and no replay;
- `steer` for active Follow-up and deadline warning, idle Follow-up with missing
  recovery, native Compact, Reset including a missing old path, and stale
  binding rejection;
- assembly with `projectTrusted: false`, proving project-level `.pi/` resources
  do not enter execution;
- provider policy for immediate pattern failure, threshold failure, and Pi's
  own unrecoverable-error normalization to `execution-failed`;
- two-phase closeout with one steer warning, immediate warning for deadlines
  under five minutes, abort at deadline, and no abort after an early normal end;
  all driven by a fake clock;
- minimal `{ promise }` Workflow Action output with existing expectation
  semantics.

## Upstream Boundary

Pi is a rapidly evolving 0.x dependency, with roughly one minor release each
week. Breaking changes concentrate in creation and service assembly, including
scope migration, Runtime assembly refactoring, and parameter-type changes; the
event protocol is relatively stable. The response is to:

- lock the SDK package version, read every Breaking / Changed entry in the
  CHANGELOG during upgrades, and run an integration smoke test;
- keep all SDK access inside `PiRuntime`, so upgrade drift changes one deep
  module;
- treat `@earendil-works/pi-coding-agent` 0.80.10 as the reference version when
  this table was written, then lock and smoke-test again at implementation time
  as required by SDK Surface.

## Implementation Gaps

Both Workflow and AgentJob paths are implemented: `PiRuntime`, the `mohist/pi`
Action, AgentJob Runtime selection, Runtime-aware Session binding, the model
catalog API and Web selector, and the existing Session transcript, tool,
status, compaction, model, usage, and cost views.

A missing Pi Session file still produces `missing-session` directly. A new
independent input does not yet perform
`definitely-missing -> create -> expected binding replacement`. An
implementation Issue still needs to be created from this specification.
