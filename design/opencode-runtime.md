# OpenCode Runtime

## Decision

`mohist/opencode` is a runtime-specific Action implemented directly against
`@opencode-ai/sdk/v2`. A Workflow that uses it directly performs Inline Agent execution;
the Action itself is not an Agent and never resolves a Mohist Agent. The full concept and
lifecycle model is defined in [`agent-execution.md`](agent-execution.md).

The ACP adapter is removed rather than retained as a fallback. Existing AgentJob execution
must also leave ACP and use the same `OpenCodeRuntime` capability through its own Agent-owned
executor, not through the Workflow Action contract. This design does not define a
`mohist/agent` Action or redesign the Mohist Agent product.

OpenCode is the only runtime implemented now. Pi may add another action later, but this
change does not introduce a generic `AgentRuntime` interface in anticipation of it.
The stable boundaries are the Workflow Action contract, the AgentJob execution contract,
and Session commands, not a speculative cross-runtime SDK wrapper.

## Direct Action contract

```ts
type OpenCodeActionInput = {
  prompt: PromptSpec
  session?: string
  options?: {
    agent?: string
    model?: {
      providerID: string
      id: string
      variant?: string
    }
  }
}

type OpenCodeActionOutput = null | {
  promise: string
}
```

The existing prompt resolver turns `prompt` into a non-empty string before entering the
runtime. `options` mirrors the v2 Session fields Mohist actually uses. OpenCode v2 exposes
agent and model switching on a Session; tool, plugin, permission, and automatic compaction
policy remain in native OpenCode configuration.

There is no `kind` or `type`. `uses: mohist/opencode` selects the runtime. The action never
reads Workflow variables itself: `TaskWithExpander` must render `options: ${{ vars.agent }}`
to an object before dispatch, and the rendered Action Input is the sole execution fact.

On a new physical Session, `agent` and `model` are passed to v2 Session creation. On an
existing physical Session, specified changes use `switchAgent` and `switchModel` before the
next Prompt. Omitted options preserve the current Session selection; on first creation they
allow OpenCode defaults. A model change, including `variant`, does not rotate the physical
Session.

The Runner's Workflow task executor receives `expect` and artifact declarations separately
from `OpenCodeActionInput` and applies them after the OpenCode Action returns its turn result.
It exposes only the matched promise value as Action Output when present. Runtime identity,
transcript, model, usage, diagnostics, and expectation details belong to their existing
state/read models rather than the action output.

## Deep module boundary

`OpenCodeRuntime` is a Runner-internal deep module. It owns:

- OpenCode Server and Client lifecycle
- readiness and model catalog
- physical Session creation, lookup, reuse, and interruption
- Prompt admission, waiting, follow-up, compact, and reset
- event subscription, durable-history reconciliation, and event normalization
- OpenCode error and compatibility diagnostics

The `mohist/opencode` Action, AgentJob execution adapter, and Session command handlers depend
on Mohist-owned request and result types. They never expose generated SDK types. The runtime
receives an already composed turn input and Session binding; it never accepts Agent ID/name
or loads a Mohist Agent definition. All ordering rules, reconnection rules, and OpenCode
error interpretation remain inside this module.

This is intentionally not a thin method-for-method SDK wrapper. Callers ask for Mohist
capabilities such as run turn, follow up, compact, and reset; the module decides which v2
operations and reconciliation steps make each capability complete.

## Process topology and readiness

Each Runner process owns one OpenCode Server and one Client shared by all OpenCode Sessions.
Use the official `createOpencodeServer()` and `createOpencodeClient()` APIs. Do not spawn or
parse the OpenCode process directly, and do not create a directory-scoped Client per action.

Every physical Session is created with:

```ts
location: { directory: workDir }
```

The physical Session's directory is immutable in Mohist. A work directory change creates a
new physical Session instead of using the v2 move API.

Before a Runner registers or claims work, it must:

1. start the shared OpenCode Server;
2. pass v2 health;
3. load the model catalog successfully.

If the OpenCode Server dies, the Runner stops claiming new work and rebuilds the Server,
Client, and event subscription. An in-flight turn affected by the loss fails; it is never
automatically replayed. A recovered Runner becomes ready only after health and catalog
checks pass again.

The SDK package is pinned in Mohist. The OpenCode CLI is supplied by the installer. Mohist
does not install, update, or enforce an exact CLI version match; incompatible API behavior
must produce an actionable readiness error. Native workspace configuration and plugins are
loaded normally; there is no `--pure` mode or `.opencode` lockfile cleanup.

## Session binding

AgentSession ownership and origins are defined in
[`agent-execution.md`](agent-execution.md); runtime identity field names are defined in
[`conventions.md`](conventions.md). `OpenCodeRuntime` receives an already resolved logical
Session target and must not create or change its origin.

Workflow-owned work resolves the target from WorkflowRun plus session name, defaulting the
name to Work ID. AgentJob-owned work receives the minted AgentSession ID from dispatch.

Reuse the current OpenCode Session only when runtime and work directory still match. Runtime
change, work directory change, and Reset create a new physical binding and append lineage;
no context is migrated. Compact, model/variant changes, and OpenCode runtime-agent changes
keep the same physical Session ID.

Only one work-owned Prompt may execute at a time per logical AgentSession, whether its owner
is TaskRun or AgentJob. Different logical Sessions may execute concurrently. User follow-up
is a special active-turn input and is not blocked behind that mutex when it can be steered
into the active turn.

## Turn execution

A turn requested by either the Workflow Action adapter or AgentJob executor follows this
sequence:

1. resolve or create the current physical Session;
2. apply specified agent/model changes;
3. call `client.v2.session.prompt()` with `delivery: "queue"`;
4. treat the returned admission as acceptance, not completion;
5. call `client.v2.session.wait()`;
6. reconcile durable history and projected messages;
7. return normalized completion facts to the caller.

`OpenCodeRuntime` does not run Workflow expectations or decide AgentJob success. The Workflow
task executor applies `expect`, artifacts, `failIf`, Action Output, and recovery semantics
after the Action returns. The AgentJob executor validates and reports its own result through
the Agent-owned contract.

SSE silence is not a failure and an idle event is not the sole completion authority. The
authoritative completion path is `wait()` followed by durable reconciliation. The existing
caller abort signal is the only execution deadline. On abort, call
`client.v2.session.interrupt()` and return an interrupted result to the caller.

Startup and readiness operations may retry within the Runner lifecycle. Prompt submission
and any response with uncertain admission are never blindly retried. The existing in-process
dispatch deduplication remains; crash-window duplicate execution after redelivery is an
accepted limitation and does not add deterministic Prompt IDs or pre-query idempotency.

## Events and reconciliation

The Runner maintains one v2 global event subscription for the shared OpenCode Server.
`OpenCodeRuntime` routes Session events by `sessionID` and location. Known typed events are
normalized into Mohist's stable transcript, tool, usage, model, status, and compaction facts.
Unknown OpenCode events are diagnostic only and do not alter Workflow or Session state.

Live events optimize display latency. Durable v2 Session history is the recovery source:

- retain OpenCode event ID plus durable aggregate sequence when available;
- make Server writes idempotent on those source identities;
- after reconnect and before final completion, page `session.history(after)` until caught up;
- read projected Session messages when the final user-visible message or failure must be
  confirmed.

This keeps event loss and duplicate delivery out of callers. Workflow success is decided by
the Workflow task executor from the Action result followed by Mohist expectation, artifact,
`failIf`, and recovery semantics. AgentJob completion is decided separately by its executor.

## Session commands

Session commands are request/reply operations from Web or CLI through Server to the Runner.
The persisted runtime binding is the routing fact; an in-memory Runner cache is only an
optimization.

### Follow-up

- If the Session is active, submit with `delivery: "steer"`.
- If it is idle, submit with `delivery: "queue"`.
- Inherit the current OpenCode agent and model unless the Session itself was changed first.
- Return success only after OpenCode admits the input.
- Surface admission or routing failure to the user; do not fire-and-forget or replay.

### Compact

Call `client.v2.session.compact()` for the current physical Session. The command does not
accept a model argument, does not create a new physical Session, and has no Server-side
synthetic-summary fallback. Reconcile resulting compaction events into the transcript.

### Reset

Reset is allowed only while the logical Session is idle. Create a new empty OpenCode Session
at the same work directory, preserving the latest agent/model selection. Rebind the logical
Session only after creation succeeds and append the new physical binding to lineage. The old
Session remains queryable for audit but contributes no context to the new Session.

Each command carries the expected current binding. Server applies a returned replacement
only if that binding is still current, preventing stale Reset results from overwriting a
newer binding. A missing OpenCode Session is an explicit error with Reset as the recovery;
it never triggers an implicit replacement.

## Permissions and errors

OpenCode's native permission configuration is authoritative. Mohist does not auto-allow
requests and does not translate OpenCode permission prompts into Workflow Approval. If a
headless turn emits an unresolved interactive permission request, interrupt the turn and
return an actionable failure.

Normalize SDK errors at the `OpenCodeRuntime` boundary into a small Mohist-owned result:
invalid input, unavailable runtime, missing Session, incompatible runtime, permission
required, interrupted, and turn failed. Keep provider-specific detail as diagnostics rather
than action output fields. Do not create a global Workflow error enum; each caller reports
the failure through its owning TaskRun or AgentJob contract.

## Model catalog

Load models through `client.v2.model.list()` and report the structured provider/model/variant
catalog in Runner registration. The Server and Web expose that catalog for configuration,
but it is advisory. An omitted model uses OpenCode's default; OpenCode remains the final
validator for a selected model.

## Testing

Default tests never start OpenCode or use a real process, network, filesystem configuration,
or clock. Inject a fake `OpenCodeRuntime` or fake generated Client/Server factory and drive
events, history pages, wait completion, process loss, and errors deterministically.

Coverage must include:

- Action Input rendering and absence of a hidden `vars.agent` fallback
- Workflow-owned and AgentJob-owned turns sharing runtime code without sharing work/session identity
- physical Session reuse and rotation invariants
- model/agent switching without rotation
- global event routing, duplicate suppression, and history backfill
- Prompt admission, wait, interruption, and no-replay behavior
- follow-up, native compact, Reset, restart routing, and stale-binding rejection
- permission, missing Session, compatibility, and process-loss failures
- minimal `{ promise }` Workflow Action Output and existing expectation semantics

## Full replacement

The implementation change removes, rather than deprecates:

- `@agentclientprotocol/sdk`
- `mohist/acp-agent` and the ACP action tree
- shared ACP connection/session management
- ACP liveness probes and their configuration
- OpenCode log scanning and CLI model parsing
- ACP private compaction metadata and synthetic Session rebinding
- `.opencode` lockfile cleanup
- all `acpSessionId` wire, Server, and Web terminology

Builtin workflows switch atomically to `mohist/opencode` and
`options: ${{ vars.agent }}`. Existing AgentJob dispatch removes its hardcoded
`mohist/acp-agent` Action name and carries an Agent-owned OpenCode execution request after
Agent launch has composed the Agent snapshot and prompt. Its executor calls
`OpenCodeRuntime` directly. This does not introduce `mohist/agent`. There is no feature flag,
compatibility alias, or ACP fallback.

## Accepted upstream risk

At the decision point, `@opencode-ai/sdk` 1.17.18 formally exports `./v2` and provides the
Session APIs required here: location-aware create, agent/model switching, durable Prompt
admission, steer/queue delivery, wait, history, messages, compact, interrupt, and typed
events.

The choice is intentionally ahead of OpenCode's public SDK documentation and client
transition. Upstream still tracks the V2 API surface as incomplete in
[Port ACP support to V2 core and APIs](https://github.com/anomalyco/opencode/issues/35457),
and is moving callers from the legacy SDK client toward a generated client in
[Track TUI migration to @opencode-ai/client](https://github.com/anomalyco/opencode/issues/34359)
and [generate complete protocol client](https://github.com/anomalyco/opencode/pull/34143).

Mohist accepts that churn to obtain the v2 Session semantics directly. SDK access remains
inside `OpenCodeRuntime` so a later move to `@opencode-ai/client` changes one deep module,
not Workflow actions or Session product contracts.

## Implementation gap

The current Runner still uses `@agentclientprotocol/sdk`, `mohist/acp-agent`, ACP liveness
and log heuristics, CLI model parsing, private compaction metadata, and `acpSessionId` wire
fields. Current Compact synthesizes Mohist-side transcript state and physical rebinding, and
the Workflow schema still embeds `expect` inside `with`. These paths are implementation debt
against this target design and must be removed in the same replacement change.
