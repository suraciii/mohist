# Self-Review — Issue 570 plan (`openspec/changes/issue-570/`)

**Round 2 — re-review (verify dispositions).** Round 1 (first full sweep, verdict PASS, no must-fix findings, 7 observations) is recorded below under "Round 1 record". Artifacts reviewed this round: `proposal.md`, `design.md`, `tasks.json`, `specs/runner-loss-work-recovery/spec.md`, `specs/runner-runtime-liveness/spec.md`, `specs/runner-resource-isolation/spec.md`, judged against issue #570 (Runner OOM/abnormal restart must leave active Agent work recoverable; recoverable interruption marking with reason; identity-preserving redelivery/reconciliation; user-visible recovery status; no resource cascade; deterministic tests for cascade protection, reconnect recovery, late-report idempotency).

## Round 2 verification

### 1. Dispositions of previous findings — nothing to dispose

Round 1 reported **zero must-fix findings** (verdict PASS), so there are no must-fix dispositions to verify. The 7 round-1 observations were non-blocking by definition and the plan artifacts are **byte-identical** to what round 1 reviewed:

- File mtimes: `proposal.md` 00:49 → specs 00:59–01:02 → `design.md` 01:10 → `tasks.json` 01:16 → round-1 `self-review.md` 01:26. No artifact is newer than the review.
- `git log`: HEAD is `efcab051e issue-570: add plan self-review (PASS)`; the only commits after the artifacts are the tasks.json and self-review commits. No source file under `packages/` was modified during or after planning.

Since no fixes were applied, none of the observations could have been addressed or mishandled; all 7 stand unchanged below and remain observations.

### 2. Regressions from fixes — none possible

No changes to any artifact since round 1, so no fix-induced regressions can exist. Re-verified that the codebase also did not drift underneath the plan (no `.ts`/`.cs` file newer than the proposal), so round 1's code-fact grounding could not have been invalidated by external movement.

### 3. Targeted adversarial re-check for pre-existing misses

Round 1 swept all four dimensions. This round I adversarially re-tested the sharpest candidates for a missed must-fix; none meets the bar:

- **"不重复执行" vs. verdict-less re-execution.** The issue's acceptance criterion "重连后可继续或进入明确终态且不重复执行" read against verdict-less work would, if it banned re-execution outright, force terminal-failing that work — recreating exactly the unrecoverable `runner-lost` loss the issue's opening sentence forbids. The only coherent reading (adopted by design Decision C/D and pinned in T-004/T-009: re-execute under original identity with at-most-one outcome per identity; held-key skip fences the genuinely-duplicate case of work still held being re-delivered) is satisfied by the plan. Not a gap.
- **Residual runner-heap runaway (round-1 observation 3).** If the 9.7–20.2 GB growth lived in the runner's own heap, per-work scopes alone would not have prevented the incident. The plan still satisfies every enumerated acceptance criterion: containment mechanism + deterministic tests (T-007), bounded teardown (T-006), and — for the residual — a recoverable interruption with bounded fallback instead of silent loss (T-001/T-002/T-004). Design consciously accepts the residual with systemd `MemoryMax` on the service. Stays an observation.
- **Spec-requirement ↔ task coverage re-audited.** Every `### Requirement` heading in all three spec deltas maps to ≥1 task, every task's `spec:` anchor slug matches an actual requirement heading (all 9 anchors re-checked character-for-character), and no requirement is task-orphaned. Migration steps 1–7 map onto T-001→T-009 in order.
- **Task graph re-validated mechanically.** `tasks.json` parses; 9 tasks; dependency edges all reference existing ids; topological visit confirms no cycles; ordering (server closeout → ledger → redelivery → journal; liveness/teardown parallel branches; isolation; surfaces; e2e verification) matches the migration plan.
- **Code-fact spot re-verification** (cheap, confirms round 1's grounding on the load-bearing claims):
  - `RunnerGrain.cs:694` `CloseoutLostAsync` calls `ObserveAgentRunnerDisconnectedAsync` (line 703) then `FailActiveWorkAsync(workerId, "runner-lost")` (line 705) — exactly the structure design Decision A replaces; the settlement-first branch is real.
  - `host.ts:652` `isReadyForClaim()` gates the claim path at line 584 — the liveness hole T-005 removes is real.
  - `TouchPresenceAsync` is called only from `DispatchService.cs:69` (the poll path); `RunnerGrain.HeartbeatAsync()` is `Task.CompletedTask` (no-op) — presence is poll-only today, so the heartbeat-presence fix in T-005 closes a real hole. (Round-1 observation 6's nuance — body-bearing heartbeats reach `HeartbeatRepairAsync`, which also does not touch presence — still holds and is still covered by the same fix.)
  - The `session.abort` + `/fetch failed/` failure family exists in `packages/runner/src/runtime/opencode/turn.ts` / `errors.ts` — the T-008 breaking sweep has a real target. No `runner-lost` literal exists in web/cli sources today, so the breaking change has no hidden consumer keyed on the string; the sweep concerns presentation of the new states.

**Round-2 conclusion:** no unaddressed must-fix finding, no regression, and no missed problem that meets the must-fix bar. Verdict unchanged.

## Verdict: PASS

The plan is ready to build.

## Round 1 record (first full sweep, unchanged)

### Dimension verdicts

**1. Coverage (checked, no must-fix issue).** Every issue goal and acceptance criterion is addressed: recoverable states for AgentJobs and workflow work (T-001 `WorkflowRun.WorkInterruption`, no terminal `runner-lost`; T-002 ledger interruption fields + `Recovering` projection); reconnect continue-or-terminal without duplicate execution (T-003 snapshot retention + identity-preserving redelivery + at-most-once report arbitration, T-004 durable work journal + first-poll declaration + held-key skip, T-001/T-002 bounded `runner-lost-recovery-expired` fallback, T-009 end-to-end at-most-one-outcome assertions); recorded interruption reason and affected work (reason code + workKey + timestamp on run interruption record and AgentJob ledgers; `runner-lost` | `runner-unregistered` stable codes); user-visible recovery status (T-008 wire projection + Web/CLI/attention rendering + breaking sweep); no resource cascade (T-006 bounded quarantine/teardown, T-007 per-work systemd scopes + `resource-contained` verdict + deployment assets); deterministic tests for cascade protection, reconnect recovery, late-report idempotency (T-007 fake `WorkExecutionLauncher`, T-004/T-009 restart tests in the `execution-envelope.startup.test.ts` style, T-003/T-009 late/duplicate report idempotency; deterministic seams: injected `TimeProvider`, reminder entry points, `RuntimeClock`, file-system seam, `vi.useFakeTimers`). Adversarial cases tried (Agent-task-with-settlement interruption, update-interrupt path, second-runner claiming) are explicitly fenced, verified out of scope in code, or deliberately assignment-bound per spec wording.

**2. Correctness (checked, no must-fix issue).** Closeout branch preserves #589's nonterminal settlement semantics (settlement first, interruption only when unfenced — two-fences invariant); restart reconciliation re-reports awaitingAck under the original report key and declares executing keys so `RenderActiveWorkflowAsync`'s reported-set check suppresses duplicate dispatch, with runner-side held-key skip preventing re-execution; deadline races resolve via grain serialization + stale-ack family + #589-proven reminder idempotency; recovering-AgentJob claim races resolve via deterministic work ids and existing revision/claim fencing; the T-005 liveness fix closes a verified real hole (presence is poll-only); teardown bounds (destroy-not-await dispatcher, SIGTERM→grace→SIGKILL escalation) are the correct fix for the verified unbounded waits; resource isolation correctly delegates enforcement to systemd/cgroups with T-006 deadlines as the non-systemd correctness bound.

**3. Consistency with the codebase (checked, no must-fix issue).** Every cited code fact was verified in the tree: presence timeout 2 min; #589 artifacts (`AgentResultSettlement`, `ObserveAgentRunnerDisconnectedAsync`, `HasUnresolvedAgentResult`, `FindReportableTaskAttempt`/`FindReportableWork`); `AgentJobLedgerRecord.DispatchJson`; `AgentJobGrain` timeout→Unknown/reminder/revision fencing; `.mohist/runner-state/` JSON-store pattern (`TerminalTaskLogDeliveryStoreImpl`); runtime diagnostic codes; `WorkflowStatusMapper` blocked derivation; `missing-workflow`/`not-running` ack shapes; CLI/web surfaces. New reason codes are genuinely new. Spec-delta format and `tasks.json` schema match the issue-589 convention; impact file lists are accurate.

**4. Task breakdown (checked, no must-fix issue).** Ordering sound, dependencies correct and acyclic (re-validated mechanically in round 2), every task has concrete verifiable acceptance criteria naming suites and commands with deterministic seams; breaking-change sweep and coordinated-release ordering assigned (T-008/T-009); rollback notes coherent.

### Round 1 observations (still non-blocking; artifacts unchanged, none elevated)

1. **WorkInterruption record lifetime across sequential interruptions** is not pinned by a test (work A interrupted → recovers → completes → work B interrupted on the same run); the deadline requirement forces correct behavior anyway; recommend pinning in T-001's tests.
2. **Re-attachment mechanics underspecified** (journal `binding` lacks a server endpoint; OS-assigned port is in-memory); the re-execute-under-original-identity fallback is spec-compliant and fake-session-testable; implementation-clarification item.
3. **In-runner Node-heap runaway is bounded only by the runner's own `MemoryMax`** — a recurrence in the runner heap still OOM-kills the Runner, now recoverably; residual consciously accepted, acceptance criteria met by the declared mechanism + tests.
4. **T-008 does not depend on T-006** — defensible since `runtime-quarantine-destroyed` is an observation reason, not a status state; keep T-008's reason-code list aligned with T-006's emissions.
5. **SpecUnitMigration ledger sync not mentioned** for server test-source changes; the architecture gate fails loudly on drift, so friction not gap.
6. **Minor context imprecision:** heartbeat-route no-op claim — body-bearing heartbeats reach `HeartbeatRepairAsync`, which also does not refresh presence; substantive claim (presence is poll-only) holds and the fix covers both paths.
7. **Open questions honestly scoped** (recovery-timeout default, reason-code wording, OpenCode server pid exposure, Pi process placement, container envelopes); none block acceptance criteria; T-006/T-007 pin verification of the SDK/pid and Pi-placement questions during implementation.

## Evidence of review

Round 2: static re-review with mechanical validation (file mtimes, `git log`/`status`, JSON parse + dependency-graph cycle check, anchor-slug audit) plus targeted code-fact spot re-verification against the unchanged workspace tree (`RunnerGrain.cs`, `IRunnerGrain.cs`, `DispatchService.cs`, `runtime/host.ts`, `runtime/opencode/turn.ts`, `runtime/opencode/errors.ts`). No implementation exists yet.

<promise>PASS</promise>
