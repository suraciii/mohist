# Self-Review — Issue 570 plan (`openspec/changes/issue-570/`)

Reviewer: pi planning review (first full sweep). Artifacts reviewed: `proposal.md`, `design.md`, `tasks.json`, `specs/runner-loss-work-recovery/spec.md`, `specs/runner-runtime-liveness/spec.md`, `specs/runner-resource-isolation/spec.md`, judged against issue #570 (Runner OOM/abnormal restart must leave active Agent work recoverable; recoverable interruption marking; identity-preserving redelivery/reconciliation; user-visible recovery status; no resource cascade; deterministic tests for cascade protection, reconnect recovery, late-report idempotency).

## Must-fix findings

None.

## Verdict: PASS

The plan is ready to build. Observations below do not block.

## Dimension verdicts

### 1. Coverage (checked, no must-fix issue)

Every issue goal and acceptance criterion is addressed by the plan:

- "活跃 AgentJob 和 workflow work 都进入可恢复状态" — T-001 (`WorkflowRun.WorkInterruption`, no terminal `runner-lost`), T-002 (AgentJob ledger interruption fields + `Recovering` projection), spec scenarios "Presence timeout with an ordinary task running" / "AgentJob running on the lost Runner".
- "重连后可继续或进入明确终态且不重复执行" — T-003 (snapshot retention + identity-preserving redelivery + at-most-once report arbitration), T-004 (durable work journal, first-poll fact declaration, held-key skip), T-001/T-002 bounded terminal fallback (`runner-lost-recovery-expired`), T-009 end-to-end restart scenarios pinning at-most-one-outcome-per-identity.
- "控制面记录中断原因和受影响工作" — reason code + workKey + timestamp on the run interruption record and on AgentJob ledgers; `runner-lost` | `runner-unregistered` stable codes.
- "用户看到恢复中状态而不是无上下文的 session.abort 错误" — T-008 (wire `interruption` projection, Web/CLI/attention rendering, breaking sweep banning `runner-lost`/unannotated `session.abort fetch failed` presentations), backed structurally by T-005/T-06 never synthesizing outcomes (deferral observations, `runtime-quarantine-destroyed` normalization).
- "资源失控不级联" — T-006 (bounded quarantine drain/teardown), T-007 (per-work systemd scopes, `resource-contained` terminal verdict, deployment assets).
- "确定性测试覆盖资源级联保护、重连恢复、迟到报告幂等" — T-007 (fake `WorkExecutionLauncher`, tree-kill coverage), T-004/T-009 (process-restart tests in the `execution-envelope.startup.test.ts` style), T-003/T-009 (late/duplicate report idempotency), all with deterministic seams (injected `TimeProvider`, Orleans reminder entry points, `RuntimeClock`, file-system seam, `vi.useFakeTimers`).

All spec requirements in the three capability deltas map to tasks; every task's `spec:` anchor slug matches an actual requirement heading; tasks cover migration steps 1–7; no spec requirement is task-orphaned. I attempted to construct uncovered cases (Agent-task-with-settlement interruption, update-interrupt path, second-runner claiming) and each is either explicitly fenced (two-fences invariant, T-009 cross-cutting assertion), out of scope by existing behavior (`BeginUpdateInterruptAsync` only drains, verified in code), or deliberately assignment-bound per the spec wording ("A recoverable-interrupted AgentJob SHALL return to execution … when a Runner claims it again" — any runner; workflow redelivery — the reconnecting runner).

### 2. Correctness (checked, no must-fix issue)

Adversarial scenarios traced against the design; each holds:

- Closeout branch (settlement first, interruption only when unfenced) matches the actual `RunnerGrain.CloseoutLostAsync` structure (verified: `ObserveAgentRunnerDisconnectedAsync` then `FailActiveWorkAsync(workerId, "runner-lost")` on `Stale`) and preserves #589's nonterminal settlement semantics — no double fence, no protocol redesign.
- Restart reconciliation: awaitingAck entries re-report under the original report key; executing entries are declared in-flight so `RenderActiveWorkflowAsync`'s `reportedWorkKeys.Contains(workKey)` suppresses duplicate dispatch; the runner-side held-key skip prevents re-execution. Verified the server's poll-body-driven redelivery (`DispatchService.PollCoreAsync` → `RenderActiveWorkflowAsync`) and the runner's in-memory skip-on-hold (`runWorkerPool`) exist as described.
- Deadline races are handled: grain-serialized terminal transition vs. late report resolves via the stale-ack family; recovery-before-deadline resolves the interruption; reminder idempotency (ensure-on-activation, re-register, unregister-after-commit) follows the proven #589 settlement-reminder pattern.
- Claim races on recovering AgentJobs: verified `StableWorkId(Key)` is deterministic (re-admission already preserves the work id today), and revision/claim fencing exists; loser's reports acknowledged stale — at-most-once outcomes hold.
- Liveness restructure is factually grounded: verified `isReadyForClaim` halts all polling on Pi-unready/OpenCode-not-ready-or-cold; `HeartbeatAsync` is a no-op and `HeartbeatRepairAsync` (body-bearing heartbeats, sent every 15 s on an independent interval) does not refresh presence — presence is poll-only, so the T-005 fix (heartbeat → `TouchPresenceAsync`) closes a real hole.
- Teardown bounds: verified `scheduleRebuild` awaits `generation.drained` unbounded and `server-process.ts` `terminateTree` awaits `dispatcher.close()`; the destroy-not-await design plus SIGTERM→grace→SIGKILL escalation (pattern already in `system/process.ts`) is the correct fix and is test-seamable.
- Resource isolation correctly delegates enforcement to systemd/cgroups (the incident's runaway was the Node process itself; in-process policing cannot reclaim it), with correctness guaranteed by the T-006 deadlines on non-systemd platforms.

### 3. Consistency with the codebase (checked, no must-fix issue)

Every code fact the design cites was verified in the current tree: presence timeout 2 min; `#589` artifacts present (`AgentResultSettlement`, `ObserveAgentRunnerDisconnectedAsync`, `HasUnresolvedAgentResult`, `FindReportableTaskAttempt`/`FindReportableWork`, snapshot retention semantics); `AgentJobLedgerRecord.DispatchJson` and `AgentJobGrain` timeout→Unknown/reminder/revision fencing; `.mohist/runner-state/` JSON-store pattern (`TerminalTaskLogDeliveryStoreImpl`: atomic writes, serialized write chain, load-on-start, `ready()` gate); runtime diagnostic codes (`server-spawn-failed`/`health-failed`/`server-exit` exist in `runtime.diagnostic()`); `WorkflowStatusMapper` blocked derivation; `missing-workflow`/`not-running` ack shapes today; CLI `mo run`/`mo runner` commands and web workflow surfaces exist. New reason codes (`resource-contained`, `runner-unregistered`, `runner-lost-recovery-expired`) are genuinely new. Spec-delta format (### Requirement / #### Scenario) and `tasks.json` schema match the established convention (compared against issue-589). Impact file lists are accurate.

### 4. Task breakdown (checked, no must-fix issue)

Ordering is sound (server closeout → AgentJob ledger → redelivery/arbitration → runner journal → liveness/teardown/isolation in parallelizable branches → surfaces → end-to-end verification); dependencies are correct and non-cyclic; every task has concrete, verifiable acceptance criteria naming suites and commands (`npm run test:fast:unit`, `npm test -w packages/runner`, `npm run verify`, `docs:check`) with deterministic seams; breaking-change sweep and coordinated-release ordering (Server → Runner → Web/CLI, `readyRuntimes` absence degrading gracefully) are assigned to T-008/T-009. Rollback notes are coherent.

## Observations (non-blocking)

1. **WorkInterruption record lifetime is ambiguous across sequential interruptions.** "At most one, on the run aggregate" plus the re-closeout idempotency criterion reads naturally as at-most-one *active* record, but no test pins a second interruption after a previously resolved one (work A interrupted → recovers → completes → work B interrupted on the same run). The spec requirement ("every recoverable interruption SHALL carry a bounded recovery deadline") forces the correct behavior anyway; recommend pinning the sequential-interruption case in T-001's tests during implementation.
2. **Re-attachment mechanics are underspecified.** The journal `binding` records runtime/session/turn ids but no server endpoint, so re-attaching to a surviving OpenCode child after runner death is not concretely explained (the OS-assigned port is in-memory only; re-attachment presumably relies on newly spawned servers reading persisted session state). The fallback (re-execute under original identity) is spec-compliant and the acceptance criteria are fake-session-testable, so this is an implementation-clarification item, not a plan defect; T-006's SDK-pid open question is already tracked.
3. **In-runner Node-heap runaway is bounded only by the runner's own `MemoryMax`.** If the 9.7–20.2 GB growth lives in the runner process's heap (e.g., undici response buffering) rather than child processes, a recurrence still OOM-kills the whole Runner — now recoverably, but sibling work on that Runner still experiences the loss event. The design consciously rejects in-process policing (deferred to future telemetry); residual risk is accepted and the issue's acceptance criteria are met by the declared mechanism + tests.
4. **T-008 does not depend on T-006.** `runtime-quarantine-destroyed` is an observation reason, not a status-surface state, so this is defensible; implementers should still keep the reason-code list in T-008 aligned with what T-006 emits.
5. **Repo test-inventory convention not mentioned.** Server test-source changes require synchronizing the SpecUnitMigration ledger (surfaced repeatedly during #589); no task mentions it. The architecture gate fails loudly on drift, so this is friction, not a gap.
6. **Minor context imprecision:** the design says "the heartbeat route calls `HeartbeatAsync`, a no-op" — body-bearing heartbeats actually reach `HeartbeatRepairAsync`, which also does not refresh presence. The substantive claim (presence is poll-only) holds and T-005's fix covers both paths.
7. **Open questions are honestly scoped** (recovery-timeout default, reason-code wording, OpenCode server pid exposure, Pi process placement, container envelopes); none block the acceptance criteria, and T-006/T-007 pin verification of the SDK/pid and Pi-placement questions during implementation.

## Evidence of review

Static planning review; no implementation exists yet. Code-fact verification performed against the current workspace tree (`RunnerGrain.cs`, `DispatchService.cs`, `WorkflowRun.Work.cs`, `WorkflowGrain.Reports.cs`, `AgentJobGrain.cs`, `RunnerRoutes.cs`, `host.ts`, `server-process.ts`, `opencode/runtime.ts`, `terminal-task-log-delivery.ts`, `WorkflowStatusMapper.cs`, CLI command definitions, #589 artifacts under `openspec/changes/issue-589/`).
