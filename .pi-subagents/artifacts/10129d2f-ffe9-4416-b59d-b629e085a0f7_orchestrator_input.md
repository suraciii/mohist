# Task for orchestrator

你是 Mohist CI 测试策略调整的唯一统筹者（只引导方向、调度、验收，不实现）。

北极星文档：plans/ci-test-strategy-goal-20260806.md（本 worktree 已提交）。执行单元：
1. mo #553（Spec 套件轻量化：AgentJobGrain 去串行化、关闭 diagnosticMessages、168 个纯逻辑文件/1407 测试归位 unit 轨）——先做，低风险收益快。
2. mo #546 父 + #550/#551/#552 子（Integration 系列状态矩阵下沉，三族并行，文件不重叠；约定不改共享测试支撑文件）。

分工：thinker（zai-coding-cn/glm-5.2 max）仅在需要抽象思维时使用（下沉方案、逐文件审计判定、等价覆盖风险评估）；worker（opencode-go/deepseek-v4-flash max）承担绝大多数实施（机械移动、重写落盘、跑测试、CI 前后对比、审查）。模型不可用时按回退链处理。

工作方式：尽可能并行；每个执行单元：thinker 方案（需要时）→ worker 实施 → 验证全绿 → 你验收；验收标准 = goal 文档每项可量化指标 + issue body 的 Done When。全部完成后报告集成就绪状态（改动文件清单、验证证据、CI 对比、残留风险），PR 由 operator 开。铁律：不动产品行为、不加 skip、不合并 fixture、不延长 CI 超时、不加测试时长预算 step。

## Acceptance Contract
Acceptance level: attested
Completion is not accepted from prose alone. End with a structured acceptance report.

Criteria:
- criterion-1: Return concrete findings with file paths and severity when applicable

Required evidence: review-findings, residual-risks

Finish with a fenced JSON block tagged `acceptance-report` in this shape:
Use empty arrays when no items apply; array fields contain strings unless object entries are shown.
`criteriaSatisfied[].status` must be exactly one of: satisfied, not-satisfied, not-applicable.
`commandsRun[].result` must be exactly one of: passed, failed, not-run.
`manualNotes` and `notes` are optional strings; an empty string means no note and does not satisfy `manual-notes` evidence.
```acceptance-report
{
  "criteriaSatisfied": [
    {
      "id": "criterion-1",
      "status": "satisfied",
      "evidence": "specific proof"
    }
  ],
  "changedFiles": [
    "src/file.ts"
  ],
  "testsAddedOrUpdated": [
    "test/file.test.ts"
  ],
  "commandsRun": [
    {
      "command": "command",
      "result": "passed",
      "summary": "short result"
    }
  ],
  "validationOutput": [
    "validation output or concise summary"
  ],
  "residualRisks": [
    "none"
  ],
  "noStagedFiles": true,
  "diffSummary": "short description of the diff",
  "reviewFindings": [
    "blocker: file.ts:12 - issue found, or no blockers"
  ],
  "manualNotes": "anything else the parent should know"
}
```