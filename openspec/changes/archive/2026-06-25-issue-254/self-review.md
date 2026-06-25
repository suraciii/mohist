# Self Review Report

## Result: PASS

Reviewed `proposal.md`、`specs/cli-module-structure/spec.md`、`design.md`、`tasks.json` against issue #254（epic #22 代码复杂度热点治理）的实际需求与代码现状。

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `AttachConnectivity` 被 `design.md` 决策 3 与 `tasks.json` T-002 归入 InfoRenderer 的内容清单，但代码核查显示它是 `private static InfoService AttachConnectivity(...)`，在 `InfoCollector.cs:138` 的 `CollectAsync` 内被调用——把 `CheckServerConnectivityAsync`（采集期）算出的 connectivity 挂到 runner 记录上以组装 `InfoResult`。属**采集期数据装配**，非渲染；放进"只依赖 TextWriter + InfoResult"的渲染器既违反变更原因归属，也与其使用点矛盾。
  Verification: 已把 `AttachConnectivity` 从 design.md 与 tasks.json T-002 的 InfoRenderer 清单移出，归入 InfoCollector 采集装配；spec req 4"采集器只持有采集与启发式"与之自洽（采集含 InfoResult 组装）。`rg`/脚本校验 tasks.json 不再出现 "InfoRenderer ... + AttachConnectivity"。
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: feasibility
  Evidence: T-002 与 T-003 都编辑 `Program.cs`（分别注册 InfoRenderer / 注册 Validator+Probe，不同行）。两者无输出依赖（独立功能模块），但自主执行若并行会在共享文件上冲突。原 `dependsOn` 均为空，仅靠 priority 隐式串行，对自主 agent 不够稳健。
  Verification: 为 T-003 增加 `dependsOn: ["T-002"]`（priority 2 < 3，合法）显式串行 Program.cs 编辑；note 注明这是文件冲突规避而非输出依赖。重新跑 DAG 校验：无环、所有 dependsOn 指向更低 priority。
  Status: resolved

## Blocking Items

（无）

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: 每个 task 的 `spec` 字段是单串，只能指向一个 requirement 锚点。spec req 2（更新协作类依赖收窄）与 req 5（CLI 行为逐字节不变）没有专属的 `spec` 指针——但二者已被 T-003 / T-001 / T-002 / T-004 的 acceptanceCriteria 实质覆盖（如 T-003 AC"构造器注入依赖数严格少于 12"、各任务 AC"npm test 全绿 / 输出逐字节一致"）。6 条 requirement 在实质上全部有任务覆盖。
  SuggestedAction: 执行阶段无需处理；若后续工具强校验"每条 requirement 至少被一个 spec 指针引用"，可允许 `spec` 字段为数组或为 req 2/5 增设独立 REVIEW 子任务。
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: consistency
  Evidence: tasks.json 的 `spec` 锚点（如 `#信息模块分离采集渲染与-systemd-解析`）依赖 Markdown 标题生成 anchor 的规则（CJK 与中文顿号"、" 的处理），不同渲染器可能产生细微差异。spec 文件路径与 requirement 名称本身正确可读，锚点偏移不影响人工定位。
  SuggestedAction: 若后续自动化按锚点严格跳转失败，再统一锚点格式；当前不修（修复 CJK 锚点反而可能引入新不一致）。
  Status: follow-up

## 核查小结

- **alignment**：proposal 的 4 条 What Changes 与 issue 的 3 个拆分目标（更新模块 / 表格渲染器 / 环境信息采集器）一一对应；issue 全部 7 条 Acceptance Criteria 与 5 条 Non-Goals 均有 spec requirement / 任务 AC / design 决策承接，无遗漏或误读。
- **completeness**：6 条 spec requirement 覆盖三个模块 + 行为守恒 + 复杂度目标；每条 requirement 有任务承载（含 item-3 所述的指针粒度细节）。
- **consistency**：spec 文件 `specs/cli-module-structure/spec.md` 与 proposal 声明的 new capability `cli-module-structure` 一致；协作类命名（RuntimeConsistencyValidator / ServiceReadinessProbe / InfoRenderer / SystemdUnitParser / RunnerRefreshOutcome）跨四份文档一致；design 决策与 spec requirement 对齐。
- **feasibility**：任务按功能模块切分（一个目标文件 = 一个完整功能切片，含提取 + 接线 + 测试），无"定义接口/注册DI/移动文件/单独测试"等过细任务；测试已折叠进 T-001/T-002/T-003。
- **dependency_completeness**：4 任务为 DAG，无环；T-004 依赖 T-001/T-002/T-003，T-003 依赖 T-002，所有 dependsOn 指向更低 priority 的存在 ID。

<promise>PASS</promise>
