## Why

The AgentSession domain already separates logical identity from the physical/runtime binding, but the command, API, and CLI layers still leak the old "rotate the session id" model: Compact and Reset mint a fresh id and rebind, their responses and CLI help advertise a "new session id", agent-launched sessions lack compact/reset entirely, and the wire still carries `acpSessionId`. So the same logical conversation looks like different sessions across sources and across recovery operations, and a backend replacement cannot tell the user a Reset is needed. We need a single canonical AgentSession resource with a stable identity so users never lose their conversation, audit trail, or orientation — regardless of entry point or runtime replacement. This is the prerequisite command-and-routing contract for the ACP→OpenCode backend swap (#409).

## What Changes

- Establish AgentSession as the single canonical logical session resource identified by a stable sessionId; WorkflowRun+sessionName and Agent ID become lookup-only keys, never identity.
- Each AgentSession persists exactly one immutable source (Workflow or Agent launch), plus the minimal current Runtime Session binding (`runtime`, `runtimeSessionId`, `runnerId`, `workDir`) and an append-only lineage.
- **BREAKING**: Compact no longer rotates the runtime binding — it keeps the current Runtime Session and only records the compaction.
- **BREAKING**: Reset requests a replacement Runtime Session and updates the binding only when the command's expected current binding is still current (stale-binding guard); it never rotates the AgentSession ID.
- **BREAKING**: Compact and Reset API/CLI responses return the same stable `sessionId`; the "returns a new session id" wording in `mo issue session compact/reset` help is removed.
- Compact and Reset share one concurrency boundary: both execute only when the logical session is idle and return a conflict while a work turn is active.
- Follow-up joins the active turn when the session is busy and starts a user-initiated turn when idle — without creating a new TaskRun or AgentJob.
- Cancel interrupts only the current turn and never deletes the AgentSession.
- Both sources expose the same operations: `mo agent session` (named agent) gains `compact` and `reset`, sharing the canonical routing and product semantics with the Workflow commands.
- When the current Runtime Session does not exist (e.g. after an ACP→OpenCode replacement of a legacy binding), session operations fail explicitly and prompt Reset — no synthetic continuous conversation is fabricated.
- Canonical wire representation uses `runtime` + `runtimeSessionId`; `acpSessionId`/`coderSessionId` are removed across server DTOs, runner payloads, and web.
- Legacy AgentSessions and historically rotated session records remain queryable and auditable; no stored data is rewritten.
- Out of scope (#409): the actual OpenCode SDK calls (`promptAsync`, `summarize`, `abort`, new session creation). This issue defines the command contract and routing they must satisfy.

## Capabilities
<!-- Each capability gets a specs/<name>/spec.md describing required behavior. -->
- `agent-session-identity`: The canonical AgentSession logical resource — stable sessionId as identity; immutable single source (Workflow via WorkflowRun+sessionName, Agent launch via Agent ID) used only for lookup; persistent minimal current Runtime Session binding + append-only lineage; canonical wire representation (`runtime` + `runtimeSessionId`) replacing `acpSessionId`; legacy data remains queryable without rewrite and legacy bindings surface as "runtime session missing".
- `agent-session-commands`: Session command semantics and the shared concurrency boundary — Compact (idle-only, keeps current Runtime binding), Reset (idle-only, requests a replacement Runtime Session and applies it under an expected-binding guard), Follow-up (joins the active turn or starts a user-initiated idle turn, creating no TaskRun/AgentJob), Cancel (interrupts the current turn only, never deletes the AgentSession). Commands route through a Mohist-owned request/result shape independent of Workflow Action Input or Agent definitions. A missing current Runtime Session fails explicitly with a Reset hint.
- `agent-session-command-surface`: The unified API and CLI command surface across both sources — Workflow and Agent-launch entries resolve through the same canonical routing; named-agent CLI gains compact/reset; recovery responses return the same stable sessionId with no id rotation; help text and error wording reflect the stable-identity model.

## Impact

- **Server** (`packages/server/src/Mohist.Server/`):
  - Domain/transitions: `Sessions/Domain/AgentSession.cs`, `AgentSession.Transitions.cs` — Compact stops rebinding; Reset replacement under expected-binding guard; lineage semantics.
  - Grain: `Sessions/Grains/AgentSessionGrain.cs` (`CompactAsync`/`ResetAsync`/`ApplyRecoveryTransitions`/`EnsureSessionIdleForRecovery`) — split compact (no rebind) from reset (guarded replacement); return stable sessionId; accept expected-binding parameter.
  - Commands: `IAgentSessionGrain.cs` (`CompactAgentSessionCommand`/`ResetAgentSessionCommand`) — carry expected current binding; drop id rotation on compact.
  - Routes: `Api/IssueRoutes.Sessions.cs` (remove `BuildNewAgentSessionId` rotation on compact; reset no longer mints a client id), `AgentSessionFollowupRoutes.cs`, `AgentSessionCancelRoutes.cs` — add generic compact/reset HTTP routes; missing-runtime-session error + Reset hint.
  - Read models/wire: `AgentSessionReadModels.cs`, `RunnerRoutes.cs` — `acpSessionId` → `runtimeSessionId` (+ `runtime`).
  - Source metadata/resolver: `WorkflowAgentSessionMetadata.cs`, `GenericAgentSessionMetadata.cs`, `AgentSessionResolver.cs` — already source-tagged; enforce canonical lookup contract.
- **Runner** (`packages/runner/src/`): `server/followup-handler.ts`, `cancel-handler.ts` drop `acpSessionId`, use `runtimeSessionId`; the runner command contract (expected binding in/out, missing-session error) lands here, while the actual SDK `summarize`/new-session/`abort` calls are #409.
- **CLI** (`packages/cli/Mohist.Cli/`): `MohistCliCommands.Agent.cs` — add `mo agent session compact|reset`; `MohistCliCommands.Issue.Session.cs` — remove "new session id" help/output; both share canonical routing. Update `CliIssueSessionSpecs.cs` assertions.
- **Web** (`packages/web/src/`): `acpSessionId`/`coderSessionId` references migrated to `runtimeSessionId`; recovery action UI consumes the stable sessionId response.
- **Docs**: `docs/agents.md`, `docs/cli-reference.md` (compact/reset gap note), `design/agent-execution.md`/`design/runtimes/opencode.md` 实装差距 — align with landed behavior.
- **Dependencies**: none added.
- **Tests**: server spec/unit for compact-keeps-binding, reset expected-binding guard, idle conflict, follow-up active/idle, cancel-no-delete, missing-runtime-session error, and source lookup parity; runner contract tests; CLI spec updates.
- **Risk (high)**: this changes persisted session identity, source lookup, API/CLI command shape, and runtime binding semantics. A bad migration could make existing sessions uncontinuable or merge unrelated sessions. Mitigated by defining the contract first (this proposal), keeping SDK mechanics in #409, never rewriting stored data, and treating legacy bindings as explicit "reset needed" failures.
