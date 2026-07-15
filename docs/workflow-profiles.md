# Workflow Profile

Workflow Profile 定义"Issue 怎么从 Draft 走到 Done"，包括阶段、任务、检查、恢复和审批点。当前 Mohist 自带 `mohist/local` 和 `mohist/github-pr`。只有当 profile 的描述和实际执行定义一致时，系统才会把它暴露给用户选择。

## 默认 Profile

`mohist/local` 的结构（简化）：

```yaml
variables:
  agent: {}

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
            - path: ${{ openspecChangeDir }}/proposal.md
      - id: specs
        # ...
      - id: design
        # ...
      - id: tasks
        # ...
      - id: self-review
        # ...
    checks:
      - name: plan-artifacts     # 验证 proposal.md / specs / design.md / tasks.json 全部就位
        with:
          changeDir: ${{ openspecChangeDir }}

  - stage: build
    requiresApproval: false      # 默认 build 不等待审批
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

## GitHub PR Profile

`mohist/github-pr` 适合希望每个 issue 都以一个可审阅、可追溯的 GitHub PR 交付的团队。阶段骨架与 `mohist/local` 完全一致（Plan → Build → Check → Integrate，审批点同样在 Plan 和 Check 之后），区别只在交付方式：`mohist/local` 把工作分支直接合入 base branch，`mohist/github-pr` 全程通过一个 GitHub PR 走完。

流程上的差别：

- **Plan 通过自审后打开 draft PR。**PR 的标题和正文取自 issue 的标题和描述。从这一刻起，这个 issue 的所有后续改动都汇聚在同一个 PR 上，团队随时可以在 GitHub 上跟进。
- **Check 阶段把 PR 变为 ready。**AI review 通过、最新改动推上 PR 之后，PR 从 draft 变为 ready for review，然后进入审批点等待决策。
- **Integrate 阶段合并 PR。**等 PR 上的检查全部通过后 squash 合并，并确认 PR 确实已合入才算集成完成。

集成失败时的自动恢复：

- base branch 在等待合并期间前进了 → 自动 rebase 到最新 base 后重试合并；出现冲突时由 Inline Agent 解决。
- PR 上的检查失败 → Inline Agent 自动修复、更新 PR，再重试合并。
- 自动恢复超出预算仍失败时，issue 停在失败状态；你可以在 Web UI 或 CLI 里查看失败原因，处理后重试。

使用要求：Runner 所在机器需要安装 GitHub CLI 并已登录目标仓库。

## 关键字段

### stage

阶段名。默认 5 个：`plan`、`build`、`check`、`integrate`、`done`。

### requiresApproval

`true` = 阶段完成后进入审批点，等待 approve / reject 决策。
`false` = 阶段完成后自动进入下一阶段。

默认：
- Plan: `true`（等待审批）
- Build: `false`（自动跑）
- Check: `true`（等待审批）
- Integrate: `false`（自动合并）

`requiresApproval` 的含义是阶段完成后需要审批；审批者可以是谁、决策怎么给出，见 [核心概念的 Approval 节](concepts.md#approval审批)。

### tasks

阶段里要执行的任务序列。每个 task：

- `id`：唯一标识
- `title`：人读的名字
- `uses`：用哪个 action（如 `mohist/opencode`、`core/artifact-exists`）
- `with`：传给 action 的参数；`mohist/opencode` 只使用 `prompt`、`session`、`options`
- `expect`：Workflow 对本次 task 的完成要求，不属于 Action Input
- `artifacts`：声明产出哪些文件

### checks

阶段完成前的验证。比如"proposal.md 文件存在"。Check 不过 → task 视为失败。

### variables

可复用变量。最常见的是 `agent`：这是现有变量名，值中的 `model` 和 `variant` 会作为
`mohist/opencode` 的 `options`，不表示 Agent 身份。
变量只有通过 `options: ${{ vars.agent }}` 绑定到 task 后才会影响执行。

### prompts

引用 prompt 模板（来自 Settings → Templates）。模板支持变量插值。

## 改 Workflow Profile

### 通过 Web UI

Settings → Workflows → 选 profile → 编辑 yaml。

### 通过文件（开发场景）

直接改 `mohist-local.workflow.yaml`，重启 server 生效。

## 常见定制场景

### 1. 让 Build 也等待审批

把 build stage 的 `requiresApproval` 改为 `true`。适合你需要在实现后、审查前增加一个审批点的场景。

### 2. 去掉 Check 阶段

适合简单项目不想多一层审查。直接删掉 check stage 的整段。

注意：去掉 check 意味着你信任 build 的产出，没有 Inline Agent 二次 review。

### 3. 加 deploy 阶段

在 integrate 后加：

```yaml
- stage: deploy
  requiresApproval: true
  tasks:
    - id: deploy
      uses: core/shell
      with:
        command: ./scripts/deploy.sh
```

### 4. 改 AI 模型 per-stage

每个 task 的 `with.options` 可以指定不同模型：

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

也可以把同一个 `options` 对象放进 project、issue 或 stage variables，再用
`options: ${{ vars.agent }}` 绑定。Workflow 不限制变量由哪一层提供。

## 创建新 Profile

当前版本不支持通过 UI 创建新 profile（roadmap）。临时方案：

1. 复制 `mohist-local.workflow.yaml` 为 `<your-name>.workflow.yaml`
2. 放在 WorkflowProfiles 目录下
3. 修改内容
4. 重启 server

之后 `mo issue create --workflow-profile <your-name>` 就能用。

## Inline Agent 实装差距

本文按目标接口使用 `mohist/opencode` 和 `options: ${{ vars.agent }}`。
当前内置 profile 仍使用 `mohist/acp-agent` 和旧的 `agent` input；在
[`mohist/opencode` Action](actions/opencode.md) 所述替换完成前，自定义现有 profile 时仍需
以当前可用 action 为准。当前 schema 还把 `expect` 放在 `with` 中；目标实现会把它
提升为 Workflow task 的完成契约，使 OpenCode Action Input 保持最小。

## Profile ID 约定

- `mohist/local` → 官方默认
- `mohist/<name>` → 官方提供的其他 profile；只有实现了独立执行定义后才会暴露
- `<your-org>/<name>` → 你自定义的

## 当前的限制

Roadmap（已知不足）：

- Profile 目前只有 `description` 描述性元数据；更结构化的 `risk_level`、`suitable_for` 等字段未提供
- quick-fix、experiment 这类轻量 profile 尚未内置，避免展示与实际执行不一致的假选项
- 没有"按 issue 内容自动推荐 profile"机制
- 不能 import/export profile
- 没有 profile 调试模式（dry run）

这些都在 roadmap 里。如果你需要这些能力，欢迎贡献或提 issue。

## 进阶：prompt 模板

Workflow 里的 `prompts.proposal` 等模板可以在 Settings → Templates 里编辑。

模板支持变量：

- `${{ issue.number }}` / `${{ issue.id }}`
- `${{ project.id }}` / `${{ project.name }}`
- `${{ repository.baseBranch }}`
- `${{ openspecChangeDir }}`
- `${{ vars.agent }}`
- 等等

完整 issue 内容由 Inline Agent 通过 CLI 获取，例如：

```bash
mo issue show ${{ issue.number }} --project-id ${{ project.id }}
```
