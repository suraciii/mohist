---
status: implemented
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

- 语言的每个构造都有类型对应。`With` 是唯一的开放结构：Definition 层只要求它是 JSON
  object，并递归校验其中的模板表达式；内部 key、required 与值类型由所选 Action 的
  manifest 裁决。
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
| with | 缺省或 JSON object；Definition 校验器不解释内部 key，只递归校验值中的模板表达式 |

### 校验入口

同一实现暴露三处，规则只有上表一份：

- Profile 保存 API：非法 definition 拒绝保存，返回错误列表。
- `mo workflow validate --file <path>`：本地校验，不解析 Project、不经服务器；`--file -`
  从 stdin 读取。
- CI：内置 profile 与 `docs/workflow-definition.md` 中的完整示例作为黄金用例，必须通过
  校验——语法参考与校验器由此互相锁定。含 `<...>` 占位符的骨架片段不进用例。

Profile 保存入口组合两种互不重叠的判断：本篇校验器拥有 Definition 结构、字段类型与
模板语言；Action catalog 拥有 `uses` 是否存在以及 `with` 的 key、required 与类型。
两类错误使用同一 YAML path 规则并标明来源，但不能互相复制规则。纯本地命令只运行前者；
保存入口把通过解析得到的语义模型交给 Action catalog 校验。

### 运行时任务

`WorkflowDefinition` 不是完整执行计划，`WorkflowRun` 也不保存 Definition snapshot。
Run 创建时物化推进生命周期所需的 StageRun 和审批事实；每个 Stage 初始化时重新读取所选
Profile 的当前 Definition。已初始化 Stage 不被后续编辑追溯改写。运行期间 recovery、
retry、审批反馈和控制命令（如 `mo issue rebase` 插入 `uses: mohist/rebase`）都可以产生
新的 `TaskRun`，使用相同的 dispatch、report 与 Variables 解析语义。

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

Definition 校验器不检查 `with` 内部 key：键名由各 Action 契约管理，只检查值里的模板
表达式。Profile 保存入口随后可以把同一任务交给 Action catalog，因此“Definition 合法”
不等于“所选 Action 及其输入在当前 Project 可用”。

## 实现侧语义索引

| 构造 | 实现语义所在 |
|---|---|
| `with` / `expect` 的展开时机与 dispatch 输入 | [`task-dispatch.md`](task-dispatch.md) |
| `expect` / `artifacts` / `setVars` / `error` 的执行分工 | [`actions.md`](actions.md) |
| recovery 的匹配位置、预算流转（`recoveryRemaining`）、人工 retry 重建 | [`recovery.md`](recovery.md) |
| `vars.*` 的合并算法与写入 API | [`variables.md`](variables.md) |
| Profile 资源、实时 Definition 解析与 API | [`profile.md`](profile.md) |
| 内置 profile 的取舍与不变量 | [`builtin-workflows.md`](builtin-workflows.md) |

## Status

权威 Definition 校验器已实现并由 Profile 保存入口、`mo workflow validate --file` 和 CI
黄金用例共同使用。未知字段、字段类型、`check.id`、`uses` 必填及保存期预校验均由
Definition 校验器负责；Action 是否存在以及 `with` 的契约仍由 Action catalog 负责。
