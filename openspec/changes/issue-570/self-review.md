# Self-Review: Issue 570 Plan — Runner Loss Work Recovery & Resource Isolation

Round: **first review (full sweep)**. Reviewed against the issue body (not the
plan's own framing), then `proposal.md`, `design.md`, `tasks.json`, and the
three spec files under `specs/`, with code-level verification of every
load-bearing claim about existing seams in `packages/server` and `packages/runner`.

## Verdict

**FAIL** — one must-fix finding (M-1) on the resource-isolation enforcement
mechanism. The runner-loss recovery side of the plan is consistent with the
codebase and covers the issue's goals; every other seam claim I probed checked
out.

## Must-fix findings

### M-1. D7/T-007 rest Linux RLIMIT enforcement on a spawn seam that does not exist

- **Where:** `design.md` D7 ("RLIMIT_AS/RLIMIT_DATA on Linux **via the
  existing spawn seam**"), `tasks.json` T-007 description (same claim) and
  T-007 notes ("**Linux RLIMIT seam exists in the spawn path**").
- **Codebase fact:** action subprocess work is spawned through
  `runCommand` → `system/process.ts` → `node:child_process.spawn`
  (`packages/runner/src/system/process.ts`, `ProcessSpawner` type pins
  `SpawnOptions`). Node's `SpawnOptions` (repo is on Node 22, `.nvmrc`)
  has no resource-limit support (`resourceLimits` exists only for
  worker threads), and a repo-wide search for
  `rlimit|prlimit|ulimit|setrlimit|resourceLimits` across `packages/runner`
  and `packages/cli` returns nothing. There is no existing seam to apply
  spawn-time OS limits through; the claim is factually wrong in both the
  design decision and the task notes.
- **Why must-fix:** the plan is the build contract for the issue's
  resource-isolation goal — "资源失控也不应让同一 Runner 上的多个长任务级联拖垮整个执行平面"
  and the acceptance criterion "有确定性测试覆盖资源级联保护" (plus
  `runner-resource-isolation` spec requirement "Per-work resource containment
  on the runner" and T-007 AC1's memory-burning child-process test). As
  written, the Linux enforcement layer cannot be executed as specified: the
  builder must invent the mechanism (spawn via a `prlimit` wrapper, a shell
  `ulimit` preexec, or post-spawn `prlimit(2)` with a race window), each of
  which has real trade-offs (signal delivery, process-group semantics,
  kill-tree behavior that `runCommand`'s timeout/`onClose` machinery relies
  on, and the design's own "no new external dependencies" constraint) that
  the design never evaluated. Leaving it unfixed means the plan is wrong
  about how the acceptance criterion is met on the primary (Linux) platform.
- **Required fix:** amend D7 and T-007 to name a mechanism that actually
  exists or is actually specified — either (a) specify the RLIMIT application
  approach concretely (e.g. wrapper command / preexec / post-spawn prlimit)
  with its trade-offs evaluated, or (b) drop the RLIMIT claim and commit to
  the watchdog + wall-clock termination path (which the design also names and
  which alone satisfies the spec scenario and T-007 AC1), documenting the
  detection-latency consequence.

## Dimension verdicts (first review, full sweep)

- **Issue goals & ACs re-read before artifacts:** done. Goals: (1) active
  AgentJob and workflow work enter recoverable states after simulated
  OOM/abnormal restart; (2) reconnect continues them or reaches a definite
  terminal without duplicate execution; (3) control plane records
  interruption reason and affected work; (4) deterministic tests for resource
  cascade protection, reconnect recovery, late-report idempotency; plus
  user-visible recovering state instead of context-free `session.abort`.
- **Coverage:** checked, no issue (aside from M-1's correctness defect).
  Goal 1 → T-001 (workflow tasks/checks interruption record), T-002
  (AgentJob recovering projection). Goal 2 → T-003 (identity-preserving
  re-delivery), T-008 (fence reconciliation / re-attach / replay), T-001/T-002
  bounded deadlines. Goal 3 → `WorkInterruption{ReasonCode, WorkId, OwnerId,
  RecordedAt, RecoveryDeadlineAt}` + Unknown-with-reason projection.
  Goal 4 → T-005 (late-report idempotency SpecTests), T-003/T-008 (reconnect
  recovery incl. an end-to-end restart integration test), T-006/T-007
  (cascade protection). User-visible state → T-004 (web, CLI, issue
  attention). Every capability requirement has at least one matching task
  with named tests.
- **Correctness:** one must-fix (M-1). Otherwise checked and sound. Key
  soundness arguments verified against code: interrupted work staying
  `Running` keeps `CurrentActiveWorkFor`/`FindReportableWork` report paths
  alive (reports clear the interruption without new identity);
  settlement-task exclusion matches the current closeout split
  (`ObserveAgentRunnerDisconnectedAsync` Stale → fail path in
  `RunnerGrain.CloseoutLostAsync`, lines 756–772); D3's no-takeover stance is
  coherent with journal-local at-most-once facts; D4 never opens the `begin()`
  fence so repeated re-delivery stays at-most-once; D4.3's wire `unknown`
  routes through the existing `ObserveAgentResultUnknownAsync` seam
  (`WorkflowReportService.ReportAsync`) rather than fabricating outcomes;
  deadline race safety (late report after deadline-fail → Stale) is covered
  by T-005 AC5. Deadline arithmetic holds with proposed defaults
  (2-min presence < 10-min JobTimeout < 15-min recovery deadline; settlement
  5-min excluded from interruption).
- **Consistency with the codebase:** one must-fix (M-1). Every other
  load-bearing seam claim verified true:
  `CloseoutLostAsync`/`FailActiveWorkAsync(workerId, "runner-lost")` exists as
  described; `AgentJobStore.ListRunningForRunnerAsync` filters
  `Status == "running"` so Unknown jobs are absent from redelivery today;
  `AddMissingRedeliveriesAsync` re-renders workflow active work from
  persisted facts; host.ts skips `admission !== 'new'` dispatches forever
  ("refusing replay"); journal replays `completed` entries into `awaitingAck`
  at startup; `MarkUnknownAsync`/`EnterUnknownStateAsync` and the durable
  `agent-job-recovery` reminder exist; `BlockUnresolvedAgentResult` +
  `agent-result-settlement` reminder is the right pattern to mirror;
  `scheduleRebuild` awaits `generation.drained` unboundedly;
  `terminateTree` awaits `dispatcher.close()` on an Agent built with
  `headersTimeout: 0, bodyTimeout: 0`; pi `shutdown()` awaits
  `services.close()` unboundedly; Accepted/Stale are both durable acks at the
  HTTP layer; a report against an Unknown AgentJob settles it. Spec files
  follow the repo's openspec requirement/scenario format; all task spec
  anchors resolve to actual requirement headers; named output paths exist
  (minor path nit in O2).
- **Task breakdown:** checked, no issue. 8 tasks, dependencies sound
  (T-006→T-07 quarantine-teardown dependency; T-001/T-002→T-003/4/5;
  T-008 and T-006 independent, matching the server-first/runner-second
  migration order, and the mixed-version windows are argued safe in the
  migration plan). Every acceptance criterion is testable and the named test
  styles (SpecTests, runner vitest with injectable clocks/fakes) match the
  repo's determinism principles.

## Observations (do not affect the verdict)

- **O1 — Landed `runner-work-result-recovery` spec interplay.** D4.3 changes
  runner behavior for re-delivered `started` entries from "refuse and leave
  outcome arbitration to existing paths" (spec scenario "process restarts
  before a result exists", landed by #545 and carried unchanged in this
  change dir) to "reconcile and surface a definite outcome" (`unknown` for
  agent tasks, failed `runner-restarted` otherwise). The new behavior is
  defensible under that spec's letter (no re-execution; outcomes flow through
  the existing unresolved/authoritative-result server paths; no fabricated
  results), but the change does not amend or annotate the older scenario, so
  the same change dir now carries two spec statements about the same trigger
  that a reader must reconcile. Recommend a clarifying amendment or note when
  M-1 is fixed.
- **O2 — Wrong test directory name in T-006 output.** Says
  "packages/runner/test"; the actual directory is `packages/runner/tests`
  (colocated `*.test.ts` also exists). Cosmetic since the task says
  "colocated and/or".
- **O3 — Shared-runtime per-work memory isolation is approximate.** D7
  cannot memory-isolate turns inside the shared OpenCode server without
  process-per-work (rejected); the mitigation chain is turn-budget quarantine
  → bounded teardown + deployment `MemoryMax` + (now) end-to-end runner-loss
  recovery. Honest, documented trade-off; acceptable relative to the issue's
  cascade-protection goal, but reviewers of the implementation should keep
  the residual OOM-the-runner case in view.
- **O4 — Open questions left open.** Recovery-timeout default tuning,
  `AdmissionReady` gating of recovering-job redelivery, per-work-type
  containment defaults, and the `TaskInterrupted` event shape are undecided.
  Each has a stated current proposal, so this is fine at plan stage; they
  should be resolved during T-001/T-002/T-003/T-007 respectively.
- **O5 — T-005's "Unknown-job-report dead-end gap" may be smaller than
  described.** Current code already accepts reports against Unknown jobs
  (settling them) and already acks reports against terminal jobs as
  `stale`. The task's value is then mostly proving these guarantees
  deterministically; the builder may find little to close beyond
  `runner-or-work-mismatch` (a non-ack, retried forever) — worth checking
  whether that case is in scope while implementing.
- **O6 — Rollback note relies on `FailActiveWorkAsync` remaining available.**
  It does (and T-001 keeps the seam, replacing the call in closeout), so the
  one-line rollback action is plausible; just flagging the coupling.

<promise>FAIL</promise>
