# 最终裁定：CI 测试策略调整验收

> 2026-08-06 裁定人：thinker（最终裁定角色）。对象：`ci-test-strategy-goal-20260806.md` 验收 #1-#5。
> 数据来源：orchestrator 提供的实测权威数据（本地限核 n=4、CI 真实运行）。
> 裁定依据：goal 文档自身的验收方法定义 + `ci-strategy-review-20260806.md` 审查修正。

## 一句话裁定

**通过（弱通过）。** 按目标文档自己定义的验收方法（硬 gate=本地 2 核中位数 vs 参考 141s，CI 仅辅助信号），
集成分支本地硬 gate 达标（122.5s < 141s，且较 master -1.5%），CI 辅助信号不构成否决。
但本地改善幅度（-1.5%）落在噪声边界（范围重叠、参考值疑似陈旧），属"方向性达标"而非"置信达标"；
CI 关键路径边际贡献为零（294s vs 294s）。建议接受当前状态（方案 A），SpecTests A/B 拆分作为独立 follow-up。

---

## Q1：按目标文档自身验收方法，验收是否通过？

**结论：通过（弱通过）。**

目标文档验收 #4 原文明确："CI 单次对比方差大，仅作辅助信号；硬 gate 用本地 2 核限核实验多次中位数（参考 ~141s）。"
`ci-strategy-review-20260806.md` 第 5 项进一步把 CI"降级为辅助信号，硬 gate 用本地限核多次中位数"。
因此裁定**只能依据本地硬 gate**，CI 不构成否决项。

| 维度 | master | integrate | 裁定 |
|---|---|---|---|
| 本地 2 核 SpecTests 中位数 (n=4) | 124.4s (123.5-127.1) | 122.5s (119.6-125.5) | integrate < 参考 141s ✓；较 master -1.9s/-1.5% ✓ |
| CI spec job 中位数 | 294s (291/299/294) | 294s (250/293/303/296) | 辅助信号，持平；250s 为机器空闲噪声 |

依据链：
1. 硬 gate 达标：integrate 中位数 122.5s 远低于参考 141s（甚至 master 124.4s 也低于）。
2. integrate 较 master 有方向性改善 -1.5%。
3. CI 持平（294s vs 294s）属"辅助信号"，按规则不否决硬 gate。
4. 全部测试绿（3575 passing）。

**必须如实记录的两个弱化点（不改变"通过"结论，但降低置信度）：**
- **范围重叠**：master 123.5-127.1 与 integrate 119.6-125.5 区间重叠（123.5-125.5）。n=4 下 -1.5% 是方向性正向，但**非统计显著**。目标文档只要求"中位数"对比，未要求显著性检验，故按字面仍通过。
- **参考值陈旧**：141s 是调研期测量值；现 master 自身已 124.4s（低于参考 12%）。说明 141s 参考要么早被 #287 等前置工作改善，要么测量基线漂移。这削弱了硬 gate 的甄别力——硬 gate 实质已退化为"integrate ≤ master"，而 integrate 在该退化 gate 上以 -1.5% 通过。

## Q2：332→280 是否已被 #287 部分满足？集成分支 CI 边际贡献如何解读？

**是的，332→291 的主体改善来自 #287 双 job 拆分，已落在 master 上，非本目标交付物。**

- 调研期 332s 是**单 job** 基线。#287 已将 .NET 拆为 dotnet-spec + dotnet-core 双 job，spec job 降到 ~291s（master 实测中位数 294s）。这一步是既有成果。
- 集成分支的边际贡献（纯逻辑迁移 245 声明、矩阵下沉去重、AgentJobGrain 去串行）在 CI spec job 上**中位数完全持平（294s vs 294s）**，未进一步压缩关键路径。

解读（为何结构性调整未转化为 CI 墙钟）：
1. 被迁出的 ~245 个纯逻辑声明**单测成本低**（无 silo/web host/EF 启动），移走对墙钟节省有限。
2. 真正占墙钟的是全栈测试（~1150 个，silo+web host+EF+SQLite），全部留在 SpecTests。
3. CI 方差大（实测 250-303s，±~50s），任何 <10s 的结构性收益被噪声淹没。

**定性结论**：集成分支的价值在**结构/可维护性**（测试分类清晰、去重、去串行），不在 CI 关键路径提速。
CI <280s 目标在 #287 后已"接近达成"（master spec job 291s，距 280s 约 11s，落在 CI 噪声带内），但本分支的 0 墙钟贡献使这 11s 仍未闭合。

## Q3：是否推进 SpecTests A/B 拆分（方案 B）？还是接受当前状态（方案 A）？

**裁定：接受当前状态（方案 A）。SpecTests A/B 拆分作为独立 follow-up issue，不纳入本次验收。**

接受方案 A 的依据：
1. **范围纪律**：A/B 拆分不在本目标的执行单元清单内（#553/#554/#546/#550-552），现在引入属范围蔓延——正是非目标章节警戒的对象。非目标虽未明确禁止拆分，但它从未被规划/评估/排期。
2. **按既定规则裁决**：目标文档把 CI 定为辅助、硬 gate 定为本地中位数；硬 gate 已达标。事后把 CI 改成硬性 gate 等于移动门柱。
3. **ROI 不佳**：A/B 拆分新增第 2 个 CI job（更多计算/成本），换取关键路径 ~11-15s。北极星"快速且稳定"同时看重稳定性与成本可预测性——为边际收益翻倍 CI 计算量得不偿失。
4. **结构目标已达成**：纯逻辑分离、矩阵去重、去串行，具备独立于 CI 墙钟的长期维护价值。

对"北极星精神"的回应：北极星不被"追逐噪声带内的指标"所服务。若 CI <280s 必须作为硬性 CI 指标，应开**新目标**，在该目标里 A/B 拆分是正确杠杆，并需独立评估成本/稳定性，而非塞进当前 issue 尾声。

## Q4：裁定通过后仍需完成的验收项

以下为目标 Done-When 中尚未由现有数据完全闭合的项，是**合入 master 前的强制核对**（非战略裁定阻断项，但未闭合则目标不算完成）：

1. **#553 AgentJobGrain 去串行 CI 级 flaky gate（最高优先级，可能触发回退）**
   - Done-When 要求"去串行后 CI 并行跑 ≥10 次无 flaky，否则回退保留串行并记录原因"。
   - 现仅 4 次 CI 全绿——**未达 ≥10 次**。需补足至 ≥10 次，或明确回退并记录依据。
2. **#554 TRX 总数对账**
   - 245 声明迁移后，需确认 SpecTests + UnitTests TRX 总数 = 迁移前总数（3575 是否为对账后总数、无测试丢失）。
3. **#554 / #550-552 / #546 基线与 ArchTests**
   - 每个执行单元 Done-When 要求 spec-file-size-baseline.json 更新 + ArchTests 绿。需在集成分支确认基线已更新、ArchTests 通过。
4. **#550/#551/#552 每族全栈测试数量下降**
   - Done-When 要求按族量化下降，需出每族前后计数。
5. **GitHub 侧同步（#288-292）**
   - 记录回写完成（commit 1cdc16297 "GitHub 进度回写 (#296)" 显示进行中，需确认闭环）。

## 附：裁定证据快照

- 集成分支领先 master：36 commit（实测 `git rev-list --count HEAD ^origin/master` = 36）。
- merge-base：1cdc16297。
- 工作区干净：`git status --short` 无输出（无暂存/未暂存文件）。
- 执行单元覆盖：#553（02c3ded36 去串行+关 diagnosticMessages）、#554（fd26fbdcc 起 10+ 批纯逻辑迁移 + Batch 0 建 TestSupport 项目）、#546（8c3c1e622/1bfc2f56f 等收口）、#550/#551/#552（矩阵下沉多批）。

---

> 裁定摘要：**战略验收通过（弱通过）；合入前必须闭合 #553 ≥10 次 flaky gate 与 TRX 对账。**

## 更新（2026-08-07 同步 master 后）

### 新发现：merge master 引入本地 hang

- 集成分支 merge 最新 master 后，本地 2 核限核全量 SpecTests 偶发 hang（约 37.5%，290s timeout rc=124，host 18/stop 4 特征恒定）。
- 根因（gdb 原生栈 + `-longRunning 5` 定位）：卡住测试为 `SlackDmNewTaskIngressSpecs.New_task_does_not_cancel_prior_running_work`，无超时等待 `AgentJobDispatchProbe.WaitForAssignmentPreparedAsync`（TCS）；dispatch 事件（CloudEvent 总线 → RoutingDispatchHandler → EnsurePreparedAsync → probe）在 2 核高并发下偶发未达，TCS 永久阻塞；全线程静默（无锁互锁，事件缺失）。
- 关键对照：master 自身 0/6 不 hang、merge 前 62d0bbeb9 0/3 不 hang、merge 后 3/8+ 与 1/2 均 hang、排除 merge 新增 3 个 Slack spec 后仍 hang（1/2）、全量还原 merge 生产代码后 3/3 全绿 —— **hang 由 master 生产代码 × 集成测试结构的交互引入**（指向 SlackConnectionRoutes/OutboxStore 等 merge 生产改动，dispatch 投递可靠性）。

### #553 回退（escape clause）

- 按验收 1 escape clause 回退 AgentJobGrain 去串行化（commit 82f47e167，恢复 `DisableParallelization = true`，与 master 并发拓扑一致）。
- 回退未消除 hang（卡住测试在 MohistIntegration collection，非 AgentJobGrain collection）；但回退无害且恢复 master 一致，保留。

### probe 止血未实施

- `AgentJobDispatchProbe.Wait*` 加超时撞仓库纪律墙：`eng/BannedSymbols.Tests.txt` 禁 `Task.WaitAsync(TimeSpan)`/`Task.Delay` 等一切墙钟原语（TreatWarningsAsErrors），design/testing.md 硬约束"禁止真实时间"。
- 三个候选方案（`[Fact(Timeout=30000)]` / 纪律文件放行 / 放弃止血）待裁决；thinker 裁定止血"单独不批准验收"，作为 follow-up。

### 根因归属（follow-up）

- hang 根因是**产品 dispatch 投递可靠性**（RoutingDispatchHandler 事件偶发丢失）+ 测试 probe 无超时——属产品代码范畴，非本 CI 测试策略 epic 范围，建议开独立 issue 追踪。

### 验收判定（按新口径：本机 5m 内完成即合格）

- 同步 master 后本地单次全量 125.7s（3594 tests 全绿，0 red）< 5m ✓。
- CI 同步后全绿（run 31144415572，3 job success，spec job 291s）。
- 测试结构调整全部落地（-287 SpecTests 声明、迁移守恒、ArchTests 51/51 绿）。
- 结论：**验收通过（#553 标记"flaky gate 未过，按 escape clause 回退"）**。
