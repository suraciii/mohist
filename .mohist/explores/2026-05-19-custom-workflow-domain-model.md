# 用户可定制 Workflow 领域模型

## 探索背景

Mohist 正在把 workflow 从内置 Plan / Build / Check / Integrate 流程，演进为通用的、用户可定制的 AI-native issue delivery workflow。

本次领域建模基于已创建的 Epic：

- Epic: `User-customizable AI-native workflow definitions`
- Child issues:
  - `#235` WorkflowDefinition compiler and builtin default workflow
  - `#236` workflow definition snapshot per WorkflowRun
  - `#237` workflow show / validate / explain
  - `#238` extends mohist/default and safe overrides
  - `#239` builtin uses catalog
  - `#240` workflow definition source and task origin in UI
  - `#241` full custom workflow definitions v1

核心目标不是“支持 YAML”，而是建立清晰的领域边界：

```text
Definition describes intent.
Snapshot freezes intent for a run.
Run records what happened.
Projection explains it to the user.
```

## 用户故事

### 默认用户

用户没有配置 workflow，只想 `mo issue start` 后稳定跑完。

他需要知道：

- Mohist 默认会跑什么
- 当前跑到哪
- 为什么卡住
- 需要他做什么

### 项目维护者

用户想保留 Mohist 默认流程，只改项目差异，比如 build 命令、lint check、approval 开关。

他需要知道：

- 我改了什么
- 有没有配错
- 会不会影响已启动 issue
- 当前 issue 用的是旧定义还是新定义

### 高级团队

用户想定义完整交付流程。

他需要可组合能力：

- agent task
- shell task/check
- artifact check
- verdict check
- approval
- merge
- reaction

### 回看历史的人

用户打开一个旧 issue，项目 workflow 已经变了。

他需要知道：

- 这个 run 当时用的是哪份 workflow
- 为什么当时跑了这些 task/check
- 当时的配置和今天的配置有什么区别

## 领域事件

领域事件使用过去式，表达业务事实。

```text
WorkflowDefinitionAuthored
BuiltinWorkflowPublished
ProjectWorkflowExtended
WorkflowOverrideApplied
WorkflowDefinitionValidated
WorkflowDefinitionRejected
WorkflowDefinitionResolved
WorkflowSnapshotCaptured

WorkflowRunStartedFromSnapshot
StageRunStarted
StageWorkMaterialized
TaskSelected
TaskExecuted
TaskFailed
CheckSelected
CheckEvaluated
CheckFailed
ReactionTaskScheduled
ApprovalRequested
ApprovalApproved
ApprovalRejected
StageRunPassed
StageRunFailed
WorkflowRunPassed
WorkflowRunFailed
```

关键新增事件：

```text
WorkflowSnapshotCaptured
WorkflowRunStartedFromSnapshot
```

这两个事件表达定义态和运行态的分离：用户改 `.mohist/workflow.yaml`，不应该改变已经启动的 run。

## 聚合根总览

```text
+================================================================================+
|                               Project / Workspace                              |
|                                                                                |
|  owns current workflow config                                                  |
|                                                                                |
|  +---------------------------+          +------------------------------------+  |
|  | WorkflowDefinition        |          | WorkflowRun                         |  |
|  | Aggregate Root            |          | Aggregate Root                      |  |
|  |                           |          |                                    |  |
|  | id                        |          | id                                 |  |
|  | name                      |          | issueId                            |  |
|  | source                    |          | status                             |  |
|  | extends?                  |          | currentStage                       |  |
|  | stages[]                  |          | definitionSnapshot                 |  |
|  | defaults                  |          | stageRuns[]                        |  |
|  | variables                 |          | failure?                           |  |
|  |                           |          |                                    |  |
|  | decides/validates:        |          | decides:                           |  |
|  | - stage graph is valid    |          | - next work item                   |  |
|  | - task/check refs valid   |          | - stage pass/fail                  |  |
|  | - uses placement valid    |          | - workflow pass/fail               |  |
|  | - override merge valid    |          | - recovery/reaction path           |  |
|  +-------------+-------------+          +------------------+-----------------+  |
|                |                                           ^                    |
|                | compile / resolve                          | starts from        |
|                v                                           | captured snapshot  |
|  +---------------------------+                              |                    |
|  | WorkflowDefinitionSnapshot|------------------------------+                    |
|  | Value Object / Run Input  |                                                   |
|  |                           |                                                   |
|  | workflowId                |                                                   |
|  | sourceChain[]             |                                                   |
|  | resolvedDefinition        |                                                   |
|  | compiledStageDefinitions  |                                                   |
|  | capturedAt                |                                                   |
|  +---------------------------+                                                   |
+================================================================================+
```

## WorkflowDefinition 聚合

`WorkflowDefinition` 是定义态聚合根。它描述 workflow 应该怎么跑，不描述某次 run 当前跑到哪里。

```text
+--------------------------------------------------------------------------------+
| WorkflowDefinition Aggregate Root                                               |
|                                                                                |
|  +----------------------+                                                       |
|  | WorkflowDefinition   |                                                       |
|  +----------+-----------+                                                       |
|             | owns                                                              |
|             v                                                                   |
|  +----------------------+       +----------------------+                         |
|  | StageDefinition      | 1..n  | StageDefinition      |                         |
|  | Entity               |       | Entity               |                         |
|  |                      |       |                      |                         |
|  | id                   |       | id                   |                         |
|  | title                |       | title                |                         |
|  | order                |       | order                |                         |
|  +----+------------+----+       +----+------------+----+                         |
|       |            |                 |            |                              |
|       | owns       | owns            | owns       | owns                         |
|       v            v                 v            v                              |
| +-----------+  +------------+   +-----------+  +------------+                    |
| | TaskDef   |  | CheckDef   |   | TaskDef   |  | CheckDef   |                    |
| | Entity    |  | Entity     |   | Entity    |  | Entity     |                    |
| |           |  |            |   |           |  |            |                    |
| | id        |  | id         |   | id        |  | id         |                    |
| | title     |  | title      |   | title     |  | title      |                    |
| | uses      |  | uses       |   | uses      |  | uses       |                    |
| | with      |  | with       |   | with      |  | with       |                    |
| | outputs?  |  | blocks?    |   | outputs?  |  | blocks?    |                    |
| +-----------+  +------------+   +-----------+  +------------+                    |
|       ^            ^                                                                 |
|       |            | referenced by                                                    |
|       +------------+------------------+                                             |
|                                    |                                                 |
|                                    v                                                 |
|                         +----------------------+                                     |
|                         | ReactionDefinition   |                                     |
|                         | Entity               |                                     |
|                         |                      |                                     |
|                         | when                 |                                     |
|                         | scheduleTask         |                                     |
|                         | inputFrom            |                                     |
|                         | maxAttempts          |                                     |
|                         +----------------------+                                     |
|                                                                                |
|                         +----------------------+                                     |
|                         | ApprovalDefinition   |                                     |
|                         | Entity / Policy      |                                     |
|                         |                      |                                     |
|                         | checkName?           |                                     |
|                         | required             |                                     |
|                         +----------------------+                                     |
+--------------------------------------------------------------------------------+
```

职责：

- 定义 stage 顺序
- 校验 task/check/reaction 引用
- 校验 `uses` 的 placement 是否合理
- 合并 builtin definition 与 project override
- 产出可运行的 compiled definitions

不负责：

- 某次 issue 的运行状态
- task/check 是否已经执行
- approval 是否已经通过
- 运行时 recovery 决策

## WorkflowRun 聚合

`WorkflowRun` 是运行态聚合根。它从 `WorkflowDefinitionSnapshot` 启动，记录某次 issue workflow 的真实推进。

```text
+--------------------------------------------------------------------------------+
| WorkflowRun Aggregate Root                                                      |
|                                                                                |
|  +----------------------+                                                       |
|  | WorkflowRun          |                                                       |
|  +----------+-----------+                                                       |
|             | owns                                                              |
|             v                                                                   |
|  +----------------------+       +----------------------+                         |
|  | StageRun             | 1..n  | StageRun             |                         |
|  | Entity               |       | Entity               |                         |
|  |                      |       |                      |                         |
|  | stageId              |       | stageId              |                         |
|  | status               |       | status               |                         |
|  | order                |       | order                |                         |
|  | failure?             |       | failure?             |                         |
|  +----+------------+----+       +----+------------+----+                         |
|       |            |                 |            |                              |
|       | owns       | owns            | owns       | owns                         |
|       v            v                 v            v                              |
| +-----------+  +------------+   +-----------+  +------------+                    |
| | TaskRun   |  | CheckRun   |   | TaskRun   |  | CheckRun   |                    |
| | Entity    |  | Entity     |   | Entity    |  | Entity     |                    |
| |           |  |            |   |           |  |            |                    |
| | taskId    |  | checkId    |   | taskId    |  | checkId    |                    |
| | status    |  | status     |   | status    |  | status     |                    |
| | attempts  |  | runCount   |   | attempts  |  | runCount   |                    |
| | output    |  | output     |   | output    |  | output     |                    |
| | artifacts |  | message    |   | artifacts |  | message    |                    |
| | reason?   |  | reason?    |   | reason?   |  | reason?    |                    |
| +-----+-----+  +-----+------+   +-----+-----+  +-----+------+                    |
|       |              |                |              |                           |
|       | emits        | emits          | emits        | emits                     |
|       v              v                v              v                           |
| +--------------------------------------------------------------------------+     |
| | WorkItemAttempt / Evidence                                               |     |
| | Value Objects                                                            |     |
| |                                                                          |     |
| | startedAt, completedAt, executionId, sessionId, output, diagnostic        |     |
| +--------------------------------------------------------------------------+     |
|                                                                                |
|  +----------------------+       +----------------------+                         |
|  | ApprovalDecision     |       | FailureDetails       |                         |
|  | Entity / Value       |       | Value Object         |                         |
|  |                      |       |                      |                         |
|  | status               |       | reason               |                         |
|  | output               |       | stageId              |                         |
|  | requestedAt          |       | taskId?              |                         |
|  | respondedAt          |       | checkId?             |                         |
|  +----------------------+       +----------------------+                         |
+--------------------------------------------------------------------------------+
```

职责：

- 从 snapshot 创建 run
- 推进 stage/task/check
- 处理 approval pending / approved / rejected
- 根据 check failure 调度 reaction task
- 记录 task/check 结果和 evidence
- 决定 stage passed/failed
- 决定 workflow passed/failed

不负责：

- 重新读取当前 `.mohist/workflow.yaml`
- 修改 workflow definition
- 解释 project override 合并规则

## Epic / Issue / WorkflowRun 关系

Epic 是长期目标聚合根，不是 issue subtype，也不运行 workflow。

```text
+-------------------------+          creates issue work          +----------------+
| Epic                    |-------------------------------------->| Issue          |
| Aggregate Root          |                                       | Aggregate Root |
|                         |     tracks linked issues only         |                |
| id                      |<--------------------------------------| epic link      |
| title                   |                                       | title          |
| description             |                                       | body           |
| linkedIssueIds[]        |                                       | stage/status   |
| progress projection     |                                       | mergeState     |
+-------------------------+                                       +-------+--------+
                                                                          |
                                                                          | starts
                                                                          v
                                                                +------------------+
                                                                | WorkflowRun      |
                                                                | Aggregate Root   |
                                                                +------------------+
```

边界：

- Epic 组织多个 issue，投影长期目标进度
- Issue 是执行工作单元，表达用户要交付什么
- WorkflowRun 是 issue 的一次 workflow 执行
- Epic 不 start、不创建 worktree、不审批代码、不跑 agent

## 与外部执行器的关系

```text
WorkflowDefinition AR
        |
        | validate / resolve / compile
        v
WorkflowDefinitionSnapshot
        |
        | captured at start
        v
WorkflowRun AR
        |
        | owns runtime state
        v
StageRun -> TaskRun / CheckRun / ApprovalDecision / FailureDetails
        |
        | uses external executor
        v
AgentSession AR / Shell / Git / OpenSpec services
```

AgentSession、Shell、Git、OpenSpec 是执行侧能力。它们可以产生 evidence，但不决定 workflow 下一步。

## 核心不变量

```text
Builtin workflow is a WorkflowDefinition.
Project workflow extends or replaces a WorkflowDefinition.
WorkflowRun always runs from a captured snapshot.
Task executes.
Check verifies.
Reaction schedules task.
Approval pauses for human decision.
WorkflowRun decides state transition.
UI explains from snapshot plus runtime evidence.
```

最关键的两个分离：

```text
WorkflowDefinition != WorkflowRun
WorkflowOverride != RuntimeMutation
```

用户改 `.mohist/workflow.yaml`，不应该改变已经启动的 run。

用户点击 retry/rerun，也不应该重新解析一份不同的 workflow，除非产品明确提供“用新定义重新开始”的动作。

## 反模型

这些概念暂时不应进入 Mohist workflow core：

```text
Job
Step
Matrix
RunnerPool
MarketplaceAction
CronTrigger
Environment
SecretScope
```

这些是通用 CI/CD 平台词汇。Mohist 当前要建的是 issue delivery workflow，不是 GitHub Actions 完整替代品。

同样，`review` 不应成为 workflow core：

```text
ReviewTask 是 builtin use case
ReviewFinding 是 structured item 的一种表现
Review 不是 WorkflowDefinition 的核心实体
```

## Child Issue 顺序映射

```text
#235 Definition source
  让 builtin workflow 进入 WorkflowDefinition 模型

#236 Run snapshot
  让 WorkflowRun 从 snapshot 运行

#237 Explainability
  让用户能 show / validate / explain

#238 Override
  让项目安全扩展 default

#239 Uses catalog
  定义 task/check 可组合能力

#240 UI source visibility
  让 issue 页面解释 definition origin

#241 Full custom workflow v1
  最后才开放完整定义
```

## 结论

这次改造的领域核心不是 YAML，也不是 compiler。

真正的核心是：

```text
Definition describes intent.
Snapshot freezes intent for a run.
Run records what happened.
Projection explains it to the user.
```

如果这个边界立住，`extends`、`uses`、`validate`、UI 展示都能自然长出来。

如果这个边界没立住，系统很容易变成“workflow.yaml 配置了一些东西，但运行时仍然到处读硬编码和当前文件状态”的半定制系统。
