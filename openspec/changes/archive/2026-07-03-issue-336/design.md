## Context

When an ops task fails, the Web shows only the terminal verdict — `status` + short
`message` + structured `output` JSON. The actual command output that explains *why*
(git conflict file/commit, clone error) is either discarded inside the runner or
collapsed into a per-call-site `combinedOutput` string that never leaves the process
(`packages/runner/src/actions/git.ts:8`). Users must SSH in and re-run the command.

This issue delivers the **end-to-end Phase 1 loop**: runner captures ops command
output line-by-line, server stores it on an independent channel, Web renders it in
the task expand area. It is **terminal-state batch only** — no real-time streaming
(that is Phase 2).

The conceptual model and industrial rationale (GitHub Actions Runner as the
reference for single sink, state/log separation, merged streams) are already
established in [`design/task-log.md`](../../../design/task-log.md). This document
covers *how* to implement it across the three packages and the concrete decisions
that the specs leave open.

Key existing surfaces this builds on / mirrors:

- `runCommand` (`packages/runner/src/system/process.ts:38`) — current aggregate-only
  capture; gains an optional `onLine`.
- `ActionContext` (`packages/runner/src/core/types.ts:167`) — gains a `log` sink.
- `WorkExecutor.execute` (`packages/runner/src/runtime/executor.ts:50`) and
  `RunnerHost.executeAndReport` (`packages/runner/src/runtime/host.ts:305`) — the
  per-work lifecycle where a collector is created and flushed.
- Artifact upload pattern — `WorkflowArtifactUploadRoutes.cs` (dual owner-kind
  routes, no grain), `ServerConnection.uploadArtifact`
  (`packages/runner/src/server/connection.ts:100`), owner-kind algorithm at
  `artifact-side-effects.ts:107`.
- Issue-path query pattern — `IssueRoutes.Artifacts.cs` +
  `ResolveWorkflowRunIdAsync` (`IssueRoutes.Helpers.cs:75`).
- Store placement convention — `Infrastructure/Data/Runner/RunnerWorkStore.cs`
  (enforced by `ArchitectureRules.DataStores_AreInInfrastructureData`).

Constraints: the runner uses an injectable clock (`n`/`now`) threaded through
modules and `vi.useFakeTimers` in tests, so timestamps must be injectable
(`design/testing.md`). C# treats warnings as errors (lint). No real external
dependencies or wall-clock in tests.

## Goals / Non-Goals

**Goals:**

- Every ops command's stdout/stderr (git/shell) is captured line-by-line, survives
  even without a trailing newline, and is viewable in the Web task expand area.
- Capture flows through **one** sink that masks, numbers, and buffers — exactly
  once. No second code path can leak a secret or drop a sequence number.
- TaskLog is stored **independently** of `WorkflowRun`/`WorkResult`/`report` — it
  never participates in status adjudication (mirrors artifacts).
- Existing ops call sites (`git()`, `runCommand`) keep their full aggregate return
  contract unchanged → zero behavior regression.
- All three packages' existing tests pass; new coverage exists for the capture
  pipeline, masking, truncation, no-loss drain, store routing, and viewer.

**Non-Goals:**

- Real-time streaming during execution (Phase 2).
- Log search, download, level/stream filtering (Phase 3).
- Capturing ACP agent dialogue (already covered by AgentSession transcript).
- A complete secret masker (encoding-variant defense) — Phase 1 ships a minimal
  credential-pattern masker; full hardening is later security work.
- Distributed/multi-runner log aggregation (single-machine assumption).
- Any change to `report`, `WorkResult`, or the `WorkflowRun` aggregate structure.

## Decisions

### D1. Independent channel mirroring artifact uploads; no grain involvement

TaskLog uploads hit a dedicated store directly, exactly like
`WorkflowArtifactUploadService` does for pending uploads. The handler does
`store.AppendAsync(...)` + `SaveChangesAsync` and returns — it never calls
`WorkflowGrain` or `RunnerGrain.ReportWorkflowResultAsync`.

**Rationale:** TaskLog is review evidence, not a status-adjudication input (the
three invariants in `design/task-log.md` §归属边界). Routing it through the grain
would couple "process recording" to "status adjudication", violating the
execution-fact/state-referee separation in `design/architecture.md`.

**Alternative considered:** embed a `log` field in `WorkResult`/`report`. Rejected —
it changes the report contract, forces the grain to receive logs it must ignore, and
breaks the "report never感知 logs" property that makes logs safe to drop on failure.

### D2. A single sink `ActionContext.log.write(source, text)` returns the seq

One funnel on the runner. Every ops output source (workspace-prep, branch-check,
action body, cleanup) calls `log.write(source, line)`; the sink masks → assigns a
monotonic `seq` → appends to the per-work buffer, in that order. `source` is a phase
label (e.g. `workspace-prep`, `branch-check`, `action:rebase`, `cleanup`); there is
**no stream parameter**.

**Rationale:** matches GA's `ExecutionContext.Write` (single funnel that masks +
numbers + routes once). A single chokepoint is the only way to guarantee no unmasked
data is ever buffered and no line is dropped or double-counted — any bypass defeats
exactly one of those guarantees.

**Alternative considered:** keep each call site assembling `combinedOutput` and mask
at upload time. Rejected — it leaves a window where unmasked text exists in the
buffer, and re-introduces the per-site fragmentation that caused the original
"output discarded" problem.

### D3. stdout/stderr are merged into one line-number sequence (no stream dimension)

`runCommand.onLine` emits child stdout and stderr through the **same** callback
sharing one `seq` sequence. `LogEntry` has no `stream` field; the server table has
no stream column.

**Rationale:** matches GA's fork-era decision to drop stream distinction; in Mohist's
ops context, locating a failure relies on *which command/phase* (`source`) and the
text itself, not on which file descriptor emitted it. Merging halves the schema and
the routing logic.

**Alternative considered:** preserve `stdout`/`stderr` as a per-line field. Rejected
as low-value complexity; the dimension does not change remediation actions and GA's
experience shows it is dispensable.

### D4. `runCommand` gains an optional `onLine`; aggregate return unchanged

`runCommand(command, args, cwd, signal, env?, options?)` where `options.onLine?`
emits merged lines. No-loss guarantees (from the spec):

1. A trailing partial line (child exits without a final `\n`) is flushed as a final
   line — track a per-stream pending buffer and emit the remainder on close.
2. A **post-exit drain** emits any buffered tail once after `close`, so nothing is
   lost across the close boundary.

The returned `CommandResult` (`stdout`/`stderr`/`exitCode`) is byte-identical to
today, so `git()` and every existing caller are unaffected.

**Rationale:** the smallest change that unlocks line-by-line forwarding for all ops
commands while preserving the aggregate contract that rebase/push/openspec/health-check
callers depend on.

**Alternative considered:** replace the aggregate with a streaming-only API.
Rejected — it would force every caller to reassemble output and regresses the
existing `combinedOutput` consumers.

### D5. Masking at the sink entry, before buffering; minimal masker for Phase 1

`log.write` masks known credential patterns (git remote URLs with embedded
credentials, and any runner-configured secrets) **before** assigning `seq` or
appending to the buffer. Only masked text ever leaves the sink.

The Phase 1 masker is intentionally minimal: a small set of regex patterns. It is
**not** the full GA-style masker (which also registers URL-encoded / JSON-escaped /
backslash-escaped variants). The masker is the single place to strengthen later.

**Rationale:** guarantees there is no "persisted-but-not-yet-masked" window. Keeping
the masker at the only funnel means a future hardened masker is a one-location
upgrade covering all sources.

**Alternative considered:** mask on the server at write time. Rejected — it requires
transmitting and transiently storing raw secrets, and the server cannot know
runner-local secrets.

### D6. Per-work `TaskLogCollector`; terminal-batch flush; head-drop truncation

Each work item owns a collector that buffers masked entries (producer appends only).
On task completion the collector flushes **once** as a terminal batch via the
independent upload channel. If the captured log exceeds the capacity limit, the
collector drops the **oldest (head)** lines and keeps the **most recent (tail)**
(error context), sets a `truncated` flag, and **does not reuse discarded `seq`
values** — retained `seq`s stay monotonic and contiguous, keeping cursor pagination
stable.

The flush happens in `RunnerHost.executeAndReport` around the existing
`report` call. It is **best-effort**: a failed log upload is logged and swallowed;
it must never block or fail the `report`, because the report carries the verdict and
logs are non-authoritative.

**Rationale:** Phase 1 is terminal-state (the full log is available once the task
finishes), so one batch is enough and avoids per-line HTTP chatter. Dropping head
keeps the error-bearing tail; not reusing seq keeps pagination deterministic.

**Alternatives considered:**
- Per-line upload during execution — rejected (chatiness; and Phase 1 explicitly
  excludes in-flight visibility).
- Drop tail / keep head on overflow — rejected (the tail holds the failure cause).
- Retry failed uploads — deferred; Phase 1 is best-effort-once (see Open Questions).

### D7. Owner-kind routing mirrors artifacts; `OwnerKind`+`OwnerId` pair in storage

Two POST routes symmetric with artifact uploads:

```
POST /api/workflow-runs/{ownerId}/work/{workId}/task-log
POST /api/agent-jobs/{ownerId}/work/{workId}/task-log
```

`ownerKind`/`ownerId` computed exactly as in `artifact-side-effects.ts:107`
(`ownerKind = work.ownerKind === "agent-job" ? "agent-job" : "workflow"`;
`ownerId` = `agentJobId` or `workflowRunId`). The store key is
`(OwnerKind, OwnerId, WorkId, Seq)` — never a single overloaded `workflowRunId`
column — so the two owner kinds cannot collide.

**Rationale:** artifact uploads already solved the agent-job asymmetry; reusing the
same routing keeps the two independent channels isomorphic and avoids a new
ownership model.

### D8. Store placement and schema

New files mirroring `RunnerWorkStore`:

```
packages/server/src/Mohist.Server/Infrastructure/Data/Runner/
  TaskLogEntryRow.cs      # entity (like RunnerWorkRow.cs)
  TaskLogStore.cs         # AppendAsync + paginated query (like RunnerWorkStore.cs)
```

`TaskLogEntries` columns: `Id` (PK), `OwnerKind`, `OwnerId`, `WorkId`, `Seq`,
`Timestamp`, `Source`, `Text` (no stream column). Index `(OwnerKind, OwnerId, WorkId,
Seq)` for cursor pagination. An EF Core migration is added under
`Infrastructure/Data/Migrations/`. `Workflow/` domain, `WorkflowGrain`,
`RunnerGrain.ReportWorkflowResultAsync`, and `WorkResult` are untouched.

**Rationale:** `ArchitectureRules.DataStores_AreInInfrastructureData` forces all
`*Store` classes into `Infrastructure.Data` and forbids stores in the feature
folder; this is the same location `RunnerWorkStore` already occupies.

### D9. Issue-path GET with cursor pagination; taskId → workId resolution

```
GET /api/projects/{projectId}/issues/{number}/workflow/tasks/{taskId}/logs?cursor=&limit=
  → { lines: [{seq, timestamp, source, text}], nextCursor, truncated }
```

Cursor pagination is over `seq` (`nextCursor` = last seq of the page, `null` at end),
so ordering is stable and resumable. `truncated` reflects whether the runner dropped
head lines at capture time. Empty result (no captured lines) returns `lines: []`,
`nextCursor: null`, never an error.

**taskId ↔ workId resolution (the one genuine design tension).** The runner stores
logs keyed by `workId` (the only work identifier it has — used for report and
artifact uploads). The Web timeline, however, addresses tasks by `TaskRun.Id`
(per-attempt, e.g. `"build.1"`; set in `TaskRun.cs:152` and projected as
`TaskStatusView(t.Id, …)` in `WorkflowStatusMapper.cs:99`). `TaskStatusView` does
not currently expose `WorkId`.

**Decision:** the GET endpoint accepts the timeline task id (`taskId` = `TaskRun.Id`)
and the **server resolves `TaskRun.Id → WorkId`** via the workflow run read model
(reuse `ResolveWorkflowRunIdAsync` from `IssueRoutes.Helpers.cs:75`, then look up the
task's `WorkId` from the run state), then queries the store by `workId`. This keeps
the path's `{taskId}` consistent with how the Web addresses tasks everywhere else
(retry, artifacts-by-task) and avoids changing the timeline DTO.

**Alternative considered:** add `workId` to the `TaskStatusView` timeline DTO and
have the Web query by `workId` directly. This removes the resolution join but widens
the timeline contract and leaks a runner-internal identifier to the UI. Preferred
only if the server-side resolution proves awkward — flagged in Open Questions.

**Note on attempts:** `TaskRun.Id` is per-attempt and each attempt has exactly one
`WorkId` (assigned in `WorkflowRun.Task.cs` `StartTask`), so resolving
`TaskRun.Id → WorkId` yields one log per attempt — the correct granularity.

### D10. Web panel is additive; fetches via the issue-path query

A log panel is added inside `TaskItem`'s expanded region
(`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:93`), rendered
**in addition to** the existing status/message/output/failure-kind blocks. It fetches
via a TanStack Query hook against the new GET endpoint (keyed by
`issueNumber + taskId`), renders each line with its `source` label + timestamp in a
scrollable region, and surfaces a truncation indicator when `truncated` is true.

**Rationale:** the log is a fourth class of evidence, not a replacement for the
verdict or structured output; rendering it alongside (not in place of) preserves the
spec's "existing rendering unchanged" requirement and degrades gracefully to "no
log" if the endpoint is absent or empty.

### D11. Injectable clock for timestamps

`TaskLogger`/`TaskLogCollector` accept a `now: () => Date` (or `() => number`)
injected at construction, threaded from the host like the existing `n` in
`workspace-registry.ts` / `worktree-enforcement.ts`. Tests use `vi.useFakeTimers` +
`vi.setSystemTime` (matching `tests/acp/support.ts`).

**Rationale:** `design/testing.md` forbids wall-clock in tests; the existing runner
code already follows this `n`-injection convention, so the logger must too.

## Risks / Trade-offs

- **[A sink bypass leaks a secret or drops a line]** → Mitigation: `log.write` is the
  only producer of buffered entries; the executor phases and `git()`/`runCommand`
  are rewired so no ops output reaches the buffer by another path. Covered by a unit
  test asserting every buffered entry passed through `write`.
- **[Minimal masker misses encoded credential variants]** → Mitigation: masking is
  centralized at the single funnel, so a hardened masker (URL/JSON/backslash
  variants) is a future one-location upgrade. Phase 1 scope (known patterns) is
  explicit in Non-Goals.
- **[Failed log upload]** → Mitigation: flush is best-effort and decoupled from
  `report`; the verdict still lands. A missing log degrades to "no log available",
  never a wrong status. (Retry behavior is an Open Question.)
- **[taskId → workId resolution adds a read join on query]** → Mitigation: the join
  is a read of already-resolved run state; `WorkId` is immutable once a task starts.
  If the join is awkward, fall back to exposing `workId` in the timeline DTO (D9
  alternative) — a contract widening, not a correctness risk.
- **[Unbounded log growth]** → Mitigation: head-drop truncation caps per-task
  storage; cursor pagination caps query cost. Discarded `seq`s are not reused, so
  pagination stays stable.
- **[Large terminal batch is momentarily big]** → Mitigation: Phase 1 batch is
  bounded by the truncation cap; acceptable for single-machine assumption.
- **[Merged streams lose the stdout/stderr dimension]** → Intentional trade-off (D3);
  locate by `source` + text, matching GA.

## Migration Plan

The change is **purely additive**: new table, new endpoints, new panel. `report`,
`WorkResult`, and `WorkflowRun` are structurally unchanged, so there is no backfill
and no downtime.

1. **Server first** — add `TaskLogEntryRow` + `TaskLogStore` + EF migration; add the
   POST and GET endpoints (`TaskLogRoutes`, mapped like
   `MapWorkflowArtifactUploadRoutes`). Register `TaskLogStore` in DI. Deploy. Existing
   consumers are unaffected (endpoints are new; no caller yet).
2. **Runner next** — add `onLine` to `runCommand` (opt-in; existing callers
   unchanged); add `TaskLogger` + masker + `TaskLogCollector`; add
   `ServerConnection.uploadTaskLog`; wire the sink into `ActionContext` and the
   executor phases; flush in `executeAndReport` before the best-effort `report`.
   Deploy. Logs begin landing but nothing reads them yet.
3. **Web last** — add the query hook + log panel. Deploy. End-to-end loop is live.

**Rollback:** because every piece is additive and independent, rollback is safe at
any layer. Dropping the panel reverts the UI to today; the runner without a server
endpoint degrades to discarding output (pre-change behavior) since uploads are
best-effort; the table can remain with no consumer. To fully revert, drop the
endpoints and the migration — no existing data depends on `TaskLogEntries`.

No data migration of existing task history is performed (pre-change tasks have no
captured logs by definition).

## Open Questions

- **Capacity limit value.** `design/task-log.md` suggests ~256KB / 5000 lines. Pick a
  concrete default (likely line-count based, e.g. 5000) and make it a single named
  constant so it is tunable.
- **taskId → workId resolution mechanism.** Confirm the workflow read model exposes
  `WorkId` lookup by `TaskRun.Id` without a grain call, or decide to widen
  `TaskStatusView` with `workId` (D9 alternative). Lean: resolve server-side; verify
  read-model availability during implementation.
- **Masker pattern inventory for Phase 1.** Confirm the exact set (git remote URL
  credentials, ACP agent keys, runner-configured tokens) and where the masker reads
  its secret list from at runtime.
- **Failed-upload retry policy.** Phase 1 is best-effort-once; decide whether a
  single retry on transient failure is worth it, or keep strictly fire-and-forget to
  preserve the "logs never affect status" invariant.
- **`Text` column type.** `nvarchar(max)` vs a capped length — decide against the
  capacity limit (head-drop already bounds per-line and per-task size).
