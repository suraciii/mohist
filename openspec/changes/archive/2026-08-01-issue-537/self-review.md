# Self-Review — Issue 537

## Verdict

The plan is coherent, well-grounded in the codebase, and ready to build. The
design correctly identifies the decisive architectural insight (the read path's
`RunningTask` gating makes orphan snapshots invisible, so terminal-drop is a
space-reclamation concern, not a correctness one), preserves the existing
read/write split, and reuses the established cold-start upgrader pattern. The
specs are normative and testable; the task split is correct (runtime
externalization as one coherent module, migration as a separate dependent
module).

Four minor findings below — all clarity/consistency improvements, none blocking.

---

## Findings

### 1. [Low] Snapshot store key uniqueness — design should cite the existing invariant

Decision A states "每个 attempt 一个唯一 WorkId" as a bald claim. `WorkId` equals
`TaskRunId`, whose format is `{definitionId}.{taskAttempt}` when `stageAttempt
== 1` (`TaskRun.cs:219-222`). `stageAttempt` is the stage's **rerun** counter,
not its index — so on a first run every stage has `stageAttempt == 1`, and two
stages that happen to define a task with the same `definitionId` produce the
**same** `TaskRunId` (the attempt counter in `MakeTask` is scoped to the current
stage's task list, `TaskRun.cs:191-195`).

This means `TaskRunId` is not mechanically guaranteed unique across stages; it
is unique **under the convention that task definition IDs are distinct across
stages within a run**. That convention is already relied on elsewhere:
`WorkflowArtifact.ProducerKey` (`WorkflowArtifact.cs:37`) uses
`{WorkflowRunId}:{TaskRunId}` with the comment "the task run ids are unique
inside a workflow run." So the design is **consistent with an existing
codebase assumption**, not introducing a new one — but it should say so rather
than asserting uniqueness without basis. If a custom profile ever violates the
convention, both artifacts and snapshots would collide; that is a pre-existing
shared risk, not one this design creates.

**Recommendation:** Add one sentence to Decision A stating that WorkId =
TaskRunId and that uniqueness-within-a-run is the same invariant already relied
on by `WorkflowArtifact.ProducerKey`, so the dependency is explicit.

### 2. [Low] Proposal references a "Cancelled" terminal state that does not exist

`proposal.md` line 8 lists "终态（Completed / Failed / Cancelled）". The
`TaskRunStatus` enum is `{ Pending, Running, Completed, Failed }` (`TaskRun.cs:18`)
— there is no `Cancelled`. A stopped run transitions its Running task via
`FailTaskForStopped` → `Failed`. The specs and design correctly use "Completed
or Failed". This is loose wording in the proposal only and does not propagate to
the build contracts.

**Recommendation:** Change the proposal's "Cancelled" to match the actual model
(stop → Failed), for consistency with the specs and design.

### 3. [Low] Spec "SHALL NOT be retained" vs design best-effort delete — mild tension

The spec (`dispatch-snapshot-persistence`, "Terminal or superseded attempts drop
the snapshot immediately") states the snapshot "SHALL be invalidated immediately
and SHALL NOT be retained." The design (Decision C) uses a **best-effort**
delete after `CommitAsync` and explicitly says "正确性不依赖删除成功" — a delete
failure leaves a transient orphan (invisible due to `RunningTask` gating, swept
at next startup). In the strict reading, a transient orphan is "retained," which
tensions with the spec's absolute "SHALL NOT be retained."

In practice they converge: tests run the happy path (delete succeeds → not
retained), and the startup orphan sweep (T-002) reclaims any stragglers. But the
wording mismatch could confuse the implementer about whether a transactional
delete guarantee is required.

**Recommendation:** Either soften the spec scenario to "the snapshot is no
longer *accessible* after terminal (deleted at the transition; a delete failure
leaves an orphan that is invisible to redelivery and swept at startup)" or add a
design note that the orphan sweep makes "not retained" effectively true.

### 4. [Low] Store write/delete exception handling unspecified

Decision E describes first-write-wins via `LoadAsync` → INSERT, and Decision C
describes best-effort terminal delete. Neither specifies what happens if the
store **write** or **delete** throws:

- **Write failure** in `StoreActiveWorkDispatchAsync`: the exception propagates
  to `DispatchService` which catches it (existing try-catch at
  `DispatchService.cs:253-265`); the next poll re-renders via
  `TranslateToDispatchAsync`. This is the existing recovery path, but the design
  doesn't state it.
- **Delete failure** after a successful `CommitAsync`: if the exception
  propagates, the report call fails despite State already being terminal. Since
  "best-effort" implies swallow-and-log, the design should say so explicitly to
  avoid the implementer letting it propagate and causing confusing report
  failures.

**Recommendation:** Add a line to Decisions C/E: snapshot store failures are
best-effort — write failures are caught by the existing DispatchService recovery
path; delete failures are swallowed and logged (the orphan is invisible and
swept at startup).

---

## What is solid

- **The Running-gating insight** (design Context, Decision C) is correct and
  decisive: `CurrentActiveWorkFor` only returns `RunningTask`, so a terminal
  task's snapshot is never read regardless of physical deletion. This makes
  best-effort delete safe and avoids coupling the snapshot store to the State
  write transaction. Verified against `WorkflowRun.Work.cs:65-81,80-81`.
- **Read/write split preservation** (Decision B) matches the existing
  architecture: reads bypass the grain (querier), writes go through the grain.
  Dropping `DispatchSnapshot` from `WorkflowActiveWork` is the right call; all
  three consumers are identified (`WorkflowGrain.cs:492`,
  `DispatchService.cs:242`, `WorkflowRun.Work.cs:223`).
- **Migration design** (Decision D) correctly reuses the established preflight +
  backup + single-transaction + idempotent pattern from
  `WorkflowRunStateDataUpgrader`, and correctly identifies the orthogonality
  with #536's legacy-format transformation (different JSON paths, ordered
  execution). The STJ-ignore-unmapped-member safety net for the window before
  the upgrader runs is correctly noted.
- **Task split** is correct: T-001 is one coherent module (store + EF migration
  + grain/DispatchService switchover + terminal delete + run-delete cascade +
  tests), and T-002 is the data migration (dependent on T-001's table). This
  matches the splitting guidance (merge interface + impl + call-site switchover;
  separate preparatory migration). DAG is valid.
- **Spec coverage** is complete: all 10 requirements across both capabilities
  are addressable by the two tasks. Every requirement has ≥1 scenario; format is
  correct (4-hashbar scenarios, SHALL/MUST language, no delta headers).

<promise>PASS</promise>
