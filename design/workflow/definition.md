---
status: wip
---

# Workflow Definition

Workflow Definition DSL 是产品的命令面之一：使用者编写它来定制 Workflow。语法与作者
可见语义的权威参考因此在产品层——
[`docs/workflow-definition.md`](../../docs/workflow-definition.md)。
本篇只登记实现侧语义的分布和设计开放问题，不复述语法。

## 实现侧语义索引

| 构造 | 实现语义所在 |
|---|---|
| `with` / `expect` 的展开时机与 dispatch 输入 | [`task-dispatch.md`](task-dispatch.md) |
| `expect` / `artifacts` / `setVars` / `errorCode` 的执行分工 | [`actions.md`](actions.md) |
| recovery 的匹配位置、预算流转（`recoveryRemaining`）、人工 retry 重建 | [`recovery.md`](recovery.md) |
| `vars.*` 的合并算法与写入 API | [`variables.md`](variables.md) |
| Profile 资源、definition snapshot 与 API | [`profile.md`](profile.md) |
| 内置 profile 的取舍与不变量 | [`builtin-workflows.md`](builtin-workflows.md) |

`recoveryRemaining` 等执行状态不属于 definition，不能在 YAML 中声明；见
[`recovery.md`](recovery.md)。

## Definition 与运行时任务

`WorkflowDefinition` 定义 Workflow 的阶段、初始任务和产生后续任务的规则，不是完整的
执行计划。`WorkflowRun` 启动时保存 Definition snapshot，并用它初始化 `StageRun` 和初始
`TaskRun`。

运行期间，task expansion、recovery、retry、approval feedback 和控制命令都可以产生新的
task。每个新 task 都进入当前 `WorkflowRun`，成为普通的 `TaskRun`，并使用相同的
dispatch、report 和 Variables 解析语义。Definition snapshot 不随这些 task 改变。

`mo issue rebase` 是控制命令插入 task 的一个例子：它向当前 Stage 插入一个
`uses: mohist/rebase` 的 task，不修改 `WorkflowDefinition`。

## Status

开放问题：

- check 用 `name`、task 用 `id`，是否统一为 `id`。
- `mohist/github-pr-status` 的 Action Input `expect` 与 task 级 `expect` 撞名，是否
  改名以消除歧义。
- 从产品参考派生 JSON Schema，并提供 `mo` 本地校验入口；错误用领域语言表达。
- `${{ openspecChangeDir }}` 是裸名，不属于任何已声明命名空间；需归入命名空间并同步
  [`task-dispatch.md`](task-dispatch.md) 与产品参考。
