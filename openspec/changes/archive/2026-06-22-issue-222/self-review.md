# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: 交叉核对四个任务的 `spec` 锚点与 spec 文件中的 `### Requirement:` 标题。T-001→`Runner definition state is persisted`、T-002→`Persisted slots are the sole authoritative source for dispatch capacity`、T-003→`Runner is a global execution resource`、T-004→`Runner slots configuration endpoint`，四者与 spec 标题逐字一致，无修复需要。
  Verification: `grep "^### Requirement:"` 输出与 tasks.json 的 spec 字段比对，全部精确匹配。
  Status: resolved（确认无误，无需改动）

## Blocking Items

（无）

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: runner-management 的次要需求（`Runner slots are configurable through the control plane`、`Runner slot capacity invariant`、`Workflow run assignment is globally unique`、`Runner executes only work it has claimed`、`Runner lifecycle transitions`）与 http-api 的 `Runner register and heartbeat concurrency field is non-authoritative for dispatch` 未单独在某个任务的 `spec` 字段中锚定，但均在对应任务的 `acceptanceCriteria` 中显式覆盖（T-002 的 AC 列出 slot 不变量、上报值忽略、lifecycle/ownership/global-uniqueness 回归；T-002+T-004 联合覆盖 configurable；T-004 覆盖非正值拒绝）。
  SuggestedAction: 实现阶段如需更强可追溯性，可在 T-002 的 notes 补一句"同时实现 runner-management#Runner slots are configurable through the control plane 的 grain 侧与 http-api#non-authoritative 的降级"。当前 AC 文本已足够指导实现与验收，不构成阻塞。
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: `Runner slots are configurable through the control plane` 的"非正值拒绝"场景由 T-004（PATCH API 层 400）满足；`RunnerGrain.UpdateAsync` 本身未强制校验正整数（design Decision 5 把校验放在 API 层）。这是设计选择而非缺陷——API 是控制平面的接收点。
  SuggestedAction: 若实现时希望防御纵深，可在 `RunnerGrain.UpdateAsync` 内对非正值抛 ArgumentException。可选，非必需。
  Status: follow-up

## 评审小结

- **alignment**：proposal 的"What Changes"五条与 issue Scope 五项逐一对应；issue Non-goals（drain/pause、历史统计、heartbeat 改造、env 移除、UI 入口）均被尊重并显式排除。
- **completeness**：10 条 spec 需求（runner-management ×8 + http-api ×2）全部被任务覆盖；关键边界（grain 回收重激活、首次接入默认值、非正值拒绝、并发读写、迁移回滚）在 design 与任务 AC 中均有交代。
- **consistency**：proposal Capabilities（`runner-management` 新建、`http-api` 修改）与 specs/ 目录、tasks 引用路径命名一致；design 5 个 Decision 与 spec 需求对齐。
- **feasibility**：4 个任务均为完整功能切片（持久化层 / grain 权威源 / 全局化 / PATCH API），无"定义接口""注册 DI""单独测试"等过细拆分；每个任务自带测试 AC。
- **dependencies**：T-001 无依赖；T-002←T-001；T-003←T-002；T-004←T-002。DAG 无环，所有 `dependsOn` 指向存在且 priority 严格更低的任务。

<promise>PASS</promise>
