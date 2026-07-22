---
status: wip
---

# Workflow Profile

`WorkflowProfile` 是 Project-scoped 资源，定义一个 Issue 如何从 Draft 走到 Done。
一个 Project 可以拥有多个 Profile，并指定其中一个作为默认 Profile。Issue 可以继承默认
Profile，也可以显式选择同一 Project 中的其他 Profile。

Profile 只包含 Workflow 的结构和行为，不包含 Variables 或 Prompts。变量解析见
[`variables.md`](variables.md)，Prompt 解析见
[`../prompt-management.md`](../prompt-management.md)，Action 契约见
[`actions.md`](actions.md)。

## Model

```plantuml
@startuml
skinparam shadowing false

class Project {
  defaultWorkflowProfileId
}

class WorkflowProfile {
  id
  name
  description
  definition
}

class Issue {
  workflowProfileId?
}

class WorkflowRun {
  workflowProfileId
}

Project "1" *-- "1..*" WorkflowProfile : owns
Project --> WorkflowProfile : default
Issue --> Project : belongs to
Issue --> WorkflowProfile : selects
WorkflowRun --> WorkflowProfile : selected at start

note right of WorkflowProfile
  Project-scoped.
  Does not own Variables or Prompts.
end note

note right of WorkflowRun
  Profile identity is fixed for this run.
  Definition is resolved as stages start.
end note
@enduml
```

`WorkflowProfile` 的最小模型是：

| Field | Meaning |
|---|---|
| `id` | Profile 在 Project 内的稳定标识 |
| `name` | 面向使用者的名称 |
| `description` | 适用场景的简短说明 |
| `definition` | stages、初始 tasks、checks、approval，以及产生后续 task 的 recovery 等规则 |

`mohist/*` ID 保留给随 Mohist 版本更新的内置 Profile。这些 Profile 在每个 Project 的同一
collection 中可见、可选，也可以作为默认 Profile，但不能修改或删除。其他 ID 的 Profile
由 Project 管理。

Profile 可以通过 `${{ vars.* }}` 和 `${{ prompts.* }}` 引用外部值，但不声明或保存这些
值。固定且只属于某个 task 的 Action Input 应直接写在 `definition` 中。

## Selection

Issue 启动 WorkflowRun 时只做一次选择：

```text
selectedProfileId =
  issue.workflowProfileId ?? project.defaultWorkflowProfileId
```

- Project 默认值必须引用该 Project 拥有的 Profile。
- Issue 显式选择也必须引用同一 Project 中的 Profile。
- 清除 Issue 的显式选择后，Issue 重新继承 Project 默认值。
- Profile 之间不继承、不 merge；选择结果始终是一个完整 Profile。
- WorkflowRun 保存启动时选中的 Profile ID。之后修改 Issue 的选择或 Project 默认值，
  只影响未来的 WorkflowRun，不会把活动 Run 切换到另一个 Profile。
- 修改同一 Profile ID 的 Definition 后，活动 Run 在后续 Stage 初始化时读取新版本。

WorkflowRun 不保存完整的 Workflow Definition snapshot。创建 Run 时只物化推进生命周期
所需的 StageRun 和审批事实；每个 Stage 初始化时，按 `workflowProfileId` 重新读取该
Profile 当前 Definition 中的 Stage 结构。已经初始化的 Stage 不被 Profile 编辑追溯改写。
运行时 task 的产生与插入见 [`definition.md`](definition.md)。每个新 attempt 建立 context
时重新解析 Variables 和 Prompt；已经接受的 attempt 保持自己的 context snapshot。
declaration 与 rendered input 的边界见 [`task-dispatch.md`](task-dispatch.md)。

## Ownership

`WorkflowProfile` 属于 Workflow 核心域，但以 `ProjectId` 作为 tenancy boundary。Project
持有默认 Profile 引用，Issue 持有可选的显式 Profile 引用；两者都不复制 Profile body。

```text
Issue -> Workflow

WorkflowRun stage initialization -> IWorkflowProfileProvider
                                      ^
                              ProjectWorkflowProfileProvider
```

`IWorkflowProfileProvider` 在 WorkflowRun 创建以及每个 Stage 初始化时，按 Project 与
Profile ID 提供当前、经过校验的 `WorkflowDefinition`。WorkflowRun 不保存 Definition
body。Provider 不读取 Variables 或 Prompts，也不负责 Profile 选择。

## API

Profile collection 是 Project 的子资源：

```text
GET    /api/projects/{projectRef}/workflow-profiles
POST   /api/projects/{projectRef}/workflow-profiles
GET    /api/projects/{projectRef}/workflow-profiles/{*profileId}
PUT    /api/projects/{projectRef}/workflow-profiles/{*profileId}
DELETE /api/projects/{projectRef}/workflow-profiles/{*profileId}
```

Project 的 `defaultWorkflowProfileId` 与 Issue 的 `workflowProfileId` 是对该 collection 的
引用，分别通过 Project 和 Issue resource 修改。删除 Profile 时必须保护仍被默认值、Issue
或活动 WorkflowRun 引用的关系；保留同一 ID 更新 Definition 则允许，活动 WorkflowRun
会在后续 Stage 初始化时读取新版本。

`profileId` 是 terminal catch-all，因此可以无损寻址 `mohist/local` 这类 ID。Variables 与
Prompts 使用独立 API，不挂在 `/workflow-profiles/{*profileId}` 下。

`GET` 和 collection list 同时返回内置与 Project 管理的 Profile。`POST` 不接受
`mohist/*` ID；对内置 Profile 调用 `PUT` 或 `DELETE` 必须返回领域错误。

## Status

与当前实现的差距：

- 当前实现把 system profile、project template 和 Project 的单例 workflow config 分成
  三套概念；目标模型统一为 Project-scoped `WorkflowProfile` collection。
- 当前 `ProjectWorkflowProfile`、`IssueWorkflowProfile` 和 `WorkflowRunProfile` 记录还
  混合保存 Variables；目标模型将这些资源完全分开。
- 当前 Issue 还可以保存 inline template；目标模型只允许选择 Project 中已有的 Profile。
- 当前有活动 WorkflowRun 时，Issue 的 Profile 选择会被锁定；目标模型允许记录新选择，
  但只对下一次新建的 WorkflowRun 生效。
- 当前 WorkflowRun 按 Stage 实时读取 Definition；Profile collection 迁移必须保留该行为，
  不得把 Definition body 复制进 WorkflowRun。
