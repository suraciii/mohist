# .NET 测试精简方案：tricky 清零、霰弹收敛、体量瘦身

> 替代 `plans/dotnet-test-suite-simplification.md`（该计划假设已漂移，本方案按其
> 诊断重新出精简版）。每个 Phase 独立可落地、独立全绿，按价值排序，可在任意
> Phase 边界停下。

## 实施方式

- **在独立 worktree 实施**：`git worktree add ../mohist-test-simplification -b test/dotnet-test-simplification master`，全部改动发生在该 worktree 的分支上，主 checkout 的 master 不动。
- **plans/ 目录清场**：本方案落盘为 `plans/` 下唯一计划文件（`plans/dotnet-test-simplification.md`），删除其余三个旧计划（`dotnet-test-suite-simplification.md`、`node-test-cleanup.md`、`spectests-speed-phase2.md`）。作为 worktree 分支上的首个 commit。

## 现状事实（master @ 459daa8a2，已核实）

| 问题 | 事实 |
|---|---|
| trait 噪音 | 4,839 处 `[Trait(Traits.Speed/Sut...)]` 分布在 228 个文件；CI（ci.yml:54）与 npm test 全量无 filter，**零消费方**；`design/testing.md:19` 自己声明「轨道靠命名+目录，not runtime trait」 |
| tricky 调度 | `CostDescendingCollectionOrderer`（实际只剩「具名 collection 先行」两档权重）+ `xunit.runner.json` 的 `maxParallelThreads: 8` + 5 个数字分片 collection（`WorkflowGrain2/3`、`MohistIntegration2`、`IntegrationIssue2/3`） |
| 层间重复 | API(Integration) 层 ~830 个测试中 **35–40% 严格重复**下层已测场景，15–20% 行为错位（逻辑在下层却只有 API 层在测）；典型：`IssueMetricsApiSpecs`(53) vs `IssueMetricsQuerierSpecs`(73)、EpicListQuery 85%、Agent cost/usage 80%、EpicBatchMembership/RunnerStatus/ProjectApi ~75% |
| 复制粘贴 | 61 个文件手写 keeper+`CopyTo`+options 样板；全局 30 份 `TestDatabase`、43 份 `TestDbContextFactory`；Epic 域 9 份 `SeedEpicAsync`、4 份已漂移的 `RecordingGrainFactory` |
| 死代码 | Support/ 下 `AgentSessionPersistenceTestHelper`、`RunnerPollClient` 0 引用；fixture 死成员（`EventBus`、`LogsPath` 等 0–2 引用）；`WorkflowGrainTestHelpers.PollWorkAsync` 静态版直接 throw |
| 巨型文件 | 58 个 >24KB（testing.md 声称 24KB enforced，实际无守卫）；Top：IssueMetricsQuerier 154KB、IssueMetricsApi 97KB、UpdateTests 97KB（**SUT 是 CLI 的 SourceCodeUpdater，放错项目**，`Mohist.Cli.Tests` 已存在） |
| 好榜样 | `WorkflowRunControlApiSpecs`：API 层只测 guard/契约，行为留给 grain 层——本方案要把这个模式推广 |

## 三原则

1. **最低可用层拥有行为矩阵**：Querier/Grain/Domain 层测全场景；API 层只留路由/绑定/状态码/JSON 形状/envelope/参数解析/removed-route 404/每端点一条成功路径。
2. **共享 setup 单点化**：seed/fake/DB 样板一份实现，改 schema 语义只动一处。
3. **调度归零**：测试顺序、分片、线程数不进架构；跑多久是跑多久。

## Phase 0 — worktree 搭建 + plans 清场 + 基线

1. 创建 worktree 与分支：`git worktree add ../mohist-test-simplification -b test/dotnet-test-simplification master`。
2. 在 worktree 内把本方案写入 `plans/dotnet-test-simplification.md`，删除 `plans/` 下其余三个旧计划文件，作为首个 commit。
3. 基线测量（在 worktree 内）：

```bash
dotnet build Mohist.sln -p:SkipWebBuild=true
/usr/bin/time -f 'elapsed=%e' dotnet test Mohist.sln -p:SkipWebBuild=true --no-build   # ×2，记录 wall time + total/pass/skip
```

Gate：两次全绿。TRX 存 /tmp，供 Phase 3 对比测试数。

## Phase 1 — 删 tricky 机制

1. **删 trait**：脚本删除全部 `[Trait(Traits.Speed...)]` / `[Trait(Traits.Sut...)]` 行（228 文件）+ 删 `Support/Traits.cs`。机械变更，零行为影响。`rg 'Traits\.' packages/server/tests` 须零命中。
2. **删 orderer**：删 `Support/CostDescendingCollectionOrderer.cs`（含 assembly attribute）。全量跑 2 次对比基线，wall time 回退 >10% 才允许恢复并记录原因（预计影响小：现 orderer 只有两档权重）。
3. **xunit.runner.json 去留**：有/无 `maxParallelThreads: 8` 各测 2 次；差异 ≤5% 删文件，>5% 保留并在 testing.md 写一句理由。
4. **改 `design/testing.md`**：删 sharding/scheduling/trait 相关段落（:19、:104-105），写朴素模型：collection 只表达共享生命周期；无 orderer；无 Speed/SUT trait；层间去重规则（原则 1）。

Gate：全绿 + 时间门槛 + trait/orderer 搜索零命中。

## Phase 2 — 删死代码、收敛复制粘贴

1. **删死面**：0 引用 Support 类（`AgentSessionPersistenceTestHelper`、`RunnerPollClient`，删除前复核引用数）；fixture 死成员（先复核 `EventBus`/`LogsPath` 等确实 0 引用再删）；静态版 `PollWorkAsync`、`GrainTestConfig.HasWorkflowRunsStatusColumn`。
2. **DB 样板单点化**：Support/ 新增一个小 helper（如 `SqliteTestDatabase`）：一次调用完成 `new SqliteConnection`(shared-cache 内存) → Open → `MigratedSqliteTemplate.CopyTo` → 返回已配置 `DbContextOptions`（含 `PendingModelChangesWarning` 抑制）。逐文件替换 61 处手写样板。
3. **Epic 域 fake 归一**：`SeedEpicAsync`、`RecordingGrainFactory`、Epic 用 `TestDatabase`/`TestDbContextFactory` 抽到 `Support/Epic/`，替换 9+4 份复制（4 份 RecordingGrainFactory 已漂移，以覆盖事件记录语义最全的为准，diff 逐个核对）。
4. **低引用 Support 类下沉**：≤2 引用的（`InMemoryFileProvider`、`InMemoryLoggerProvider` 等）移到唯一消费方文件内或旁边。

Gate：全绿。

## Phase 3 — 层间去重（霰弹修改主战场）

每删一个 API 层测试，先在表格记录下沉去向（下层同名 spec 或新增下层测试）；禁止「下层没有就直接删」，错位行为先补下层测试再删上层。

1. **IssueMetrics 对（第一刀）**：`IssueMetricsQuerierSpecs` 补 CurrentTotal/PreviousTotal 窗口小计矩阵（13 个错位场景），顺手按 5 个指标组拆成 5 个文件（154KB 直接清零）；`IssueMetricsApiSpecs` 从 53 删到 ~20（保留 6 契约 + 14 RangeQuery 参数解析/时钟注入 + 每端点 1 成功路径）。
2. **Epic 域**：`EpicListQueryApiSpecs`（85% 重复）、`EpicBatchMembershipApiSpecs`（75%）按原则 1 裁剪；`EpicApiSpecs` 的 Start/Pause/Resume × 5 状态矩阵参数化为 `[Theory]`（action × fromStatus × expectedCode）。
3. **Agent/Runner/Project**：`AgentCostRollupApiSpecs`+`AgentUsageTimeseriesApiSpecs`（80%）、`RunnerStatusApiSpecs`（75%）、`ProjectApiSpecs`（75%）同法裁剪，grain/reporter 层补缺口。
4. **错位归位**：`Project/Api/ProjectWorkflowProfileManagerSpecs`、`ProjectWorkflowProfileDisabledSpecs`（0 处 HTTP 调用）移出 Api/ 目录；`IssueCreationSpecs` 文件内 8 个 grain/HTTP 双测场景去重（grain 留矩阵，HTTP 留一条 400 映射）；`IssueSessionApiSpecs` 与 `AgentSessionSpecs` 内重复的 endpoint 测试合并。

每域 Gate：该项目全绿、无新 skip、删除映射表逐条有去向、TRX 总数对账（删除数 = 重复数，新增数 = 下沉数）。

## Phase 4 — 巨型文件瘦身 + 归位

1. **`UnitTests/SystemSpecs/UpdateTests.cs`（97KB）移到 `Mohist.Cli.Tests`**（SUT 是 CLI `SourceCodeUpdater`），按子命令拆 4–5 文件，抽 updater 构造 helper 收敛 ~40 行/个的重复 arrange。
2. **Top 文件按主题拆**：`AgentSessionSpecs`（4 主题）、`SystemUpdateServiceSpecs`（主题拆 + 570 行 fake 基建抽共享）、`MohistLocalWorkflowProfileSpecs`（YAML parser/不变量下沉 UnitTests；`FakePromptLoader`/`FakeDbContextFactory` 外移 Support；3 个 StartWork integration 归位）、`WorkflowProfileManagerSpecs`（`ExpandTaskWith_*` 7 个纯函数测试下沉 UnitTests）、`WindowsInstallSpecs`（Server/Runner 镜像用例参数化，砍近半方法数）。
3. **ArchTests 补文件大小守卫**：24KB 上限 + ratchet 基线文件（对齐 `scripts/node-test-file-budget-baseline.json` 惯例），列表只许缩小不许扩大；本 Phase 结束后无文件 >100KB。

Gate：全绿 + ratchet 生效 + ArchTests 绿。

## Phase 5 — collection 语义命名 + 定稿

1. `WorkflowGrain2/3`、`MohistIntegration2`、`IntegrationIssue2/3` 按子域语义重命名分组（如 WorkflowExecution/WorkflowRecovery/WorkflowArtifacts、IssueLifecycle/IssueProfile），同 fixture 类型保持生命周期语义不变。
2. 连跑 5 次全量：出现状态泄漏 → 修归属或回退该分组；wall time 回退 >10% → 保留数字分片并在 testing.md 记一句「负载分片是有意为之的临时债」。
3. `design/testing.md` 定稿终态模型；收尾 commit。

## 明确不做（相对旧计划的裁剪）

- 不拆测试项目（不搞 ComponentSpecs/IntegrationSpecs/TestSupport 新项目）——目录+命名已够表达层级。
- 不重写 `MohistIntegrationFixture` 的 Services/Grains 访问模式（49 文件，收益不抵成本）；只删死成员。
- 不拆 `WorkflowGrainSpecs` 430 行基类（39 个派生类，列入将来再说）。
- 不碰产品行为、不加新 skip、不碰 runner/web/CLI 测试逻辑（UpdateTests 仅物理移动+拆分）。
- 不做多轮线程数调优实验（Phase 1.3 一次决策）。

## 风险

| 风险 | 应对 |
|---|---|
| 删 orderer/分片后墙钟回退 | 每步计时对比基线，>10% 回退该步并记录 |
| 层间去重误删有效测试 | 删除映射表 + TRX 总数对账，错位行为先补下层再删上层 |
| Epic fake 归一时语义漂移 | 4 份 RecordingGrainFactory diff 逐个核对，取语义最全者 |
| trait 删除是大 diff（228 文件） | 纯机械删除 + 编译验证，单独成 commit 便于 review |

## 验证命令（每 Phase）

```bash
dotnet build Mohist.sln -p:SkipWebBuild=true
dotnet test Mohist.sln -p:SkipWebBuild=true --no-build
rg 'Traits\.|CostDescendingCollectionOrderer' packages/server/tests   # Phase 1 后零命中
```
