## Context

The server already exposes four per-session management endpoints under
`/api/projects/{p}/issues/{n}/sessions/{name}/*` (metadata, transcript, compact, reset),
plus the existing list endpoint `/{n}/coder-sessions`. The CLI today only wires the list
command (`mo issue sessions`). An agent running a long task cannot self-manage its context
window — it cannot compact near the limit, reset when context is polluted, or read a
transcript to resume/review — because the CLI verbs are missing.

This change is **pure CLI wiring**: no server endpoint, domain model, or DTO changes. All
four endpoints and the `session_active` conflict semantics already exist in
`IssueRoutes.Sessions.cs` and are covered by `AgentSessionRecoveryApiSpecs`.

Relevant server-side shapes the CLI must consume:

- **Metadata** (`AgentSessionMetadataDto`): `id`, `sessionName`, `acpSessionId`, `status`,
  `model`, `stage`, `title`, `createdAt`, `completedAt`, `eventSummary`, `usage`
  (incl. `contextWindowUsed/Size`, `contextUsagePercent`, `healthStatus`), `metadata`
  (`partCount`, `toolCount`).
- **Transcript** (`AgentSessionTranscriptResponse`): `turns[]` (each with `startedAt` /
  `completedAt` and nested `assistant` parts), `partCount`, `lastActivityAt`.
- **Recovery result** (`AgentSessionRecoveryResult`): `id`, `agentSessionId` (the **new**
  follow-on runtime session id), `status`, `contextWindowSize`, `contextWindowUsed`,
  `contextUsagePercent`, `contextWindowUsedBefore`, `operation`, `wasCompacted`.

All server responses use the standard envelope `{ success, data, error, code }`. The 409
conflict is emitted as `ApiResults.Conflict(..., "session_active")` →
`{ success:false, error:"Cannot compact while session is active", code:"session_active" }`.

## Goals / Non-Goals

**Goals:**

- Add a `mo issue session` (singular) command group with `show`, `transcript`, `compact`,
  `reset` verbs that drive the four existing endpoints.
- Surface the new follow-on session id (`New session: <id>`) for compact/reset so an
  agent/user knows the subsequent session identifier.
- Render a **summary** in `-o table` for the potentially long transcript (part/turn count,
  first/last activity) rather than dumping every message.
- Surface HTTP 409 `session_active` (code + message) for compact/reset — never silently
  succeed when the server rejected the operation.
- Preserve `mo issue sessions <num>` (plural, list) unchanged.

**Non-Goals:**

- No server-side changes (endpoints, domain, DTOs all exist).
- No session create/delete CLI (sessions are workflow-managed).
- No changes to the `coder-sessions` alias or attachment/metrics CLIs.

## Decisions

### Decision 1: New singular `session` command group, parallel to existing `sessions`

Add a `session` (singular) subcommand under `mo issue` with four child verbs
(`show`/`transcript`/`compact`/`reset`). The existing plural `sessions` (list) command is
left untouched and remains the documented source of the `<name>` positional argument.

- **Rationale**: Keeps the established list command stable (no behavior change, no alias
  churn) while giving the four verbs a clean, discoverable namespace. The
  singular/plural split mirrors the resource model: `sessions` = collection (list),
  `session <verb>` = operate on one named session.
- **Alternative considered**: Flatten verbs directly under `issue` (e.g.
  `mo issue compact-session`). Rejected — it pollutes the `issue` namespace and hurts
  discoverability/help output.
- **Alternative considered**: Move the list under the new group
  (`mo issue session list`). Rejected — the issue explicitly requires the existing
  `mo issue sessions <num>` to behave identically.

### Decision 2: Reuse the existing `PrintPostWithOutputAsync` / `PrintWithOutputAsync` pipeline (no new POST-error plumbing)

Route all four verbs through the existing helpers:
- `show` / `transcript` → `api.PrintWithOutputAsync(path, mode, tableShape)`.
- `compact` / `reset` → `api.PrintPostWithOutputAsync(path, body, mode, tableShape)` (POST
  with an empty body object, matching the server's `content: null` contract).

Both funnel through `PrintEnvelopeAsync`, which already:
- Parses the `{ success, data, error, code }` envelope.
- On non-success (incl. 409): writes `error (code)` to **stderr** and returns non-zero
  (1 for 409, 4 for 404) — via `PrintResponseAsync`.
- On success + JSON mode: emits the raw `data` JSON.
- On success + table mode: dispatches to `TableRenderer` by shape.

This means the **load-bearing 409 `session_active` passthrough is already satisfied** by
existing infrastructure — the only requirement is that we do *not* short-circuit
non-success before the envelope parser runs.

- **Rationale**: The proposal flagged "ensure the API layer can issue POST and surface
  structured error". Tracing `MohistCliApi.cs:476-537` shows this already works for any
  non-success envelope. Building a dedicated conflict-aware POST method would duplicate
  `PrintResponseAsync`'s error path for no behavioral gain.
- **Alternative considered**: Add a `PostAndReadAsync`-style path returning `PostResult`
  so the command inspects `code == "session_active"` explicitly. Rejected — it adds
  branching without changing what the user sees (the generic path already prints
  `Cannot compact while session is active (session_active)` to stderr with exit 1).

### Decision 3: Three new `TableShape`s — `SessionMetadata`, `SessionTranscriptSummary`, `SessionRecovery`

Add three entries to the `TableShape` enum and three `Render*` methods in the
`TableRenderer` partial (mirroring the existing `Sessions` shape in
`TableRenderer.Issues.cs`):

- **`SessionMetadata`** (`show`): key/value block — name, status, model, stage, created
  time, part/tool counts, and context-window usage (used/size + percent + health).
- **`SessionTranscriptSummary`** (`transcript`): summary block — turn count, part count,
  first activity (first turn's `startedAt`), last activity (`lastActivityAt`). Deliberately
  does **not** iterate every part/message.
- **`SessionRecovery`** (`compact`/`reset`): prints `New session: <agentSessionId>` as the
  prominent line, then `operation`, `wasCompacted`, context-window before/after
  (`contextWindowUsedBefore` → `contextWindowUsed`), `contextUsagePercent`, `status`.

In all cases `-o json` bypasses the renderer and emits the raw server payload (handled by
the shared pipeline).

- **Rationale**: Each server payload has a distinct, stable shape; a dedicated renderer per
  verb keeps the table output meaningful and avoids generic key-dumping. The `New session:`
  line is the one output an agent parses, so it must be a fixed prefix string.
- **Alternative considered**: One generic "object pretty-printer" table shape. Rejected —
  the transcript in particular must be summarized, not dumped, and compact/reset need the
  `New session:` affordance.

### Decision 4: Compact/reset POST with an empty body

The compact/reset endpoints expect no body (`POST ... content: null`). Pass an empty
anonymous object (`new { }`) to `PrintPostWithOutputAsync`, which serializes to `{}`.
The server ignores the body for these routes.

- **Alternative considered**: Send `HttpMethod.Post` with truly null content via a custom
  helper. Rejected — `PostAsJsonAsync` with `{}` is already used elsewhere and the server
  route never reads the body.

### Decision 5: Path construction reuses `ProjectIssuesPath` + escaped segments

Build paths as
`ProjectIssuesPath(resolvedProjectId, $"/issues/{Escape(number)}/sessions/{Escape(name)}[/verb]")`,
matching how the existing `BuildSessions` builds the `coder-sessions` path. `Escape` is the
existing helper used across issue subcommands.

## Risks / Trade-offs

- **[`agentSessionId` field naming] -> document the source clearly** — The recovery
  payload's `agentSessionId` is the *new follow-on* runtime id, distinct from the logical
  `id`. The `New session:` line must read `agentSessionId`, not `id`. The renderer and the
  help text will call this out to avoid wiring the wrong field.
- **[409 only surfaces via stderr exit code, no machine-readable flag] -> acceptable** —
  The conflict is surfaced as `error (code)` on stderr with exit 1. An agent must check the
  exit code / stderr text; there is no dedicated `--exit-on-conflict` flag. This matches
  every other error path in the CLI and keeps the surface minimal. Adding a distinct exit
  code for `session_active` is a possible follow-up but out of scope.
- **[Transcript table summary loses detail by design] -> point users at `-o json`** —
  Table mode intentionally summarizes. The summary will be documented as a preview; full
  data is one `-o json` away.
- **[Server-side shape drift] -> low risk, mitigated by JSON mode** — The CLI renders
  specific fields defensively (missing → blank). If the server adds fields, table output
  is unaffected; if it removes fields, only the table degrades while `-o json` still
  reflects reality.

## Migration Plan

No data migration, schema change, or server deployment is involved.

**Deploy:**
1. Add the `session` command group + four verbs in `MohistCliCommands.Issue.cs`.
2. Add the three `TableShape` entries + renderers.
3. Rebuild the CLI (`mo update` / `dotnet build`).
4. Verify against a running server: `mo issue sessions <n>` (unchanged), then
   `mo issue session show/transcript/compact/reset <n> <name>`.

**Rollback:** Revert the CLI changes; the server is untouched so no rollback is needed
there. The new `session` group is additive — removing it has no effect on existing
commands.

## Open Questions

- None blocking. The server contract, DTO shapes, envelope, and conflict semantics are all
  confirmed against `IssueRoutes.Sessions.cs`, `AgentSessionReadModels.cs`, and
  `AgentSessionRecoveryApiSpecs`.
