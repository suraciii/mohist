# Self-Review — issue-520（第二轮，修复后复评）

复评对象：`proposal.md`、`specs/{agent-readiness,agent-availability,agent-concurrency}/spec.md`、`design.md`、`tasks.json`，对照 issue #520。本轮在前一轮 FAIL 及其修复之后进行。

结论：前一轮的阻塞问题均已修复，制品内部自洽、与验收标准对齐、可交付自主构建。仅余两条非阻塞的清晰性建议。**PASS**。

---

## 前一轮问题修复确认

- **H1（proposal↔design 在 follow-up 上直接矛盾）已修复**：proposal:9、concurrency spec「The gate applies consistently to every entry point」、design D5、tasks T-002 现统一表述——launch 达限进入等待；follow-up（会开始新执行且达限）以可重试背压信号拒绝、不排队（v1，完整排队列为后续）。四处一致，不再矛盾。
- **H2（readiness spec 描述了 design 判为不可行的主动确认机制）已修复**：readiness spec 三个场景已改为执行历史机制——Ready 需「结构完备 + 无缺口 + 有成功执行作正向证据」；Needs setup 为结构缺陷或执行揭示的配置类失败；Unknown 为从未执行/非结论性；并在要求文内显式声明「Server 不主动探测凭据」。与 design D1 一致。
- **M1（并发 grain 与架构 process-manager 约束的关系）已修复**：design D3 新增「架构归类」，将其定位为共享权威资源 grain（类比 `RunnerRegistryGrain`/`RunnerGrain`），而非命令串行 process manager；并约束 grant-on-release 异步派发、participant 不在通知栈回调，避免同步回调环。
- **M2（D4 无限等待缺少回收/边界）已修复**：design D4 新增「等待态唤醒与回收」：稳定等待 Job 卸下定时器、仅由 permit-grant 或 runner 上线唤醒、reminder 兜底、可见且可取消、无隐式超时失败。
- **L2（Availability 提示性）已修复**：design D2 注明 `CanStartNow` 是提交前提示、非派发预留（全局容量共享）。
- **L1（T-001 体量）/L3（T-004 数据已具备）**：非阻塞，按前一轮判断保留；L3 已核对 `AgentJobGrain.cs` 确认 Runner error code 作为 `FailureCategory` 已持久化，T-004 仅需分类映射。

## 非阻塞观察（建议后续抛光，不阻断构建）

### O1. concurrency spec 内部措辞存在表面张力（清晰性）
「Reaching the limit causes waiting, not failure」要求写「newly submitted work … SHALL enter a waiting state」，未限定 launch；而「The gate applies consistently to every entry point」明确 follow-up 达限是「拒绝而非排队」。两者对 follow-up 的表述需读者用「特殊覆盖一般」来调和。建议把前者显式限定为 launch，或交叉引用后者，消除表面矛盾。**不阻断**：tasks T-001/T-002 对 launch-等待 / follow-up-拒绝的描述明确无歧义，构建者按任务执行不会出错。

### O2. Readiness 在「执行定义被编辑后」的重置语义建议显式化（完整性）
readiness 要求已含「reflect the current definition and re-evaluated when the definition changes」，可隐含推出「编辑 model/runtime 后，旧的成功执行不再确认新定义→回退 Unknown 直到新定义执行」。建议把这条显式写出，避免实现者把编辑前的成功误用于编辑后的定义（出现对未验证新定义的陈旧 Ready）。**不阻断**：当前要求文已可推出正确行为，属于「点明更稳」。

## 验收标准覆盖核对

- AC#1（三态区分 + 缺口/下一步）：readiness spec + T-004 覆盖。✓
- AC#2（Runner/容量不改 Readiness，看到 Availability）：readiness 独立性 + availability + T-003 覆盖。✓
- AC#3（所有入口一致、达限等待不失败）：launch 由 T-001 等待；follow-up 由 T-002 以可重试背压拒绝（v1）。依「已接受的工作进入等待，达边界拒绝新输入」的可靠性契约解读，rejected-at-gate 的 follow-up 非已接受工作，不触发终态失败——与 AC#3「而不是失败」一致；完整 follow-up 排队已显式列为后续。可接受。✓（建议见 O1）
- AC#4（调低不停 active、不改 Session）：concurrency spec + T-001 覆盖。✓
- AC#5（调高后等待工作自动推进、无需重提）：launch 由 T-001 的 grant 推进覆盖；follow-up 无已接受等待项（达限即拒），不适用。✓
- AC#6（等待工作可见 + 原因）：availability + T-003/T-005 覆盖。✓
- Non-Goals：均被尊重。

tasks.json：JSON 合法、DAG 无环、`dependsOn` 均指向更低优先级、每任务含测试验收；spec 格式（`### Requirement`/`#### Scenario`、每要求≥1 场景）完整。

---

<promise>PASS</promise>
