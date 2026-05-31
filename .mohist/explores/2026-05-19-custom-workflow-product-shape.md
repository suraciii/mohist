# 用户可定制 Workflow 产品形态

## 探索背景

Mohist 当前 workflow 已经有 `WorkflowRun / StageRun / Task / Check` 的运行时模型，也有 `StageDefinition`、`GenericStageRunner` 和 `workflow.yaml` 的早期入口。

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
  onFailure 负责把失败的 check 转成有限次数的修复循环；
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

内部实现可以先用语义化 TS block 表达默认 workflow，不必等完整 YAML parser 才纠正架构：

```
MOHIST_DEFAULT_WORKFLOW_SOURCE
  id: mohist/default
  stages:
    - id: plan
      tasks/checks/approval
    - id: build
      tasksFrom: mohist/ralph-tasks
      checks
    - id: check
      on: code.changed -> reset checks-and-approval
      tasks/checks/onFailure
    - id: integrate
      tasks/checks
```

这份 source block 等价于未来的 builtin YAML：它只能包含用户也能理解的语义字段，不包含运行时为了执行而生成的 `taskExecutionPolicies`、`checkPolicies`、`repairPolicies`、`invalidationPolicy`。

运行路径应是：

```
Builtin semantic source / project workflow.yaml
  -> parseWorkflowDefinitionSource
  -> WorkflowDefinition
  -> compileWorkflowDefinition
  -> WorkflowDefinitionSnapshot.compiledStageDefinitions
  -> WorkflowRun / GenericStageRunner
```

这样默认 workflow 和项目 YAML 会使用同一种 parser/编译器；内置流程仍可用 TS 保持类型安全并引用内置 result contract；用户看到的 YAML 形态不会被 runtime policy 污染。

关键边界：workflow 的所有行为都必须能从定义中推导并在编译/装载阶段显式化。运行时不应靠 runner 临场猜测行为，也不应依赖散落在 runner 内部的 stage-specific 分支来决定下一步。

也就是说：

```
Allowed:
  definition -> compiler/loader -> compiled definition snapshot -> engine runs it

Not allowed:
  definition -> runner/runtime heuristics infer hidden behavior while running
```

如果当前代码里仍有 `taskExecutionPolicies` / `checkPolicies` / `repairPolicies` / `invalidationPolicy`，它们最多只能被视为迁移期的 compiled definition 内部结构或 legacy compatibility，不是用户 workflow 模型，也不是长期运行前提。

这里不引入 `ExecutableWorkflowDefinition` 作为新领域概念。领域上只有 `WorkflowDefinition`；编译只是把同一份定义变成 `WorkflowDefinitionSnapshot` 中可直接运行的 `compiledStageDefinitions`。如果后续需要更强类型，也应优先命名为 `CompiledStageDefinition` / `CompiledWorkflowDefinition`，表达“同一份定义的编译形态”，而不是制造第二个业务对象。

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
  emits?
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
  onFailure?
  blocksStage
```

Check 的核心问题是：

> 当前事实是否满足继续推进的条件？

### Failure Policy

失败处理策略挂在 check 上，而不是作为 stage 顶层的独立 reaction 表。

```
CheckDefinition.onFailure.retry
  limit
  task
  inputFrom?
```

它回答的问题是：

> 这个 check 不通过时，是否自动修，修几轮，用什么上下文修？

它不应该再引入 `then`。修复后哪些判断需要重跑，应由 stage-level event policy 推导。

## 事件驱动的 Check reset 模型

Mohist 的 workflow 不是普通 CI 数据流。它围绕一个 issue，把“想法”变成“可交付结果”。

因此 YAML 里不应让用户到处写：

```yaml
rerun:
  tasks: [...]
  checks: [...]
  approval: true
```

这种写法暴露的是内部失效策略，用户必须重复维护同一组重跑列表，而且很容易漏掉新的 check。

更合适的模型是：

```text
task 声明自己完成后会 emits 哪些事件
stage 声明这些事件发生后如何 reset 当前 stage 的 checks/approval
check 只保留 success / fail / not-run 三态
```

这里不需要引入 `resources` 作为中间层，也不需要每个 check 写 `staleWhen`。用户真正想表达的是：

```text
这个 task 做完后发生了什么？
这件事发生后，这个 stage 的判断是否需要重新跑？
```

### 事件由 workflow 定义，不由引擎预设

Mohist engine 不应该内置 `plan`、`review`、`delivery` 这类资源或事件名。

原因是：

- 通用 workflow 不一定有 Plan 阶段。
- 用户可能把设计、实现、审查、发布拆成完全不同的事件。
- `plan.updated` 这类事件是 mohist/default 模板的产物，不是 Mohist workflow engine 的领域不变量。
- 一旦事件名由引擎预设，用户会以为所有 workflow 都必须围绕这些概念组织，反而削弱通用性。

更好的边界：

```text
Workflow engine
  understands: stage events, task emits, reset checks/approval
  does not understand: plan/review/delivery as special names

mohist/default workflow
  uses: code.changed and other template events
  uses them to model default issue delivery flow
```

事件不需要提前声明。编译器可以从 task 的 `emits` 和 stage policy 中收集事件名。可选的 `events` 块只用于说明文字，不作为 v1 必填字段。

### mohist/default 的核心事件

mohist/default 第一版只需要一个核心事件：

| 事件 | 含义 | 典型发出者 | 典型失效对象 |
| --- | --- | --- | --- |
| `code.changed` | 当前 issue 准备交付的代码/文件内容发生变化 | Build tasks、review fix、rebase | Check stage 的 checks 和 approval |

`code.changed` 比 `deliverable.changed` 更朴素，也更接近真实规则：

- Check stage 验证的是“当前代码是否可以交付”。
- 代码变了，旧 check 结论不再能代表当前代码。
- 用户不需要理解 review.updated、deliverable.changed、resources 等中间概念。
- 对 v1 来说，不做文件级影响分析；只要发生代码变更，就重置 Check stage 的检查结果。

### 真实 Check 场景

```text
ai-review task
  produces review.md
   │
   ▼
review-passed check
  reads review.md
   │ FAIL
   ▼
fix-review-findings task
  emits: [code.changed]
   │
   ▼
code.changed
   │
   ├─ reset checks: health:check, review-passed, merge-ready -> not-run
   └─ reset approval -> not-run
```

用户 YAML 应表达真实控制点，而不是手写每个 check 的 stale 条件：

```yaml
stages:
  - id: check
    on:
      code.changed:
        reset: checks-and-approval

    tasks:
      - id: ai-review
        uses: mohist/agent
        with:
          prompt:
            ref: mohist/check/ai-review

    checks:
      - id: health:check

      - id: review-passed
        onFailure:
          retry:
            limit: 2
            task:
              id: fix-review-findings
              uses: mohist/agent
              emits: [code.changed]
              with:
                prompt:
                  inline: |
                    Fix the blocking findings in:

                    {{ openspec.changeDir }}/review.md

                    Apply the minimal code changes required.
                    Do not edit review.md.

      - id: merge-ready

    approval: true
```

`rebase-branch` 也可以自然落到同一模型：

```yaml
tasks:
  - id: rebase-branch
    uses: mohist/rebase
    emits: [code.changed]
```

如果 rebase 没有改变 SHA，运行时不应 emit `code.changed`；如果 SHA 变了，Check stage 的 checks 和 approval 回到 `not-run`。

### Check 状态

Check stage 的 check 状态应保持三态：

```text
not-run -> success
        -> fail
```

不需要单独暴露 `stale` 状态。代码变更后，旧结果作为历史保留，但当前 check 状态直接回到 `not-run`。这更贴近用户心智：

```text
代码变了，所以这几个检查还没基于最新代码跑过。
```

### 默认推导，显式覆盖

为了避免 mohist/default YAML 变啰嗦，内置 `uses` 可以提供模板级默认事件契约，但这些默认值必须来自所处 stage 的 event policy，不能成为 engine 级特殊事件。

| uses / prompt ref | 默认事件契约 |
| --- | --- |
| `mohist/agent` + `mohist/check/fix-review-findings` | emits `code.changed` |
| `mohist/rebase` | emits `code.changed` only when SHA changed |

用户只有在自定义会修改代码的 task 时才需要显式写 `emits: [code.changed]`。

### Prompt context

默认 review 修复不需要 `retry.inputFrom`。

真实场景里，`fix-review-findings` 最需要的上下文就是审查报告文件本身。与其把 `review.md` 解析成 `blockingItems`，再通过复杂模板 helper 拼回 prompt，不如直接把路径交给 agent：

```yaml
with:
  prompt:
    inline: |
      Fix the blocking findings in:

      {{ openspec.changeDir }}/review.md

      Apply the minimal code changes required.
      Do not edit review.md.
```

Mohist v1 应提供少量内置 prompt 变量：

| 变量 | 含义 |
| --- | --- |
| `{{ issue.number }}` | 当前 issue number |
| `{{ issue.title }}` | 当前 issue title |
| `{{ worktree.path }}` | 当前 issue worktree path |
| `{{ openspec.changeDir }}` | 当前 issue 的 OpenSpec change directory |

`retry.inputFrom` 作为高级能力保留，但不应出现在 default review workflow 的主路径里。它适合少数需要从 failed check output 中抽结构化数据的场景：

```yaml
inputFrom:
  failedCheck:
    type: failed-check-output
  reviewItems:
    type: check-items
    filter: blocking
```

如果未来支持 prompt 插值，`inputFrom` 应该生成命名变量，而不是匿名 selector 数组。模板应引用原始变量或简单 helper：

```text
{{ failedCheck.output.reviewReport }}
{{ json(reviewItems) }}
```

不要引入 `blockingItems.markdown` 这类隐式派生字段；如果需要格式化，应显式使用 helper，例如 `{{ markdown(reviewItems) }}`。

### 编译边界

`emits` / `on.<event>.reset` 是用户态和定义态概念。

运行时仍可以编译成内部结构：

```text
WorkflowDefinition
  stages[].on
  tasks with emits
        |
        v
compileWorkflowDefinition
  event reset policy
  repair policy
        |
        v
StageDefinition runtime metadata
  InvalidationPolicy
  RepairPolicy
  TaskExecutionPolicy
```

这保留现有 runtime 的可实施性，同时避免把 `InvalidationPolicy` 暴露给用户。

### 约束

- `emits` 表示这个 task 完成后发生了某个 workflow 事件，不是文件系统写入。
- Check 不发事件，不改代码，不启动 agent；如果需要修复，必须通过 `onFailure.retry.task`。
- `code.changed` 后只 reset 当前 stage 的 checks 和 approval；task 历史保留。
- Engine 不预置事件名。v1 不做文件级依赖，不做 arbitrary DAG，不做复杂表达式。

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
- stage event reset policy
- task `emits`
- prompt template interpolation with built-in variables
- builtin `uses`
- shell check/task
- agent task
- artifact existence check
- verdict check
- merge task
- OpenSpec built-in tasks
- check failure -> repair task
- max attempts
- compile emitted events into check reset policy
- keep `retry.inputFrom` optional and off the default review path
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

## GitHub Actions Runner 参考结论

GitHub Actions runner 的关键架构不是“runner 直接解释 YAML”，而是：

```text
.github/workflows/*.yml
  -> GitHub service / workflow parser / planner
  -> AgentJobRequestMessage
  -> JobRunner
  -> StepsRunner
  -> ActionRunner
  -> HandlerFactory
```

Runner 收到的是已经 materialized 的 job message。`JobRunner` 初始化 job context，把 message 中的 steps enqueue；`StepsRunner` 只负责条件判断、超时、执行和结果合并；`ActionRunner` 再根据 action definition 的 execution type 交给 node/container/script/composite handler。

这给 Mohist 的启发：

- YAML/parser/compiler 和 runner 必须有硬边界。
- 运行时应执行 `WorkflowDefinitionSnapshot` 中的 compiled definition，而不是边跑边从 YAML 或 task id 猜行为。
- `uses` 应该是 handler registry 的入口，类似 GitHub Actions 的 action handler factory。
- full custom workflow 至少要能表达 builtin default workflow 中所有内置 `uses`；否则“默认 workflow 只是内置定义”这个产品承诺不成立。
- composite action 的思路后续可借鉴为 Mohist reusable task/workflow snippet，但 v1 不应先复制 GitHub Actions 的 job/matrix/needs/service container 复杂度。

对应到当前 Mohist 的偏移：

- `workflow-inspector.ts` 和 `workflow-loader.ts` 仍是两套 YAML/config 入口。
- full custom workflow 只允许 `mohist/agent` / `mohist/ralph-tasks`，无法表达 `mohist/openspec-sync`、`mohist/archive-change`、`mohist/merge`。
- `GenericStageRunner` 仍有 Plan/Check task id 特判。
- service-call dispatch 仍偏向 Integrate stage/task id，而不是根据 `uses` catalog 选择 handler。

因此近期改进顺序应是：

1. 让 full custom workflow 的 task `uses` 接受 catalog 中允许作为 task 的内置 use。
2. service-call dispatch 先按 `uses` 选择能力，task id 只作为兼容 fallback。
3. 后续把 `workflow-loader.ts` 收口到 definition parser，把 runtime policy side table 演进成直接 executable shape。
4. 最后再清理 runner 内 Plan/Check 特判和完全开放自定义 stage id。

## 分阶段落地

### P0: Definition 成为事实源

目标：默认 workflow 和用户 workflow 走同一条编译执行路径。

- 将 builtin workflow 表达为 `WorkflowDefinition`
- 增加 `WorkflowDefinition -> StageDefinition[]` compiler
- `WorkflowRun.startWorkflow` 接收编译后的 definitions
- `GenericStageRunner` 不再直接依赖 `DEFAULT_STAGE_DEFINITIONS`
- `mo workflow show` 展示展开后的定义
- `mo workflow validate` 给出用户可理解的错误

完成后，Mohist 仍可以默认工作，但“默认”只是一个内置定义。

### P0.5: Legacy runner 退出核心路径

目标：用户 YAML 是 workflow 的产品契约，`GenericStageRunner` 是唯一默认 stage executor。

当前 `CheckStageRunner / PlanStageRunner / BuildStageRunner / IntegrateStageRunner` 是迁移期实现，不是新的领域模型。只要生产路径仍注册这些 runner，用户就会看到两个互相竞争的事实源：

```text
workflow.yaml says what should run
legacy runner code may still do something else
```

这会破坏“默认 workflow 只是 builtin YAML”的产品承诺。

迁移原则：

- 生产默认只注册 `GenericStageRunner`。
- legacy runner 只能作为显式兼容模式存在，不能挂在默认 fallback 后面。
- legacy code 应移动到 `workflow/legacy/`，避免它在核心 workflow API 中看起来仍是一等实现。
- 核心导出不再导出 `CheckStageRunner` 等 stage-specific runner。
- 有价值的旧测试应迁移到 `GenericStageRunner`、`WorkflowRun`、task/check handler 层。
- 还没迁完的旧测试可以临时引用 `workflow/legacy/*`，但测试名称必须表达 legacy。

第一步应先清掉 `CheckStageRunner` 的生产路径，因为 Check 是最容易污染新模型的 stage：它包含 ai-review、review-passed、merge-ready、approval、repair、re-review 等核心语义。如果这些仍由手写 Check runner 决定，YAML 就不是事实源。

### P0.6: Builtin default 作为 parser 输入

目标：内置默认 workflow 和项目 YAML 复用同一个 definition parser。

默认 workflow 应由 `MOHIST_DEFAULT_WORKFLOW_SOURCE` 这样的语义定义生成，而不是直接维护编译后的 `StageDefinition`。

可接受的 source 字段：

- `id/name`
- `stages[].id`
- `tasks`
- `tasksFrom`
- `checks`
- `on`
- `approval`
- `onFailure.retry`

不应出现在 source 中的字段：

- `workSources`
- `taskExecutionPolicies`
- `checkPolicies`
- `approvalPolicy`
- `repairPolicies`
- `checkFailurePolicies`
- `invalidationPolicy`

这些字段必须由 `parseWorkflowDefinitionSource` 和 `compileWorkflowDefinition` 统一生成。项目 YAML 的 full custom workflow 也应复用同一个 parser，避免 builtin TS block 与用户 YAML 变成两套模型。

### P0.7: Compiled definition 边界取代 runtime policy side table

目标：运行时引擎可以直接运行 `WorkflowDefinitionSnapshot` 中的 compiled definition，而不是依赖 runtime policy side table 和 runner heuristics。

原则：

- workflow 的所有行为都来自定义。
- 编译阶段可以把语义定义展开成可运行的 compiled definition。
- 运行时只执行 compiled definition，不临场推导隐藏行为。
- `uses` catalog 可以提供 action/check 的执行能力和契约，但不能暗中增加用户没定义的 workflow 行为。
- `onFailure.retry`、`task.emits`、`stage.on`、`approval`、`tasksFrom` 都应成为 compiled definition 的直接结构或显式编译结果。

迁移方向：

- 保留现有 `taskExecutionPolicies/checkPolicies/repairPolicies/invalidationPolicy` 作为短期编译产物，保证兼容。
- 新增或逐步演进到更直接的 compiled shape，例如：

```ts
CompiledStageDefinition {
  stage
  taskQueue: CompiledTaskDefinition[]
  dynamicTaskSources: DynamicTaskSourceDefinition[]
  checks: CompiledCheckDefinition[]
  approval?: ApprovalDefinition
  eventHandlers: EventHandlerDefinition[]
}
```

- `GenericStageRunner` 只读取 compiled definition，不读取用户 source，也不从 task id 猜测行为。
- 当 compiled definition 足够完整后，移除 policy side table。

这一步比“runner runtime 推导”更符合用户目标：用户自定义 workflow 的关键不是运行时聪明，而是定义本身完整、可解释、可验证、可执行。

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

目标：高级用户可以定义自己的 stages/tasks/checks/onFailure。

- 支持完整 `workflow.stages[]`
- 支持 `uses` catalog
- 支持 task outputs / check inputs
- 支持 `onFailure.retry.task`
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
WorkflowRun / GenericStageRunner
  ↓
Issue UI / CLI explainability
```

第一阶段不要追求完整 GitHub Actions。应先做到：

**内置流程和用户流程使用同一个 definition 模型，同一个校验器，同一个 runner，同一个展示面。**

做到这一点后，Mohist 的 workflow 才真正从“硬编码产品流程”变成“可定制交付系统”。

## 待实施改进项

### P0: 收敛 default Check stage 定义

- 将 default Check stage 改成 stage-local event policy：

```yaml
on:
  code.changed:
    reset: checks-and-approval
```

- `fix-review-findings` repair task 显式 `emits: [code.changed]`。
- `rebase-branch` 在 SHA 变化时 emit `code.changed`。
- default `review-passed.onFailure.retry` 移除 `inputFrom`。
- default `fix-review-findings` prompt 改为模板变量引用 `{{ openspec.changeDir }}/review.md`。

### P1: 扩展 WorkflowDefinition source model

- `TaskDefinition` 增加 `emits?: string[]`。
- `StageDefinition` 增加 event policy，例如 `on?: Record<string, { reset: 'checks-and-approval' | 'checks' | 'approval' }>`。
- 保持 `CheckDefinition` 简单，不增加 `source.task` / `staleWhen`。
- `CheckFailureRetry.inputFrom` 保留为 optional advanced capability。

### P2: Compiler 生成 compiled definition

- `compileWorkflowDefinition` 将 `stage.on + task.emits` 编译成 compiled definition 的 event handler。
- `code.changed -> checks-and-approval` 在 compiled definition 中显式表达为当前 stage checks reset + approval reset。
- 编译器不要求事件提前声明；从 `emits` 和 `stage.on` 收集事件。
- 如果 task emits 了没有 stage policy 的 event，给 warning 或 explain 信息，不阻断 v1。
- 现阶段可以继续生成 `InvalidationPolicy` 兼容旧 engine，但它只是过渡产物，不是长期模型。

### P3: Prompt interpolation

- 在 agent task dispatch 前渲染 prompt 模板。
- 支持内置变量：`issue.*`、`worktree.path`、`openspec.changeDir`。
- 对 `prompt.ref` 的 builtin prompt 保持现有 builder 行为；对 `prompt.inline` / `prompt.file` 执行模板渲染。
- 如果模板引用不存在变量，报清晰错误，不静默替换为空。

### P4: Repair task dispatch simplification

- 让 `fix-review-findings` 走普通 `mohist/agent` dispatch，而不是 hardcoded `repair-fix-adapter` prompt builder。
- 保留 `repair-fix-adapter` 仅用于 legacy 或 health fix 过渡路径。
- 确保 opencode ACP session 创建/复用能力不丢失：repair task 仍通过 agent-session dispatch 执行。

### P5: Check state reset behavior

- `code.changed` 后，当前 stage checks 回到 `not-run` / `pending`，approval 清空。
- task 历史保留，不把 task 标记 stale。
- 下一轮 Check stage 按定义顺序重新执行 tasks，然后重新 evaluate checks。
- UI 展示“checks reset because code.changed”，但当前 check 状态只显示三态：not-run / success / fail。

### P6: Validation and tests

- 更新 workflow-inspector YAML 编译测试。
- 更新 workflow-run-domain reset/invalidation 测试。
- 更新 stage-runner migration regression，覆盖：
  - review failure schedules `fix-review-findings`
  - repair emits `code.changed`
  - checks + approval reset
  - next Check pass reruns `ai-review`
- 保留 shared ACP session regression，确认 Plan/Check agent session 创建和复用未回退。
