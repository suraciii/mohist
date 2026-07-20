---
status: wip
---

# Workflow Definition

Workflow Definition 是 Workflow Profile 的核心内容：声明阶段、任务、检查、审批点与恢复
规则的 YAML 文档。它是产品命令面之一，语法与作者可见语义的权威在
[`docs/workflow-definition.md`](../../docs/workflow-definition.md)。
本篇定义它的语义模型与唯一权威校验器；不复述语法。

## Model

### 语义模型

语义模型与载体语法分离：YAML 解析为下列类型。引擎、runner 与校验器只面对类型，不面对
语法树。

```text
WorkflowDefinition(Approval?, Stages[])
Approval(FeedbackTasks: Task[])
Stage(Name, RequiresApproval = false, LockBehavior?, Resources[], Tasks[], Checks[])
Task(Id, Uses, Title?, With?, Expect?, Artifacts?, SetVars?, Recovery?)
Expect(Files[]: FileExpectation(Path),
       Markers[]: MarkerExpectation(Path, OneOf[], FailIf?))
Artifacts(Files[]: ArtifactDeclaration(Path))
Recovery(Budget = 0, Handlers[]: Handler(When, Tasks[], RetrySelf = false))
Check(Id, Uses, Title?, With?)
```

- 语言的每个构造都有类型对应。`With` 是唯一的无类型部分：它是 Action 的自由输入
  （JSON blob），内部结构由各 Action 契约定义，definition 层不校验。
- `Expect` 是一等构造，位于 task 顶层，不进入 `With`。执行分工（executor 验证、合成
  promise output）见 [`actions.md`](actions.md) 与 [`task-dispatch.md`](task-dispatch.md)。
- 审批反馈任务就是有序的 `Task[]`，不设独立类型。全部完成后，当前 stage 的 checks
  重新执行。

### 不属于模型的

- 执行状态：`recoveryRemaining`、attempt、任务输出。不能在 YAML 中声明；见
  [`recovery.md`](recovery.md)。
- Variables 值与 Prompt 正文：独立资源，模型只持有 `${{ }}` 引用。
- Profile 元数据（id、名称、适用场景）：由 Profile 资源持有；definition 顶层只有
  `approval` 与 `stages`。

### 放置

模型、解析器与校验器放在独立类库 `Mohist.Workflow.Definition`，无 Orleans / ASP.NET
依赖（Orleans surrogate 留在 server，沿用现有 `WorkflowDefinitionSurrogates` 方式）。
server 与 CLI 都引用它：保存 API 与 `mo` 本地校验跑同一份代码。

## Semantics

### 解析即校验

唯一入口：`Parse(yaml) → Definition | Error[]`。

- 未知 key 是错误，不忽略、不降级为告警。agent 的生成—校验—修复循环依赖这个信号。
- 类型错误是错误。`budget: abc` 报错，不静默取默认值。
- 错误全量收集，不在首错中断。
- 每条错误 = YAML 路径 + 领域语言消息，不出现异常堆栈或实现术语：

```text
stages[1].tasks[0].recovery.handlers[0]: handler 需要声明 tasks 或 retrySelf 之一
```

### 校验规则

| 位置 | 规则 |
|---|---|
| 顶层 | 只允许 `approval`、`stages`；`stages` 非空 |
| approval.feedback | `tasks` 非空；每项遵守 task 规则 |
| stage | `stage` 名非空且在 definition 内唯一；`tasks` 非空 |
| stage | `lockBehavior` 仅允许 `sequential`，且必须与非空 `resources` 同时出现；`resources` 不得单独出现 |
| task | `id` 非空且在所属任务列表内唯一；`uses` 必填；`title` 可选 |
| expect | `files[].path` 非空；`markers[].oneOf` 非空；`failIf` 必须是 `oneOf` 的成员 |
| artifacts | `files[].path` 非空 |
| setVars | key 非空；值必须是 `output.` 开头的输出字段路径 |
| recovery | `budget` 为非负整数；`handlers` 非空、有序 |
| handler | 可选 `when` 形如 `field=value`，两侧非空；缺省时为唯一且最后一个默认 handler；至少声明 `tasks` 或 `retrySelf` 之一 |
| check | `id` 非空且在 stage 内唯一；`uses` 必填 |
| 模板 | 所有 `${{ }}` 可解析；根命名空间必须在产品参考的表内；`failure.*` 只允许出现在 recovery handler 的 tasks 内 |
| 模板 | `tasks.<id>` 引用的 id 必须是 definition 中声明的任务 |

### 校验入口

同一实现暴露三处，规则只有上表一份：

- Profile 保存 API：非法 definition 拒绝保存，返回错误列表。
- `mo workflow-profile validate <file>`：本地校验，不经服务器。
- CI：内置 profile 与 `docs/workflow-definition.md` 中的完整示例作为黄金用例，必须通过
  校验——语法参考与校验器由此互相锁定。含 `<...>` 占位符的骨架片段不进用例。

### 运行时任务

`WorkflowDefinition` 不是完整执行计划。`WorkflowRun` 启动时保存 definition snapshot；
运行期间 recovery、retry、审批反馈和控制命令（如 `mo issue rebase` 插入
`uses: mohist/rebase`）都可以产生新的 `TaskRun`，使用相同的 dispatch、report 与
Variables 解析语义，不改写 snapshot。

运行时插入的任务不再过 definition 校验：runner 构造的恢复任务来自已校验 definition 的
子树；服务端控制命令构造的任务由构造方保证合法。

## Examples

未知 key（拼写错误）：

```yaml
handlers:
  - when: error.code=conflict
    retryself: true
```

```text
stages[0].tasks[0].recovery.handlers[0]: 未知字段 retryself，是否想写 retrySelf
```

lockBehavior 缺少 resources：

```yaml
- stage: integrate
  lockBehavior: sequential
  tasks: [ ... ]
```

```text
stages[1]: lockBehavior 需要同时声明非空 resources
```

模板引用了表外的命名空间：

```yaml
- id: proposal
  uses: mohist/opencode
  with:
    prompt: 阅读 ${{ openspecChangeDir }}/proposal.md
```

```text
stages[0].tasks[0].with.prompt: 未知命名空间 openspecChangeDir
```

`with` 内部的 key 不校验：`with` 是 Action 的自由输入，键名由各 Action 契约定义管理。
校验器只检查 `with` 值里的模板表达式。

## 实现侧语义索引

| 构造 | 实现语义所在 |
|---|---|
| `with` / `expect` 的展开时机与 dispatch 输入 | [`task-dispatch.md`](task-dispatch.md) |
| `expect` / `artifacts` / `setVars` / `error` 的执行分工 | [`actions.md`](actions.md) |
| recovery 的匹配位置、预算流转（`recoveryRemaining`）、人工 retry 重建 | [`recovery.md`](recovery.md) |
| `vars.*` 的合并算法与写入 API | [`variables.md`](variables.md) |
| Profile 资源、definition snapshot 与 API | [`profile.md`](profile.md) |
| 内置 profile 的取舍与不变量 | [`builtin-workflows.md`](builtin-workflows.md) |

## Status

实装差距：

- `Expect` 未建模：expect 作为 JSON blob 藏在 `TaskDefinition.With` 里穿透全程，runner
  在 Action 内部验证（`acp-agent` 调 `verifyExpectations`），而非 executor。现行
  `ValidateTaskExpectations` 对 PASS / FAIL marker 的拒绝一并删除——marker 文本归作者，
  语言层不管。
- 解析器 `IgnoreUnmatchedProperties`：未知 key 被静默丢弃；`budget` 解析失败静默取 0。
- `title` 现被强制必填（目标可选）；`uses` 现可空（目标必填）；check 现用 `name`
  （目标 `id`）。
- 模型仍持有 workflow 级 `Variables` / `Defaults` / `Artifacts` 与 stage 级
  `Variables` 字段（目标移除：Variables 是独立资源）。
- 审批反馈任务在代码中是独立形状 `FeedbackTaskConfig`（目标复用 `Task`）。
- 现行注入裸根名 `mohist`、`project`、`openspecChangeName`、`openspecChangeDir` 与
  `workspace.changeDir`（见 `IssueVariableBuilder`），内置 profile 直接使用
  `${{ openspecChangeDir }}`。目标：删除这些注入，内置 profile 改写字面模板
  `openspec/changes/issue-${{ issue.number }}`，server 不再计算 openspec 路径公式；
  命名空间白名单即产品参考表内的十个根。
- 字符串内嵌入的表达式解析不出值时，runner 现保留原文（`template.ts`）；目标是任务
  失败。
- 共享类库、`mo workflow-profile validate`、docs 示例进 CI 均未实现。
