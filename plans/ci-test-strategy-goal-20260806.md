# Goal: 测试策略调整——降低集成度，让 GitHub CI 在有限资源下更快

> 2026-08-06 立项。本文是主 agent（orchestrator）的统筹目标：引导方向、调度执行、验收把关，不亲自实现。
> 2026-08-06 修订（审查后）：明确执行模型、#553 收窄、新增 #554、验收方法修正。见 `plans/ci-strategy-review-20260806.md`。

## 一句话目标

在 GitHub CI（4 vCPU 慢机器）为唯一目标环境的前提下，通过降低 Spec 套件测试集成度，
把 CI 关键路径（.NET job，当前 ~332s）降到 **~280s 以下**，并让测试轨语义与"命名 + 目录"约定一致。

## ⚠️ 执行模型（关键约束）

- **执行由 herdr agent 完成，不调用 mo workflow。** orchestrator 不执行 `mo issue start`，
  不触发 `mohist/github-pr` 等 mo workflow profile——那些会让 mo 系统自己开 worktree 跑，与 herdr 执行层双线冲突。
- mo issue 在这里是**需求与验收单元**（Body 的 Done When 是验收清单），不是 workflow 触发器。
- 执行链路：orchestrator 统筹 → herdr thinker（设计/审计）/ worker（实施）在各自 worktree 执行 → 合并到集成点（本 worktree）→ operator 开 PR 集成 master。
- orchestrator 推进方式：读 goal/issue/review 文档 → 用 herdr 调度 thinker/worker → 收集证据 → 对照 Done When 验收。它不写代码、不改文件、不跑测试。

## 背景事实（已调研核实，2026-08-06）

- CI 关键路径 = .NET job：Build ~106s + Test ~194s（SpecTests 占 Test 的 98%）。Node job ~194s 不占瓶颈。
- SpecTests 3818 个测试构成：全栈（silo+web host+EF+SQLite）~1150 个占 ~100s；半集成 grain ~600；
  轻 DB ~200；**纯逻辑零集成 ~1407 个（37%，但 100% 依赖 SpecTests.Support，非机械迁移）**；
  串行 collection 133 个（AgentJobGrain 88 个待 CI 级 flaky 验证）。
- 已排除：maxParallelThreads 4 vs 8 无差异；4 项目并行争抢仅 11-12s；CI 机器比本地 2 核慢 ~35%（不可控）。
- 前置计划 `plans/dotnet-test-simplification.md` 的 Phase 1（trait 清零、orderer 删除）已通过 #137 落地；
  Phase 3（层间去重）未完成，与本次"矩阵下沉"是同一件事。

## 执行单元（Mohist issues）

| issue | 内容 | 集成度动作 |
|---|---|---|
| mo #553 | 关诊断日志（diagnosticMessages）+ AgentJobGrain 去串行（CI 级 flaky gate） | 配置调整 |
| mo #554 | 纯逻辑测试归位 unit 轨：先迁 SpecTests.Support 纯逻辑 helper 到 TestSupport 项目，再逐批迁零依赖文件 | 霰弹修改（非机械） |
| mo #546（父） | 集成测试状态矩阵下沉总览，不进 workflow；共享 collection 变更统一收口 | 统筹 |
| mo #550（子） | API 契约族：IntegrationApi/Telemetry/Misc/IssueProfile（~326 测试） | 矩阵下沉 |
| mo #551（子） | 会话与 Issue 族：IntegrationSessions/Issue/IssueLifecycle（~377 测试） | 矩阵下沉 |
| mo #552（子） | Workflow/Runner/平台族：IntegrationWorkflow/Runner/Platform/MohistIntegration（~448 测试） | 矩阵下沉 |

推进顺序：#553 先（纯配置，最快）；#554 与三族下沉（#550-552）可并行；
三族下沉先由 thinker 出审计方案（每族是否需新建半集成 fixture/collection），共享 collection 定义变更集中到父 #546 一次提交，避免三子并行改 MohistCollections.cs 冲突。

## 分工与工作方式（并行）

- **主 agent（orchestrator，codex gpt-5.6-luna max）**：唯一统筹者。只做方向引导、任务切分、验收把关、冲突仲裁。不写代码、不改文件、不跑测试、**不启动 mo workflow**。用 herdr 调度 thinker/worker。
- **thinker（pi zai-coding-cn/glm-5.2 max）**：宝贵智力资源，仅在需要抽象思维时使用——下沉方案设计、逐文件审计判定（契约 vs 矩阵）、等价覆盖风险评估、Support helper 分类。
- **worker（pi opencode-go/deepseek-v4-flash max）**：廉价劳动力，绝大多数场景——配置调整、helper 迁移、测试重写落盘、跑测试、收集对比数据、代码审查。
- 回退链：模型不可用时按 worker → orchestrator → xhigh 回退。
- **集成点**：主 agent 所在 worktree（本分支 test/ci-test-strategy-20260806）为暂时集成点，各执行 worktree 合并到此；全部验收通过后由 operator 开 PR 集成 master。

## 验收（每项可量化）

1. **#553**：CI 日志完整可见不再截断（diagnosticMessages 关闭）；AgentJobGrain 去串行后 CI 并行跑 ≥10 次无 flaky，否则回退保留串行并记录原因；SpecTests 全量绿。
2. **#554**：纯逻辑 helper 与集成 helper 分离（TestSupport 项目落地）；纯逻辑测试逐批归位 UnitTests；spec-file-size-baseline.json 更新、ArchTests 绿；SpecTests 与 UnitTests 全量绿、TRX 总数对账。
3. **#550/#551/#552**：每族全栈测试数量下降；一处行为变化只改一个测试文件；spec-file-size-baseline.json 更新、ArchTests 绿；全量绿无 flaky。
4. **总目标**：CI .NET job 从 ~332s 降到 ~280s 以下。
   - **CI 单次对比方差大，仅作辅助信号**；硬 gate 用本地 2 核限核实验多次中位数（参考调研：本地 2 核 SpecTests ~141s）。
5. 每个执行单元 Done When 全部勾选；GitHub 侧（#288-292）记录同步。

## 非目标（明确不做）

- **不启动 mo workflow**（执行走 herdr）。
- 不合并 fixture；不做 fixture 启动优化。
- 不加测试时长预算 step；不延长 CI 超时。
- 不自托管 runner；不动产品行为；不加新 skip。
- 不动 `OtelTracing` 串行（进程级 ActivitySource 隔离，必须保留）。
- 不做多轮线程数调优实验（已证明无差异）。
