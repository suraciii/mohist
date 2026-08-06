# Goal: 测试策略调整——降低集成度，让 GitHub CI 在有限资源下更快

> 2026-08-06 立项。本文是主 agent（orchestrator）的统筹目标：引导方向、调度执行、验收把关，不亲自实现。

## 一句话目标

在 GitHub CI（4 vCPU 慢机器）为唯一目标环境的前提下，通过降低 Spec 套件测试集成度，
把 CI 关键路径（.NET job，当前 ~332s）降到 **~280s 以下**，并让测试轨语义与"命名 + 目录"约定一致。

## 背景事实（已调研核实，2026-08-06）

- CI 关键路径 = .NET job：Build ~106s + Test ~194s（SpecTests 占 Test 的 98%）。Node job ~194s 不占瓶颈。
- SpecTests 3818 个测试构成：全栈（silo+web host+EF+SQLite）~1150 个占 ~100s；半集成 grain ~600；
  轻 DB ~200；**纯逻辑零集成 1407 个（37%）**；串行 collection 133 个（AgentJobGrain 88 个无串行依据）。
- 已排除的因素：maxParallelThreads 4 vs 8 无差异；diagnosticMessages 本地无耗时影响但日志爆炸（CI 日志被截断到 27KB）；
  4 项目并行争抢仅 11-12s。CI 机器本身比本地 2 核慢 ~35%（不可控，不投入）。
- 前置计划 `plans/dotnet-test-simplification.md` 的 Phase 1（trait 清零、orderer 删除）已通过 #137 落地；
  **Phase 3（层间去重）未完成**，与本次"矩阵下沉"是同一件事，以本次 issue 拆分为准推进。

## 执行单元（Mohist issues，全部 ready、p2、mohist/github-pr 流程）

| issue | 内容 | 集成度动作 |
|---|---|---|
| mo #553 | Spec 套件轻量化：AgentJobGrain 去串行化（已验证 3 次全绿无 flaky）、关闭 diagnosticMessages、168 个纯逻辑文件（1407 测试）归位 unit 轨 | 结构性归位，不重写行为 |
| mo #546（父） | 集成测试状态矩阵下沉总览，不进 workflow | 统筹 |
| mo #550（子） | API 契约族：IntegrationApi/IntegrationTelemetry/IntegrationMisc/IssueProfile（~326 测试） | 矩阵下沉 |
| mo #551（子） | 会话与 Issue 族：IntegrationSessions/IntegrationIssue/IssueLifecycle（~377 测试） | 矩阵下沉 |
| mo #552（子） | Workflow/Runner/平台族：IntegrationWorkflow/IntegrationRunner/PlatformIntegration/MohistIntegration（~448 测试） | 矩阵下沉 |

推进顺序：#553 先（低风险、收益快、让审计范围变干净）；#546 start 后三个子 issue 并行
（文件不重叠，不同集合族；约定不改共享测试支撑文件避免冲突）。

## 分工与工作方式（并行）

- **主 agent（orchestrator）**：唯一统筹者。只做方向引导、任务切分、验收把关、冲突仲裁。
  不写代码、不改文件。通过 mission goal 持续推进。
- **thinker（zai-coding-cn/glm-5.2 max）**：宝贵智力资源，仅在需要抽象思维时使用——
  下沉方案设计、逐文件审计判定（契约 vs 矩阵）、等价覆盖风险评估、testing.md 增量。
- **worker（opencode-go/deepseek-v4-flash max）**：廉价劳动力，绝大多数场景——
  机械移动/重命名/命名空间调整、测试重写落盘、跑测试、收集 CI 对比数据、代码审查。
- 回退链：模型不可用时按 worker → orchestrator → xhigh 回退。
- **集成点**：主 agent 所在 worktree（本分支）为暂时集成点，各执行分支合并到此；
  全部验收通过后开 PR 集成 master。

## 验收（每项可量化）

1. mo #553 落地后：SpecTests 进程 CI 耗时下降 ≥20s；CI 日志不再截断；SpecTests 项目零集成测试可机械核查（0 个无 fixture/grain/http 的测试文件）；全量绿、AgentJobGrain 并行连跑 3 次无 flaky。
2. mo #550/#551/#552 逐个落地后：对应族全栈测试数量下降；CI .NET job 时长下降（每片记录前后对比）；一处行为变化只改一个测试文件（抽样核查）；全量绿无 flaky。
3. 总目标：CI .NET job 从 ~332s 降到 ~280s 以下；design/testing.md 的轨放置与层间去重规则与实际代码一致。
4. 每个子 issue 的 Body 中 Done When 全部勾选，GitHub 侧记录同步。

## 非目标（明确不做）

- 不合并 fixture（共享 fixture 会串行化 collection）；不做 fixture 启动优化。
- 不加测试时长预算 step（operator 已明确否决）；不延长 CI 超时。
- 不自托管 runner；不动产品行为；不加新 skip。
- 不做多轮线程数调优实验（已证明无差异）。
- 不做旧 plan 的 Phase 2/4/5 剩余项（死代码清理、巨型文件拆分）——除非本目标完成后 operator 另行立项。
