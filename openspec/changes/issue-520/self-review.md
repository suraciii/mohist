# Self-Review — issue-520

评审对象：`proposal.md`、`specs/{agent-readiness,agent-availability,agent-concurrency}/spec.md`、`design.md`、`tasks.json`，对照 issue #520 的 User Voice / Product Shape / Domain Model / Acceptance Criteria / Non-Goals。

结论：发现必须在构建前修复的问题（跨制品自相矛盾、与验收标准偏离）。**FAIL**。

---

## High — 必须修复

### H1. Proposal 与 Design 在 follow-up 并发行为上直接矛盾，且偏离 AC #3

- `proposal.md:9` 明确写：「让 MaxConcurrentRuns …对所有调用入口一致生效（含 launch 与 **follow-up**…）：达到限制后提交的工作**进入等待**。」
- `design.md:74-78`（D5）却决定 follow-up 达限时「**以独立的并发原因拒绝**（可重试）…不做完整排队」。
- issue AC #3：「MaxConcurrentRuns 对所有调用入口一致生效；达到限制后提交的工作**进入等待而不是失败**。」follow-up 是「调用入口」之一，也是「提交的工作」。
- `specs/agent-concurrency/spec.md:17-19` 的 follow-up 场景只要求「受闸门约束、不绕过」，对「等待 vs 拒绝」保持中立，因此**没有**调和这一矛盾。

**影响**：proposal（follow-up 等待）与 design（follow-up 拒绝）互相冲突；无论实现者遵循哪一份，都会违反另一份，且二者合起来都不能同时满足 AC #3 对 follow-up 的字面要求（「进入等待」）。这是交付前必须收敛的硬矛盾。

**方向**：二选一并贯通三份制品——(a) 让 follow-up 也排队等待（需为 follow-up 物化轮次记录 + 给 `AgentSessionGrain` 加唤醒），或 (b) 把 AC #3 显式收窄为「launch 等待；follow-up 达限以可重试原因拒绝」，并同步修订 proposal 与 concurrency spec 使之与 design 一致、与 AC 对齐。当前 design 的 D5 取舍本身合理，但它是单方面偏离 proposal/AC，未回写到 spec/proposal。

### H2. Readiness 的 spec 场景描述的是一套 design 明确判为不可行的机制

- `specs/agent-readiness/spec.md:5-7`「Definition confirmed executable」→ Ready，要求「the Server can confirm the Agent's referenced Runtime, Model and Variant are **executable under the current configuration**」。
- `specs/agent-readiness/spec.md:13-15` Unknown 的示例是「the runtime capability **catalog is not currently readable**」。
- 但 `design.md:10,32-44`（D1）明确：Server 架构上**无法**主动确认凭据/executability（无凭据存储；`RunnerRegistryGrain` 目录是内存态、无 Runner 即空、且 `design/runtimes/opencode.md` 声明目录非执行合法性权威、不参与 readiness），Readiness 实际由**执行历史**派生（成功→Ready、配置类失败→Needs setup、从未执行/非结论性→Unknown），**根本不读目录**。

**影响**：spec 的三个场景示例（「confirm executable under current configuration」「catalog not readable」）与 design 的真实机制相反。实现者若按 spec 去做主动确认/读目录，会撞上 design 已判定的不可行；spec 与 design 必须先对齐才能交给自主构建。

**方向**：把 readiness spec 的场景改写为 design D1 的执行历史机制（Ready=定义完备且无已知缺口、且有成功执行作正向证据；Needs setup=结构缺口或执行已揭示的配置类失败；Unknown=从未执行/非结论性），或在该 spec 内显式标注 proactively-confirm 的限制。注意 design 的取景本身诚实且与 issue 一致（Unknown=Mohist 暂无法确认），问题在于 spec 没跟上。

---

## Medium — 建议修复

### M1. `AgentConcurrencyGrain` 的持久化许可/等待态与架构 process-manager 约束的关系未交代

- `design.md:57-66`（D3）按 agent 持久化「活动许可集合 + FIFO 等待者列表」。
- `design/architecture.md:137-186` 对持久化 application process manager 的约束很严：只持久化命令投递 fence（commandId/kind/payload/expectedRevision），**不**存业务/调度事实，且不得成为第二业务权威。
- design 只断言该 grain「只持有调度态、不存业务事实」，但未把它**映射**到架构认可的类别（是像 `RunnerRegistryGrain` 那样的共享资源 grain，还是受约束的 process manager），也未说明 FIFO 等待队列为何不违反「不持久化调度/业务态」。

**影响**：构建时可能触发 ArchTest 或边界争议；需在 design 中给出该 grain 的归类与正当性，否则实现者要自行猜测边界。

### M2. D4 无限等待缺少对「永久等待 AgentJob」的回收/边界

- `design.md:70-72`（D4）移除 `runner-unavailable` 终态失败后，缺 Runner/容量/并发的 AgentJob 无限等待。
- 与 WorkflowRun 不同，AgentJob 没有 issue 生命周期兜底；被用户放弃的等待 Job 会持续占用 grain + `agent-job-recovery` reminder。

**影响**：运维/资源累积风险在 design 的 Risks 中只提到 feature-flag 回退，未给等待 Job 的上限或清理。建议补一条回收策略或显式接受「无限等待 + 监控」。

---

## Low — 提示

### L1. T-001 体量偏大
T-001 把「新增有状态 grain（FIFO + 对账 + 持久化）+ `AgentJobGrain` dispatch 改造 + BREAKING 的 D4 移除 + 全量测试」捆在一个任务。耦合度高（grain 离开首消费者不可用）使合并有据，但交付风险偏高；若实施中过大，可把 grain 作为前置准备任务拆出。

### L2. Availability 的 `CanStartNow` 是建议性，非派发保证
Runner 容量是全局共享，两个 Agent 可同时读到「can start now」并在 dispatch 时争用。spec/design 未声明 Availability 只是提交前提示、不保证派发时仍有 slot，建议补一句以免被过度解读。

### L3. T-004 执行历史分类已具备数据基础（非阻塞）
已核对 `AgentJobGrain.cs:326-330,1600-1613`：Runner 的 error code 已作为 `FailureCategory` 持久化（precedence: output failureCategory → `WorkResult.Error.Code` → status）。因此 T-004 把配置类失败映射为 Readiness 缺口是可行的（只需加一层分类映射），原先担心的「需先增强结果捕获」不成立——此条仅供实现参考，不构成阻塞。

---

## 覆盖度核对

- AC#1（三态区分 + 缺口/下一步）：readiness spec + T-004 覆盖（受 H2 措辞影响）。
- AC#2（Runner/容量不改 Readiness，看到 Availability）：readiness 独立性 + availability 覆盖。✓
- AC#3（所有入口一致、达限等待不失败）：launch 由 T-001 覆盖；**follow-up 偏离（H1）**。
- AC#4（调低不停 active、不改 Session）：concurrency spec + T-001 覆盖。✓
- AC#5（调高后等待工作自动推进）：launch 由 T-001 覆盖；follow-up 无等待项（因 D5 拒绝），不适用。✓
- AC#6（等待工作可见 + 原因）：availability + T-003/T-005 覆盖。✓
- Non-Goals：均被尊重（执行定义内容、Web 布局、Slack Connection、跨 Agent 调度）。

tasks.json：JSON 合法、DAG 无环、`dependsOn` 均指向更低优先级任务、每个任务含测试验收。结构无误。

---

<promise>FAIL</promise>
