# Workflow Profile

Workflow Profile 定义一个 Issue 怎样从 Draft 走到 Done，包括阶段、任务、检查、恢复和
审批点。Profile 是 Project 内的资源：一个 Project 可以拥有多个 Profile，并指定其中
一个作为默认 Profile。

Variables 和 Prompts 是独立资源，不属于 Workflow Profile。Profile 只通过
`${{ vars.* }}` 和 `${{ prompts.* }}` 使用它们。

## 选择 Profile

创建或更新 Issue 时可以显式选择同一 Project 中的 Profile。没有显式选择时，Issue 使用
Project 的默认 Profile；清除显式选择后，也会重新继承 Project 默认值。

Issue 启动 Workflow 时确定本次运行使用的 Profile。之后更换 Issue 的 Profile 或 Project
默认值，只影响下一次运行，不会改变已经开始的运行。

Mohist 默认提供：

- `mohist/local`：本地合并，适合不依赖代码托管平台的项目；默认使用。
- `mohist/github-pr`：通过一个 GitHub PR 完成交付。

`mohist/*` Profile 随 Mohist 版本更新，不能直接编辑或删除。版本更新只影响之后启动的
Workflow；已经开始的 Workflow 继续使用启动时的 definition。需要修改内置流程时，创建
一个新的 Project Profile。

## Profile 包含什么

Profile 包含：

- 名称与适用场景说明；
- stages 和各阶段的 tasks；
- stage checks 与 task completion expectations；
- approval points；
- failure recovery；
- Action Input，以及对 Variables 和 Prompts 的引用。

Profile 不包含：

- Project、Issue 或 Run 的 Variables 值；
- Prompt 正文；
- Issue 身份、仓库状态等运行上下文；
- 某次 Workflow 的执行状态和 task output。

`mohist/local` 的结构可以简化表示为：

```yaml
stages:
  - stage: plan
    requiresApproval: true
    tasks:
      - id: proposal
        title: Generate proposal
        uses: mohist/opencode
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
        expect:
          files:
            - path: openspec/changes/issue-${{ issue.number }}/proposal.md
      - id: specs
        # ...
      - id: design
        # ...
      - id: tasks
        # ...
      - id: self-review
        # ...
    checks:
      - id: plan-artifacts
        with:
          changeDir: openspec/changes/issue-${{ issue.number }}

  - stage: build
    requiresApproval: false
    tasks:
      # 按 tasks.json 执行

  - stage: check
    requiresApproval: true
    tasks:
      - id: review
        # Inline Agent review 自己的产出

  - stage: integrate
    requiresApproval: false
    tasks:
      - id: merge
        # 合并到 base branch
```

## 关键字段

### definition 语法

definition 的完整语法——stage、task、expect、artifacts、setVars、recovery、check 和
模板表达式——见 [Workflow Definition 参考](workflow-definition.md)。

内置 Profile 的执行阶段是 `plan`、`build`、`check`、`integrate`；`done` 是 Workflow
完成后的终态，不是需要配置 task 的阶段。默认 Plan 和 Check 完成后等待审批，Build 和
Integrate 自动推进。

### vars references

Profile 可以用 `${{ vars.agent }}` 等表达式读取 Variables，但不在 Profile 中声明值。

Variables 按 Project → Issue → Run 的顺序合并；同名值由后面的 scope 覆盖。Project 和
Issue 都可以设置 workflow-wide 或 per-stage 值。task 的 `setVars` 写入本次 Run 的
workflow-wide Variables，供后续 task 使用。

变量只有显式绑定到 Action Input、expect、check 或其他支持表达式的位置后才会影响执行。

### prompts references

Profile 使用 `${{ prompts.proposal }}` 等 key 引用 Project Prompt。Prompt 正文只在
Project 中配置；Issue 不提供 Prompt override。Project 没有配置某个内置 key 时，Mohist
使用 builtin Prompt。

## GitHub PR Profile

`mohist/github-pr` 与 `mohist/local` 使用相同的 Plan → Build → Check → Integrate 主干和
审批点，但交付方式不同：

- Plan 自审通过后创建或复用 draft PR；
- Check 通过后把 PR 标记为 ready；
- Integrate 等待 PR checks，通过后 squash merge；
- base branch 前进时自动 rebase，PR checks 失败时按 Profile 声明执行恢复；
- 自动恢复耗尽后停止并暴露失败原因，由用户处理后 retry。

Runner 所在机器需要安装 GitHub CLI，并登录目标仓库。

## 常见定制

### 让 Build 等待审批

把 Build 的 `requiresApproval` 改为 `true`。

### 去掉 Check

删除 Check stage。这样会缩短流程，但也失去 Integrate 前的独立 review。

### 增加 Deploy

在 Integrate 后增加 stage：

```yaml
- stage: deploy
  requiresApproval: true
  tasks:
    - id: deploy
      uses: core/shell
      with:
        command: ./scripts/deploy.sh
```

### 为某个 task 固定模型

只属于一个 task 的固定值可以直接写在 Action Input 中：

```yaml
- id: proposal
  uses: mohist/opencode
  with:
    session: plan
    prompt: ${{ prompts.proposal }}
    options:
      model: anthropic/claude-sonnet-4
      variant: high
```

需要让 Project 或 Issue 调整时，再改为 `options: ${{ vars.agent }}` 并在独立的 Variables
设置中提供值。

## 管理 Profile

在 Settings → Workflows 中管理当前 Project 的 Profile collection、编辑自定义 Profile
的 definition，并指定 Project 默认 Profile。Issue 详情页只负责选择或更换 Profile，不
直接编辑 Profile definition。

CLI 命令见 [CLI 参考](cli-reference.md#workflow-profile)。Profile ID 只需在所属 Project
内唯一；内置 Profile 使用 `mohist/<name>`，自定义 Profile 使用能稳定表达用途的 ID。

## 实装差距

- 当前 Settings 仍把默认 template、Variables 和 Prompts 组合成一份 workflow config；
  目标界面会把三个资源分开。
- 当前自定义 Workflow Definition 仍以 project template 或 Issue inline template 存在；
  目标模型会统一为 Project 的 Workflow Profile collection。
- 当前有活动 Workflow 时还不能更换 Issue 的 Profile；目标行为允许提前选择下一次运行
  使用的 Profile，同时保持当前运行不变。
- 当前 Issue 仍支持 Prompt override；目标模型只保留 Project Prompt。
- 当前进行中的 Workflow 还没有完整保存启动时的 Workflow Definition；目标行为是
  Definition snapshot 固定，运行时仍可产生新的任务，Variables 和 Prompts 继续按各自
  时机解析。
- 当前部分内置 task 仍使用旧 Action Input；目标接口以 Action 文档为准。
