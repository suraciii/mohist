# 审查：测试策略调整方案的影响与风险

> 2026-08-06 审查人：pi 主会话（统筹层）。对象：`plans/ci-test-strategy-goal-20260806.md` + mo #553/#546/#550-552。
> 结论：方向正确，但有 **1 个致命遗漏**（会让执行直接失败）和 4 个需修正的风险。

## 🔴 致命：#553「纯逻辑迁移」不是机械操作，会让 worker 直接编译失败

**事实**：
- 全部 240 个"纯逻辑"测试文件 100% `using Mohist.Server.SpecTests.Support`（实测：240/240）。
- `Mohist.Server.UnitTests.csproj` **不引用** SpecTests 项目（只引用 Mohist.Server + Mohist.Cli）。
- `SpecTests/Support/` 有 60 个文件 / 432KB 的 helper、fake、TestDatabase、RecordingGrainFactory 等。

**后果**：goal/issue body 把第三步定性为"机械移动/重命名/命名空间调整"是错的。直接迁移到 UnitTests 会大面积编译失败（找不到 Support 符号）。

**修正方向**（任选其一，交 orchestrator 决策）：
1. 收窄范围：只迁移"零 Support 依赖"的纯逻辑文件（需先扫描出这批子集，可能远少于 168）。
2. 先迁移共享 Support helper：识别纯逻辑 helper（无 silo/EF/HTTP 依赖）迁到 UnitTests 或新建 `Mohist.Server.TestSupport` 共享项目，集成测试继续从 SpecTests 引用。
3. 降级为 follow-up：把"纯逻辑归位"从 #553 拆出，#553 只做去串行化 + 关诊断日志（这两项确实是机械的）。

**当前 worker 正在做前两步（去串行化、关 diagnosticMessages），尚未撞墙，但第三步会失败。**

## 🟡 AgentJobGrain 串行依据需重新核实，方案断言过武断

**事实**：
- `OtelTracing` 集合同样带 `DisableParallelization = true`，且 MohistCollections.cs:106-118 有详细注释证明是**进程级 `Microsoft.AspNetCore` ActivitySource 污染**所必需（两个 OTel host 并行会互相注入 spans）。
- `AgentJobGrain` 集合带相同标记但**无注释说明依据**。fixture 共享状态是实例字段（`_sharedEventBus`、`_sharedEventStore`），非 static。
- MohistCollections.cs:30-33 注释提到 cluster-scoped 状态（`RunnerRegistryKeys.Global`、`IManagementGrain.ForceActivationCollection`、`FakeTimeProvider.Advance`）"lives inside each collection's own cluster"，暗示**这些状态本就不跨 collection**——支持 AgentJobGrain 可去串行，但也说明这类隐藏依赖不易一眼识别。

**风险**：goal 写"已验证 3 次全绿"——本地 3 次不足以证明 CI 安全（CI 慢 35% + 邻居争抢，时序敏感的 flaky 只在慢机器暴露）。

**修正方向**：移除串行前，用 `--filter` 在 CI 触发该 collection 并行跑 ≥10 次；或保留串行（88 个测试串行的耗时收益有限，风险收益比不划算，可从 #553 剔除）。

## 🟡 三族并行会撞 Support / Collection 共享文件

**事实**：Issue 域 39/57、Workflow 域 31/71、Sessions 域 21/54 文件带 `[Collection(`，多族共用 `MohistIntegrationCollection`（`ICollectionFixture<MohistIntegrationFixture>`）。

**矛盾**：issue body 写"不改共享测试支撑文件（fixture 定义、集合定义）避免冲突"，但下沉若需新建半集成 fixture/collection，就**必须改 MohistCollections.cs**——三个 worker 同时改会冲突。

**修正方向**：thinker 审计方案须明确每族"新增 fixture"还是"复用现有 collection"；若需改 MohistCollections.cs，三族串行该步骤（或由 orchestrator 统一收口一次）。

## 🟡 ArchTests spec-file-size-baseline.json 会被触发

**事实**：`Mohist.Server.ArchTests/spec-file-size-baseline.json` 按路径记录 42 个文件大小，ratchet 语义（只许缩不许扩）。

**影响**：迁移改路径 → 旧条目失效、新路径未登记 → ArchTests 红。#553 和 #550-552 都需同步更新基线。

**修正方向**：每个执行单元的验收加一项"更新 spec-file-size-baseline.json 并保证 ArchTests 绿"。

## 🟡 验收方法不可靠：CI 单次对比方差大

**事实**：goal 验收"记录 CI 前后对比 wall time"。但调研本身用 60 次中位数——GitHub CI 单次运行方差极大（4 vCPU、共享邻居）。

**影响**：worker 跑一次 PR CI 对比会得出误导性结论（可能假阳性"降了"或假阴性）。

**修正方向**：CI 对比不作为硬性 gate，改用本地限核实验（2 核）+ 多次中位数；CI 侧只验证"全绿 + 无 flaky + 日志可读"。

## 🟢 确认无误的部分

- `design/testing.md:111` 层间规则与方案一致（API 层只留契约，矩阵归下层）——方向正确。
- 不动产品行为、不加 skip、不合并 fixture 的原则正确。
- `OtelTracing` 串行**绝不能动**（方案未触及，正确）——它是真实的进程级隔离需求。
- `maxParallelThreads` 不动（testing.md:130 已论证是内存上限非速度旋钮）——正确。

## 优先级建议

1. **立即**：给 ci-553-worker 修正第三步（纯逻辑迁移非机械，改用上面 3 个修正方向之一）。
2. **立即**：AgentJobGrain 去串行的 CI 级 flaky 验证（或从 #553 剔除）。
3. **thinker 产出前**：明确每族下沉是否需改 MohistCollections.cs，协调冲突。
4. **每个执行单元**：验收加"更新 spec-file-size-baseline.json + ArchTests 绿"。
5. **验收方法**：CI 对比降级为辅助信号，硬 gate 用本地限核多次中位数。
