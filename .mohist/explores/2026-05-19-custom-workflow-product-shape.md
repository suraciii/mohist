# 用户可定制 Workflow 产品形态

## 探索背景

Mohist 当前 workflow 已经有 `WorkflowRun / StageRun / Task / Check` 的运行时模型，也有 `StageDefinition`、`ConfigDrivenStageRunner` 和 `workflow.yaml` 的早期入口。

但用户目标不是“让某几个命令可配置”，而是打造一个通用的、用户可定制的 workflow，就像 Azure DevOps Pipeline / GitHub Actions 一样：用户能把项目交付流程写成可读、可验证、可执行、可观察的 pipeline-as-code。

本次探索的核心问题：

- 用户应该如何理解 Mohist workflow？
- `workflow.yaml` 最小但完整的产品形态是什么？
- 内置 Plan / Build / Check / Integrate 如何变成同一套可配置模型？
- 哪些能力是第一阶段必须有，哪些属于后续扩展？

## 产品结论

Mohist 应把 workflow 做成 **AI-native pipeline-as-code**，不是传统 CI 的复刻。

用户最终感知到的产品应是：

```
我可以把一个 issue 从想法到落地的交付过程写成 workflow。

每个 stage 里有 tasks 和 checks：
  task 负责执行，可以调用 agent、跑脚本、生成文件、合并代码；
  check 负责验证，只读判断是否可以继续；
  reaction 负责把失败的 check 转成后续修复 task；
  approval 负责把需要人决策的点显式停下来。

Mohist 会按定义执行、展示进度、保留证据、处理失败，并告诉我下一步为什么是这个。
```

最重要的产品转变：

```
Current:
  Mohist has a built-in workflow with a few configurable gates.

Target:
  Mohist runs a workflow definition.
  The built-in workflow is just the default definition.
```

因此，`DEFAULT_STAGE_DEFINITIONS` 不应继续是业务事实源。它可以保留为 builtin workflow template，但运行时应从同一种 `WorkflowDefinition` 编译出 `StageDefinition[]`。

## 用户旅程

### 1. 使用默认 workflow

新用户不应该先写 YAML。

默认体验仍然是：

```
mo issue create
mo issue start
```

用户在 issue 页面看到：

```
Plan
  tasks: proposal, specs, design, tasks, self-review
  checks: artifact completeness, self-review, health gate
  approval: required

Build
  tasks: loaded from tasks.json
  checks: build health

Check
  tasks: ai-review
  checks: review passed, merge ready
  approval: required

Integrate
  tasks: spec sync, archive, merge
  checks: post-merge health
```

这个默认 workflow 必须被呈现为“可展开的定义”，而不是一套隐藏在代码里的固定流程。

### 2. 查看当前 workflow

用户应该能回答：

- 这个 issue 会经历哪些 stage？
- 每个 stage 会跑哪些 task？
- 哪些 check 会阻塞？
- 失败后会自动修复、重试、还是等待用户？
- 哪些地方需要 approval？
- 这些定义来自 builtin 还是项目配置？

理想命令：

```bash
mo workflow show
mo workflow validate
mo workflow explain check.merge-ready
```

Web UI 应提供同样的展开视图：

```
Workflow: default
Source: builtin + .mohist/workflow.yaml overrides

Plan
  Task     proposal        uses: mohist/agent
  Task     specs           uses: mohist/agent
  Check    self-review     uses: mohist/verdict
  Approval user-approval
```

### 3. 做小范围自定义

第一类用户需求不是重写整个 workflow，而是改少量策略：

- build check 命令换成 `pnpm test`
- Check stage 多加一个 lint check
- 某些项目不需要 OpenSpec sync
- review 失败后允许两次修复
- Plan 阶段 approval 关闭，Check 阶段 approval 保留

用户希望写：

```yaml
extends: mohist/default

checks:
  health:build:
    uses: mohist/shell
    with:
      command: pnpm build

stages:
  check:
    checks:
      - id: lint
        uses: mohist/shell
        with:
          command: pnpm lint
```

这比一开始要求用户完整复制默认 workflow 更好。

### 4. 定义自己的 workflow

高级用户才需要完整定义：

```yaml
workflow:
  stages:
    - id: plan
      tasks:
        - id: design
          uses: mohist/agent
          with:
            prompt: prompts/design.md
            outputs:
              - design.md
      checks:
        - id: design-complete
          uses: mohist/artifact-exists
          with:
            path: design.md
      approval: true

    - id: build
      tasks:
        - id: implement
          uses: mohist/agent
          with:
            prompt: prompts/implement.md
        - id: unit-tests
          uses: mohist/shell
          with:
            command: npm test
      checks:
        - id: build-clean
          uses: mohist/shell
          with:
            command: npm run build

    - id: integrate
      tasks:
        - id: merge
          uses: mohist/merge
      checks:
        - id: post-merge-health
          uses: mohist/shell
          with:
            command: npm test
```

但 v1 不应追求 GitHub Actions 的完整表达力。Mohist 的差异化是“issue delivery workflow”，不是通用云 CI。

## 关键概念

### WorkflowDefinition

用户写的 workflow 定义。

```
WorkflowDefinition
  id
  extends?
  stages[]
  defaults?
  variables?
```

它描述“应该怎么跑”，不描述“现在跑到哪”。

### StageDefinition

一个用户能理解的交付阶段。

```
StageDefinition
  id
  title
  tasks[]
  checks[]
  reactions[]
  approval?
```

Stage 是用户的决策边界，不应只是技术分组。

### TaskDefinition

执行工作单元。Task 可以产生副作用。

```
TaskDefinition
  id
  title
  uses
  with
  needs?
  outputs?
  resultContract?
  selfRepairPolicy?
```

Task 的核心问题是：

> 要做什么？用什么能力做？产出什么证据？

### CheckDefinition

只读验证。Check 不修改代码、不启动修复 agent。

```
CheckDefinition
  id
  title
  uses
  with
  blocksStage
```

Check 的核心问题是：

> 当前事实是否满足继续推进的条件？

### ReactionDefinition

失败处理策略。Reaction 本身不执行修复，它调度 task。

```
ReactionDefinition
  when
  scheduleTask
  inputFrom
  maxAttempts
  afterExhausted
```

它把 “check failed” 转成 “下一步应该做什么”。

### Run

运行态必须和定义态分开：

```
WorkflowDefinition -> WorkflowRun
StageDefinition    -> StageRun
TaskDefinition     -> TaskRun
CheckDefinition    -> CheckRun
```

用户看到的是：

- definition: 这条 workflow 设计上会怎么跑
- run: 这一次 issue 实际跑到了哪里、发生了什么、证据是什么

## 最小可行产品形态

第一阶段应只做“足够通用、但不过度设计”的能力。

### 必须支持

- `extends: mohist/default`
- stage 顺序定义
- static tasks
- static checks
- approval on/off
- builtin `uses`
- shell check/task
- agent task
- artifact existence check
- verdict check
- merge task
- OpenSpec built-in tasks
- check failure -> repair task
- max attempts
- validate/show/explain
- UI 展示 definition source

### 暂不支持

- 任意第三方 JavaScript action
- matrix build
- remote runner
- service containers
- cron/schedule trigger
- complex expression language
- job-level permissions
- secret marketplace
- reusable workflow marketplace
- arbitrary DAG replacing stage model

这些能力属于 GitHub Actions / Azure Pipelines 的完整 CI/CD 面，不是 Mohist v1 的核心。

## 内置 uses 目录

Mohist 的可定制能力应该从稳定的内置 action catalog 开始：

```
mohist/agent
  Run an AI agent task with prompt, context, outputs, and result contract.

mohist/shell
  Run a shell command as a task or read-only check depending on placement.

mohist/artifact-exists
  Verify required artifacts exist.

mohist/verdict
  Parse declared output source for PASS/FAIL structured verdict.

mohist/ralph-tasks
  Materialize and execute tasks.json.

mohist/openspec-sync
  Sync OpenSpec changes into main specs.

mohist/archive-change
  Archive OpenSpec change.

mohist/merge
  Merge issue branch into target branch.

mohist/rebase
  Rebase candidate branch as a visible task.

mohist/approval
  Read approval state and block until user decision.
```

这些 `uses` 是产品契约。内部可以由现有 TaskHandler、CheckRegistry、service-call 实现。

## 用户界面形态

Issue 页面不应该只显示运行结果，还应该显示“这次运行来自什么定义”。

```
┌────────────────────────────────────────────┐
│ Workflow: default                          │
│ Source: builtin + .mohist/workflow.yaml    │
│ Current: Check / ai-review                 │
├────────────────────────────────────────────┤
│ Plan                                       │
│   ✓ proposal        mohist/agent           │
│   ✓ specs           mohist/agent           │
│   ✓ self-review     mohist/verdict         │
│   ✓ approval        approved by user       │
│                                            │
│ Build                                      │
│   ✓ T-001           mohist/ralph-tasks     │
│   ✓ health:build    mohist/shell           │
│                                            │
│ Check                                      │
│   ▶ ai-review       mohist/agent           │
│   · review-passed   mohist/verdict         │
│   · merge-ready     mohist/merge-ready     │
└────────────────────────────────────────────┘
```

用户要能区分：

- 这是定义里的固定 task
- 这是动态 materialize 出来的 task
- 这是失败后插入的 reaction task
- 这是用户手动触发的 recovery task

但这些来源应该是详情 metadata，不应变成新的顶层概念。

## 和 GitHub Actions / Azure Pipelines 的关系

Mohist 应学习它们的产品直觉：

- workflow as code
- runs are inspectable
- every step has logs and conclusion
- failure points are explicit
- users can copy/paste/modify definitions
- validation error points back to config line

但不应照搬它们的完整模型：

- Mohist 的 stage 是 issue delivery stage，不是泛 CI job
- Mohist 的 task 可以是 AI agent session
- Mohist 的 check 必须保持只读
- Mohist 的 reaction 是一等失败收敛机制
- Mohist 的 approval 是产品决策点，不只是 environment gate
- Mohist 的 artifact 包括 proposal/design/spec/review 等交付文档

## 分阶段落地

### P0: Definition 成为事实源

目标：默认 workflow 和用户 workflow 走同一条编译执行路径。

- 将 builtin workflow 表达为 `WorkflowDefinition`
- 增加 `WorkflowDefinition -> StageDefinition[]` compiler
- `WorkflowRun.startWorkflow` 接收编译后的 definitions
- `ConfigDrivenStageRunner` 不再直接依赖 `DEFAULT_STAGE_DEFINITIONS`
- `mo workflow show` 展示展开后的定义
- `mo workflow validate` 给出用户可理解的错误

完成后，Mohist 仍可以默认工作，但“默认”只是一个内置定义。

### P1: 用户覆盖默认 workflow

目标：用户可以小范围修改默认流程。

- 支持 `extends: mohist/default`
- 支持覆盖 health gate command
- 支持追加/移除 task/check
- 支持配置 approval
- 支持配置 repair maxAttempts
- UI 显示 builtin 与 project override 的来源

这是最有用户价值的一步，因为它让不同项目真正适配自己的交付习惯。

### P2: 完整自定义 workflow

目标：高级用户可以定义自己的 stages/tasks/checks/reactions。

- 支持完整 `workflow.stages[]`
- 支持 `uses` catalog
- 支持 task outputs / check inputs
- 支持 reaction inputFrom
- 支持简单 `needs`
- 支持 artifact/resultContract 声明

P2 之后，Mohist 才接近“GitHub Actions for AI development workflows”的产品形态。

### P3: 扩展生态

目标：让团队复用 workflow。

- workflow templates
- reusable snippets
- project/team default workflow
- versioned builtin actions
- third-party action/plugin boundary

这一步不应提前做，否则会让核心模型过早复杂化。

## 设计约束

- Task executes, Check verifies.
- Workflow definition 和 workflow run 必须分离。
- 默认 workflow 必须仍然开箱即用。
- 自定义能力先从内置 `uses` 开始，不先开放任意代码执行生态。
- 用户看到的是交付流程，不是内部 runner 分类。
- 失败收敛必须是产品能力，不是异常处理细节。
- 配置错误必须在 start 前暴露，不能让用户等到 agent 跑一半才发现。
- UI 必须解释“为什么现在执行这个 task/check”。
- 任何涉及 main branch side effects 的步骤必须保留可见 evidence。

## 开放问题

- Stage id 是否继续限制为 `plan/build/check/integrate`，还是允许自定义 stage id？
- 如果允许自定义 stage id，Issue 的 `stage` enum 和列表过滤如何演进？
- `uses: mohist/shell` 作为 task 和 check 时，如何在产品上防止用户把有副作用的命令放进 check？
- 自定义 workflow 的权限/安全边界如何表达，尤其是 shell 与 merge？
- 默认 workflow 被用户覆盖后，老 issue 的 active workflow 是否冻结，还是随配置变化？
- Workflow definition 是否应按 run 持久化快照，避免后续配置变化影响历史 run？
- UI 如何在不增加复杂度的情况下显示 override 来源？

## 结论

Mohist 距离通用可定制 workflow 的最大差距，不在 runner 执行能力，而在产品化的 definition 层。

正确方向是：

```
Builtin behavior
  ↓
Builtin WorkflowDefinition
  ↓
Compiler
  ↓
StageDefinition[]
  ↓
WorkflowRun / ConfigDrivenStageRunner
  ↓
Issue UI / CLI explainability
```

第一阶段不要追求完整 GitHub Actions。应先做到：

**内置流程和用户流程使用同一个 definition 模型，同一个校验器，同一个 runner，同一个展示面。**

做到这一点后，Mohist 的 workflow 才真正从“硬编码产品流程”变成“可定制交付系统”。
