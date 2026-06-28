# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The spec (`specs/workflow-supervision/spec.md:62`) described the
  `RunnerWorks` ledger parenthetically as "(grain storage)", and the proposal
  Impact section (`proposal.md:70`) said "new `RunnerWorks` table + grain
  storage". Both contradict design D1 (`design.md:96-129`), which explicitly and
  emphatically chooses an EF SQL table and **rejects** `[PersistentState]` grain
  storage (rationale: whole-state blob read/write degrades activation latency
  against unbounded history). The tasks reinforce D1
  ("Design D1 (EF table, NOT [PersistentState])"). A reader taking "(grain
  storage)" as a technical directive would contradict the decided architecture.
  Changed: removed the "(grain storage)" parenthetical from the spec (leaving
  the requirement implementation-neutral, matching the issue body which defers
  storage schema to implementation) and changed the proposal to "new
  `RunnerWorks` table (EF SQL)".
  Verification: `grep` confirms no remaining "grain storage" as a positive
  directive in spec/proposal; design D1 and tasks are the unchanged authority.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Task titles contain technical-action verbs (T-001 "注入 … 切换 …",
  T-002 "EF 表 + store + … 接入", T-003 "持久 reminder + 内存扫描 …"). The
  feasibility heuristic flags such phrasing. However each task is a complete,
  independently-verifiable feature slice (T-001 delivers a time-testable
  RunnerGrain with configurable `WorkCompletionTimeout` + `FakeTimeProvider`
  fixture; T-002 delivers the whole ledger module end-to-end; T-003 delivers the
  whole timeout safety net end-to-end). None is a standalone sub-step that
  cannot ship alone, and merging any would create an over-coarse task. T-001 in
  particular is a deliberate testability prerequisite that de-risks T-002/T-003
  and stands alone with green build+tests.
  SuggestedAction: Optionally reword titles to lead with the slice outcome
  (e.g. T-001 → "RunnerGrain 时间可测化与统一超时配置"). Not required for
  correctness; left as-is to avoid disturbing task references.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design Open Questions (reminder minimum-period floor vs. the ~1min
  scan target; the exact `IAgentJobGrain` synthesized-fail hook for agent-job
  timeout/runner-loss; whether `RecoverActiveWorkflowWorkAsync` survives as dead
  code after activation hydration) are explicitly deferred to implementation.
  They are already carried into T-003's notes as implementation-time
  confirmations, so no task is left without an owner.
  SuggestedAction: Resolve during T-003 implementation; add a small
  `IAgentJobGrain` fail hook if absent (already anticipated in D3 / T-003
  notes).
  Status: follow-up

---

Traceability summary (all issue Acceptance Criteria → spec scenario → task):

| AC | Spec scenario | Task |
|----|---------------|------|
| 1 取用时写入 RunnerWorks | 取用时插入台账行 | T-002 |
| 2 注册 per-runner reminder 扫描内存 | 使用持久 reminder 而非 grain timer; 扫描零 DB 读 | T-003 |
| 3 超时合成 failed(timeout) | RunnerGrain 控制面安全网兜底 | T-003 |
| 4 同步重启后孤儿被检测失败 | 跨重启检测孤儿 work | T-003 |
| 5 runner-loss 不变且互不干扰 | 超时与 runner-loss 合成互不干扰 | T-003 |
| 6 状态仅 outstanding\|completed\|failed | 状态扁平失败原因入 Reason | T-002 |
| 7 recovery task 走新 deadline | 恢复 task 作为新 work 走新 deadline | T-002 |
| 8 RecoverActiveWorkflowWorkAsync 不重置时钟 | RecoverActiveWorkflowWorkAsync 不重置时钟 | T-002 |
| 9 所有时间读取走 TimeProvider | 超时相关时间读取统一走 TimeProvider (+ 取用点经 TimeProvider 记 TakenAt) | T-001 / T-002 / T-003 |

Dependency chain is acyclic and priority-ordered: T-001 (priority 1, no deps)
→ T-002 (priority 2, depends T-001) → T-003 (priority 3, depends T-001+T-002).
All three task `spec` anchors resolve to existing `### Requirement:` headings in
`specs/workflow-supervision/spec.md`.

<promise>PASS</promise>
