## Context

A launch already creates two entities — an `AgentJob` (work owner) and an `AgentSession` (conversation owner) — but only the Session identity is surfaced. This change adds the missing AgentJob read surface and unifies the Session conversation under a source-independent command. The proposal establishes the why; the specs establish the required behavior. This document covers how.

Current state (key facts, with sources):

- **Launch drops the job identity.** `AgentLaunchResult(SessionId, AgentId, AgentName)` carries no job key (`IAgentLauncher.cs:121`). The manual-launch key `agent-job-launch-{guid}` is minted at `AgentLauncher.cs:124` and discarded; the mention path (`LaunchMentionAsync`, `AgentLauncher.cs:292`) discards its `CommentJobKey` the same way. Only the routed path returns a key (`RoutedAgentLaunchOutcome.JobKey`, `IAgentLauncher.cs:134`). The HTTP `201` body `AgentSessionLaunchResponse(SessionId, AgentId, AgentName, Status, TranscriptUrl)` (`AgentSessionLaunchRoutes.cs:203`) therefore has no `jobId`.
- **AgentJob has no queryable store.** `AgentJobState` is persisted only via `[PersistentState("agent-job")]` Orleans grain storage (`AgentJobGrain.cs:57`), backed by the opaque ADO.NET/SQLite `OrleansStorage` table keyed by grain hash — not queryable by `AgentId`. There is no `AgentJobRow`, no `DbSet`, no `*Store`, no `*Querier` (confirmed by exhaustive search). The grain does expose read methods — `GetStatusAsync`, `GetTerminalResultAsync`, `GetRuntimeSnapshotAsync` (`IAgentJobGrain.cs:10,18,19`) — but no product HTTP GET reads them. The only job-result HTTP surface is the validation-only `POST /api/agent-jobs/validate` smoke test (`AgentJobController.cs:31`).
- **AgentJobTerminalResult** is `{ Status, Message, Output, ArtifactUploadIds, FailureReason, ExitCode }` (`IAgentJobGrain.cs:250`); `AgentJobStatus` is `Pending/Running/Completed/Failed`.
- **The AgentSession pattern to mirror.** `AgentSessionRow` (JSON `State` blob + indexed computed columns) + `AgentSessionStore` (the grain writes the row synchronously on every transition — `AgentSessionGrain.cs:114`; the relational store *is* the persistence, there is no `[PersistentState]`) + `AgentSessionQuerier` (reads rows directly). The agent-scoped recency list leans on a `LabelAgentId` computed-column index (`MohistDbContext.cs:196`).
- **The generic-session read route excludes workflow sessions.** `GET .../agent-sessions/{sessionId}` calls `GetGenericSessionSummaryAsync` → `FindGenericSessionAsync`, which requires `source-kind == "agent-launch"` (`AgentSessionQuerier.cs:601-611`) and returns 404 for workflow-originated sessions. Yet workflow sessions *do* carry a stable `Id` (`AgentSessionSummaryDto.Id`, `WorkflowSessionDto.Id`), and list-by-source routes already exist: `GET /agents/{agentId}/sessions` (`AgentSessionListRoutes.cs:23`), `GET /issues/{n}/coder-sessions` (`IssueRoutes.Sessions.cs:15`), `GET /api/workflow-runs/{runId}/sessions` (`WorkflowSessionRoutes.cs:11`). The follow-up/cancel routes already handle both sources (`AgentSessionFollowupRoutes` resolves canonically by id).
- **CLI command tree.** Root assembled in `MohistCliCommands.Build` (~22 top-level subcommands); there is no top-level `session`. Sessions live nested: `mo agent session …` (`MohistCliCommands.Agent.cs:553`) and `mo issue session …` / `mo issue sessions <num>` (`MohistCliCommands.Issue.Session.cs`). Table shapes are an enum (`MohistCliApi.cs:1068`) → `ResourceOutputCatalog.For` returns cardinality + fields (`ResourceOutput.cs:18-100`); `AgentSessionLaunch` columns are at `ResourceOutput.cs:68`.
- **Stakeholders / coordination.** #484 removes Session terminal-state semantics (the `failureReason`/`failureCategory` proxy on `GenericAgentSessionSummaryDto`); #484's design explicitly defers the `mo session` command surface to this issue. The two are interlocking: this issue gives the job result a home so #484 can stop mirroring it onto the Session. Per `AGENTS.md` the project is in active development with no version-compatibility constraint.

## Goals / Non-Goals

**Goals:**
- `mo agent launch <agent>` returns both the AgentJob id and the AgentSession id; the HTTP launch response carries `jobId`.
- A product AgentJob read surface: `mo agent job list <agent>` and `mo agent job view <job-id>` over net-new HTTP GET routes, exposing status and `AgentJobTerminalResult`.
- A source-independent top-level `mo session` (list/show/transcript/followup/compact/reset/cancel) addressed by stable Session ID, retiring the duplicate `mo agent session` and `mo issue session` groups.
- The CLI reads a launch's work outcome from `agent job` and the conversation from `session`.

**Non-Goals:**
- No change to the AgentJob execution/dispatch contract, retry, or recovery (that is #410's domain). This change only *reads* job state that already exists.
- No removal of Session terminal-state fields from DTOs — that is #484. This change stops *preferring* the Session as the result read path; it does not edit the Session DTO.
- No session `archive`/`delete` lifecycle. The #484 cross-reference bundles "`mo session`, archive/delete" against this issue, but archive/delete is not required by the issue title or the specs and is deferred to a follow-up to keep this slice bounded (see Open Questions).
- No Web UI changes beyond what is forced by the response field addition (`jobId`); the Web session page is out of scope.

## Decisions

### D1. The AgentJob grain key is the product job id (no separate external id)

Expose the existing grain string key verbatim as `jobId`. It is already stable (minted once, persisted for the grain's lifetime) and charset-safe (`[A-Za-z0-9_.-]+`, matching the validation the existing endpoint already enforces, `AgentJobController.cs:155`). It becomes the relational read-model primary key, the `jobId` in the launch `201`, and the addressing key for `agent job view`. Formats: manual `agent-job-launch-{guid}`, routed `StableJobKey(projectId,eventId,ruleId)`, mention `CommentJobKey(projectId,commentId,agentId)`.

**Rationale.** The spec forbids an id-translation gap between launch and read. A second opaque id would require a registry mapping external id ↔ grain key and would duplicate identity for no product benefit. The key already satisfies the constraints an external id would need.

**Alternatives.** Mint a separate opaque `job_<id>` and keep a registry — rejected: dual identity, extra table, and exactly the translation gap the spec rules out. Derive a human-readable id from `(agent, sequence)` — rejected: collides with the routed/mention deterministic-key model and breaks redelivery idempotency.

### D2. AgentJob gains a relational read model mirroring AgentSession

Introduce `AgentJobRow` (JSON `State` blob + indexed columns: `JobKey` (PK), `ProjectId`, `AgentId`, `Status`, `SubmittedAt`, `TerminalAt`), a `DbSet<AgentJobRow>` + `OnModelCreating` mapping with `json_extract` stored computed columns and a composite index on `(AgentId, ProjectId, SubmittedAt)`, an EF migration, an `IAgentJobStore`/`AgentJobStore` (upsert on write), and an `AgentJobQuerier` (`ListByAgentAsync(projectId, agentId)` + `GetByKeyAsync(jobKey)`). New routes read the querier; the grain remains the single writer.

**Persistence strategy — recommended: make the relational store authoritative like AgentSession.** `AgentJobGrain` injects `IAgentJobStore` and writes the row synchronously on each persisted transition (submit, runner-acceptance, terminal), and drops `[PersistentState("agent-job")]`. Activation loads from the store, exactly as `AgentSessionGrain` does. This is one write per transition (no dual-write consistency window) and is the established pattern in this codebase.

**Alternatives.** (a) Keep `[PersistentState]` as authoritative and add a best-effort mirror write to `AgentJobRow` — rejected as primary: a crash between the two writes leaves the read model stale, and for a *terminal* transition that is the final write, so a crashed mirror would never show terminal. (b) Serve `view` directly from the grain (`GetStatusAsync`/`GetTerminalResultAsync`) and keep only a list index — rejected: two read paths (grain for detail, row for list) with different freshness, and the grain is not enumerable for `list`. (c) Derive a job list from `AgentSession.LabelAgentId` — rejected: conflates the two owners (a session is not a job; follow-ups create sessions but not jobs) and cannot show job status/result.

### D3. `AgentLaunchResult` carries the JobKey; the launch response surfaces it

Add `JobKey` to `AgentLaunchResult` (`IAgentLauncher.cs:121`). `LaunchAsync` and `LaunchMentionAsync` return the key they already compute (`AgentLauncher.cs:124`, `:256`); the routed path already returns it. The HTTP handler adds `JobId` to `AgentSessionLaunchResponse` and a job-result link. No launch pipeline behaviour changes — only the returned identity.

**Rationale.** Minimal, surgical change: the key is already minted, just propagated. Keeps the "exactly one job + one session + one dispatch" invariant untouched.

**Alternatives.** Re-read the job key from the session labels after launch — rejected: the session is the conversation owner, not the job-id authority; depending on it re-couples the two owners.

### D4. A source-agnostic session read route for unified `mo session`

Add a project-scoped route group `/api/projects/{projectRef}/sessions` with `GET /{sessionId}` and `GET /{sessionId}/transcript` that resolve an AgentSession by id **without** the `source-kind == "agent-launch"` gate (contrast `FindGenericSessionAsync`, `AgentSessionQuerier.cs:601`). The handler returns a unified summary DTO carrying the fields common to both sources (id, agent/workflow identity, activity, created/last-activity, model, usage, context refs), branching internally on the resolved `source-kind` to populate source-specific fields. The unified `list` accepts `?agent=`, `?issue=`, `?run=` filters and delegates to the existing querier methods (`ListAgentSessionsAsync`, `ListSummariesByIssueAsync`, `ListByWorkflowAsync`).

**Rationale.** The stable session id already exists on every row (both sources); only the read route gates it out. A new route avoids widening the generic-session DTO contract (whose doc-comments explicitly omit workflow fields by construction) while giving the CLI one address per session.

**Alternatives.** (a) Widen `FindGenericSessionAsync` to drop the source-kind gate — rejected: changes the `GenericAgentSessionSummaryDto` contract and its "cannot fabricate workflow identity" invariant (`AgentSessionReadModels.cs:222`). (b) Keep separate `agent-sessions` and workflow routes and have the CLI try both — rejected: re-creates the duplicate-command-set problem the spec retires. The older `agent-sessions/{id}` GET can be removed (no version-compat constraint) once the CLI moves over.

### D5. CLI command restructure

- **`agent launch`**: move `BuildSessionLaunch` up to register directly under `agent` (`MohistCliCommands.Agent.cs:18`); it prints `jobId` + `sessionId`.
- **`agent job` subgroup**: new `BuildJob(api)` under `agent` with `list <agent>` (→ `GET .../agents/{agentId}/jobs`) and `view <job-id>` (→ `GET .../agent-jobs/{jobId}`).
- **top-level `session`**: new `SessionCommands.Build(api)` added to the root (`MohistCliCommands.Build`): `list` (`--agent`/`--issue`/`--run`), `show`, `transcript`, `followup`, `compact`, `reset`, `cancel`, each taking a Session ID argument and hitting the D4 unified routes (and the existing followup/cancel routes, which already accept an id).
- **Remove** the `agent session` subgroup (`MohistCliCommands.Agent.cs:553`) and the `issue session` / `issue sessions` groups (`MohistCliCommands.Issue.Session.cs`); `mo issue sessions <num>` is replaced by `mo session list --issue <num>`.
- **Table shapes**: add `jobId` to `AgentSessionLaunch` (`ResourceOutput.cs:68`); add `AgentJobList`/`AgentJobView` to the `TableShape` enum (`MohistCliApi.cs:1068`), the cardinality switch, and the fields arms (`ResourceOutput.cs:23-97`).

**Rationale.** The CLI is a thin client over HTTP; each new command maps 1:1 to a route. Relocation is mechanical given the existing `System.CommandLine` structure. No aliases are kept (active-dev convention).

### D6. Result and conversation read paths do not overlap

`agent job` is the sole CLI read path for a launch's terminal outcome; `session` is the sole read path for the conversation. Once #484 removes `failureReason`/`failureCategory` from the Session DTO, the Session presents no competing verdict. This change does not edit the Session DTO (out of scope, #484-owned) but the CLI stops reading result from it.

**Rationale.** Realises the spec invariant "CLI 不用 Session 状态代替 AgentJob 结果". Keeps the two ownership boundaries from `design/agent-execution.md` clean.

## Risks / Trade-offs

- **[AgentJobGrain persistence migration] → Mitigation:** switching the grain off `[PersistentState]` to the relational store touches activation/recovery and the reminder path. Mitigate by mirroring `AgentSessionGrain`'s load-on-activate + write-on-transition exactly, and by relying on the existing spec/unit test harness (fake store, injected `TimeProvider`, no real DB). If the migration proves risky in review, fall back to the D2 alternative (a) mirror-write as an incremental step, accepting documented stale-on-crash for terminal until a later hardening.
- **[Read-model lag vs grain] → Mitigation:** the grain writes the row in the same transition that mutates state, before returning, so at every grain-call completion boundary the row matches. List/view read the row, not the grain, so they observe the last completed transition — acceptable for a status read path. No read blocks on grain activation.
- **[Unified DTO shape drift between sources] → Mitigation:** the unified summary DTO carries common fields and leaves source-specific fields absent-when-empty (the codebase "absent rather than null" idiom). A contract test asserts workflow-only fields (`workflowRunId`, `sessionName`) and agent-only fields (`agentId`) are populated only for their source.
- **[Breaking CLI paths] → Mitigation:** no compat constraint per `AGENTS.md`; update all CLI spec tests (`CliAgentSessionCommandSpecs`, `CliIssueSessionSpecs`) and docs in the same change. Root help / leaf help updated to the new tree.
- **[Coordination with #484] → Mitigation:** the two issues are interlocking, not blocking. This issue can land first (it adds the job home); #484 then removes the Session proxy. If they land together, ordering within the PR is: job read model → launch `jobId` → session unification → #484 DTO cleanup.

## Migration Plan

1. **Server — AgentJob read model (D2):** add `AgentJobRow` + `DbSet` + migration + `AgentJobStore` + `AgentJobQuerier` + DI; migrate `AgentJobGrain` to write-through store persistence; register in `MohistServiceRegistration` and `MohistApiRegistration`.
2. **Server — launch identity (D3):** add `JobKey` to `AgentLaunchResult`; propagate in `LaunchAsync`/`LaunchMentionAsync`; add `JobId` to the launch `201`.
3. **Server — routes:** add `GET .../agents/{agentId}/jobs` and `GET .../agent-jobs/{jobId}` (D1 keys); add the unified `/sessions` route group (D4).
4. **CLI (D5):** relocate `agent launch`; add `agent job`; add top-level `session`; remove `agent session` and `issue session(*,s)`; update table shapes.
5. **Tests & docs:** update CLI/server spec tests; close the gap notes in `docs/cli-reference.md:346,352`; align `docs/agents.md`, `design/cli.md:67`, and the `design/agent-execution.md` 实装差距 section.

**Rollback.** Each layer is independently revertible. The AgentJob read model is additive (new table/routes); reverting the grain persistence strategy is the only state-affecting step and is safe because the relational row is a strict superset of the prior grain state. No stored-data rewrite is required for existing AgentJobs/Sessions — they remain addressable by their existing stable ids; the read model populates going forward.

## Open Questions

- **Session archive/delete:** the #484 cross-reference bundles "`mo session`, archive/delete" against this issue, but archive/delete is not in the issue title or the specs. Decision: defer to a follow-up issue unless the planner explicitly scopes it in. If included, it needs a session lifecycle spec this change does not currently define.
- **`mo session list --run` project scoping:** the existing `GET /api/workflow-runs/{runId}/sessions` route is not project-scoped. The unified list route should be project-scoped (consistent with `--project`); confirm whether to re-scope the run filter under `/api/projects/{projectRef}/...` or keep the run-id-only route and resolve project from the run.
- **AgentJob read-model field set for `list`:** confirm the minimal list columns (job id, status, submitted-at, terminal-at, agent name) suffice for the CLI table, or whether the resolved model/prompt-summary should be denormalized into the row.
