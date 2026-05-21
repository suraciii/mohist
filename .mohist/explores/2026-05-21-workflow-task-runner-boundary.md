# Workflow task runner boundary

## 探索背景
- Mohist 正在把内置阶段流程改造成用户可定义的 workflow。
- 近期已经把 `workflow/repair` 目录清理为 `workflow/reaction`，但模型层仍残留 runner 概念。
- 用户明确指出：模型是模型，runner 是 runner。Task 应该作为 workflow 的基本执行单元，但执行方式不能污染领域模型。

## 关键发现
- GitHub Actions runner 的有价值参考不是术语，而是分层：
  - Step 是被编排的执行单元。
  - StepsRunner 负责条件、timeout、状态收尾。
  - ActionRunner 加载 action 定义并选择 handler。
  - HandlerFactory 根据 execution type 选择 script/node/container/plugin/composite handler。
- Mohist 当前偏移点：
  - `workflow/model/workflow-definition.ts` 同时表达用户定义、派生 check failure policy、派生 invalidation policy、推断 task execution kind。
  - `TaskExecutionKind`、`TaskExecutionPolicy`、`repair-task` 是 runner 概念，不应该属于模型。
  - `repairPolicies` 把 onFailure.retry.task 翻译成旧的 fix-task 视角，弱化了 “task 是单元” 的模型。

## 可视化
```
用户 YAML / 内置默认定义
        │
        ▼
┌──────────────────────┐
│ workflow/model        │
│ - WorkflowDefinition  │
│ - StageDefinition     │
│ - TaskDefinition      │
│ - CheckDefinition     │
│ - WorkflowRun         │
│ - StageRun/TaskRun    │
└──────────┬───────────┘
           │ 只暴露事实和规则，不解释 uses
           ▼
┌──────────────────────┐
│ workflow/runner       │
│ - StageRunner         │
│ - TaskRunner          │
│ - CheckRunner         │
│ - Handler registry    │
└──────────┬───────────┘
           │ 根据 task.uses 选择 handler
           ▼
┌──────────────────────┐
│ task handlers         │
│ mohist/agent          │
│ mohist/health-gate    │
│ mohist/rebase         │
│ mohist/openspec-sync  │
└──────────────────────┘
```

## 决策与结论
- TaskDefinition 是定义；TaskRun 是每一次执行事实。retry、onFailure、event 触发都应该创建新的 TaskRun，而不是 reset 原地执行。
- 模型层可以保存 `uses` 字段，但不能推断 `uses` 对应哪个 handler。
- `onFailure.retry.task` 应该保留为 TaskDefinition；模型 append 的是普通 TaskRun，不应该制造 `repair-task` 特殊类型。
- runner 层可以有自己的 executable view，例如 `RunnerStageDefinition` 或 `TaskExecutionPlan`，但这个 view 不能反向污染模型。

## 后续改造方向
- 把 `TaskExecutionKind`、`TaskExecutionPolicy` 和 uses 到 handler 的推导搬出 `workflow/model`。
- 把 `compileWorkflowDefinition` 收敛为模型定义校验/归一化；如果 runner 还需要派生信息，放到 `workflow/runner` 或 `workflow/execution`。
- 把 `repairPolicies` 逐步改名/替换为 `checkFailurePolicies`，并让 policy 直接引用 retry task definition。
- 后续将 `task-runtime` 组织为 task runner/handler 层，不再让 stage runner 关心 repair/rebase/agent 等实现细节。

## 本轮实施记录
- `workflow/model/workflow-definition.ts` 已停止导出 `TaskExecutionKind`、`TaskExecutionPolicy`、`WorkSourceDefinition` 等 runner 概念。
- `compileWorkflowDefinition` 现在只做用户定义校验、clone/normalize，以及模型规则派生：check policy、approval policy、check failure policy、event invalidation policy。
- 新增 `workflow/runner/workflow-runtime-definition.ts`，由 runner 侧把模型快照投影成运行时定义，补足 `workSources` 和 `taskExecutionPolicies`。
- 默认内置 workflow、inspector 和持久化快照恢复路径都会显式生成 runtime snapshot；模型快照本身保持纯定义视角。
- 旧 `repairPolicies` 模型字段已移除，失败后的自动任务统一来自 `check.onFailure.retry.task` 派生的 `checkFailurePolicies`。
- 项目 override 里的旧 `repair:` shortcut 已移除，覆盖失败重试需要直接写 `checks.<id>.onFailure.retry`，和完整 workflow YAML 保持一致。

## 开放问题
- 现阶段是否保留 `CompiledStageDefinition` 名称作为过渡，还是直接拆出 `RunnerStageDefinition`。
- `checkPolicies`、`approvalPolicy`、`invalidationPolicy` 中哪些是纯模型规则，哪些是 runner projection。
- `repair-fix-adapter` 是否应拆成普通内置 task handler，还是短期保留为 runner-side compatibility adapter。
