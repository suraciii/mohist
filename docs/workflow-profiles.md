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

`mohist/github-pr` 走 PR-first 形态：plan stage 最后一个 task 通过显式
`mohist/create-github-pr` 打开或复用 draft PR，把 `prNumber`/`prUrl` 写入
workflow runtime variables；check stage 在 `ai-review` 通过后用
`mohist/push`（`forceWithLease: true`）把最新 commit 推到 PR head，
再用 `mohist/mark-github-pr-ready` 把 PR 标记为 ready，最后用只读的
`mohist/github-pr-status` check 确认 PR 状态并等待审批决策；integrate
stage 依次执行 `archive-change` → `push` → `merge-pr`，
用 `mohist/github-pr-status` 的 `expect: merged` check 验证 PR 已合入。

```yaml
- stage: plan
  tasks:
    - id: proposal
    - id: specs
    - id: design
    - id: tasks
    - id: self-review
      with:
        session: plan
        prompt: ${{ prompts.self-review }}
        options: ${{ vars.agent }}
      expect:
        markers:
          - path: ${{ openspecChangeDir }}/self-review.md
            oneOf:
              - <promise>PASS</promise>
              - <promise>FAIL</promise>
            failIf: <promise>FAIL</promise>
      recovery:
        budget: 2
        handlers:
          - when: promise=FAIL
            tasks:
              - id: recover:fix-plan-review
                uses: mohist/opencode
                with:
                  session: plan
                  prompt: ${{ prompts.fix-plan-review }}
                  options: ${{ vars.agent }}
            retrySelf: true
    - id: open-draft-pr        # mohist/create-github-pr
      with:
        draft: true
        titleFrom: issue.title
        bodyFrom: issue.body
      setVars:
        github.pr.number: output.prNumber
        github.pr.url: output.prUrl
  checks:
    - name: plan-artifacts     # mohist/openspec-artifacts
      with:
        changeDir: ${{ openspecChangeDir }}

- stage: check
  tasks:
    - id: ai-review
      with:
        session: check
        prompt: ${{ prompts.review }}
        options: ${{ vars.agent }}
      expect:
        markers:
          - path: ${{ openspecChangeDir }}/review.md
            oneOf:
              - <promise>PASS</promise>
              - <promise>FAIL</promise>
            failIf: <promise>FAIL</promise>
      recovery:
        budget: 2
        handlers:
          - when: promise=FAIL
            tasks:
              - id: recover:fix-review-findings
                uses: mohist/opencode
                with:
                  session: check
                  prompt: ${{ prompts.auto-fix }}
                  options: ${{ vars.agent }}
            retrySelf: true
    - id: push                 # mohist/push (forceWithLease: true)
    - id: mark-pr-ready        # mohist/mark-github-pr-ready
  checks:
    - name: github-pr-status   # mohist/github-pr-status (read-only)

- stage: integrate
  tasks:
    - id: archive-change
    - id: push
    - id: merge-pr            # mohist/merge-github-pr
      with:
        prNumber: ${{ vars.github.pr.number }}
        method: squash
      recovery:
        budget: 2
        handlers:
          - when: errorCode=base-moved
            tasks:
              - id: recover:rebase        # mohist/rebase (conflictMode: task)
                recovery:
                  budget: 1
                  handlers:
                    - when: errorCode=conflict
                      tasks:
                        - id: recover:resolve-rebase-conflicts
                          uses: mohist/opencode
                          with:
                            session: integrate
                            prompt: ${{ prompts.resolve-rebase-conflicts }}
                            options: ${{ vars.agent }}
              - id: recover:push
            retrySelf: true

          - when: errorCode=pr-checks-failed
            tasks:
              - id: recover:fix-pr-checks   # mohist/opencode
                uses: mohist/opencode
                with:
                  session: integrate
                  prompt: ${{ prompts.fix-pr-checks }}
                  options: ${{ vars.agent }}
              - id: recover:push
            retrySelf: true
  checks:
    - name: merge-verified    # mohist/github-pr-status with expect: merged
```

`self-review` 和 `ai-review` 都使用 `expect.markers` +
`failIf: <promise>FAIL</promise>` 把
marker 命中映射成 task 失败，并把命中的 promise 值作为 `output.promise`
返回。`recovery.handlers` 可以直接匹配 `promise=FAIL`（参见
[`design/workflow/actions.md`](../design/workflow/actions.md)）。失败会
触发匹配 handler 声明的 recovery task，recovery 完成后用 `retrySelf: true`
重新运行原失败 task。

`open-draft-pr` 是 plan 的最后一个 task，它把稳定的 PR 身份写入 workflow
runtime variables，后续 stage 不需要重复打开 PR：
`vars.github.pr.number` 和 `vars.github.pr.url`。

`mark-pr-ready` 只依赖 `vars.github.pr.number`，幂等：PR 已经 ready 时
`gh pr ready` 不再调用、直接成功返回。它不更新 title/body、不推送代码。

`push` 是显式同步 task，把本地线性 workflow branch 推到同名远程 branch；
`forceWithLease: true` 用于 rebase 后允许 head history 重写。`push` 不
声明业务 recovery —— 失败意味着权限/网络或远程 branch 被外部写入，应作为
普通 task failure 暴露。

`merge-pr` 等待 GitHub PR checks，通过后 `gh pr merge --squash`，并重新
查询确认 `state=MERGED` 才视为集成完成。`merge-verified` check 通过
`mohist/github-pr-status` 的 `expect: merged` 做只读确认。

rebase 冲突只通过 task-level recovery 处理。冲突时返回
`errorCode: conflict` 并保留 rebase 进行中；profile 的
`recover:rebase` recovery（`errorCode=conflict` →
`recover:resolve-rebase-conflicts`）由 Inline Agent 解决冲突、完成 rebase，然后
workflow 继续走 `recover:push` 并 `retrySelf: true` 重新合并。

PR title/body 不从 workflow metadata 读取。profile 通过
`titleFrom: issue.title`、`bodyFrom: issue.body` 指示 `mohist/create-github-pr`
在运行时执行 `mo issue show <number> --project-id <projectId> --output json`，
再用返回的 issue title/body 创建或更新 PR。

`mohist/github-pr` 使用四个 GitHub PR action：

- `mohist/create-github-pr` 推送 workflow branch、创建或复用 draft PR，
  返回 `output.prNumber` 和 `output.prUrl` 供 `setVars` 写入 workflow
  runtime variables。
- `mohist/mark-github-pr-ready` 只读 `vars.github.pr.number`，调用
  `gh pr ready` 把 draft PR 标记为 ready；幂等。
- `mohist/push` 推送本地 workflow branch 到远程 head；可选
  `forceWithLease: true` 携带 `--force-with-lease`。
- `mohist/merge-github-pr` 读取 `vars.github.pr.number`，在真正 merge 前
  等待 GitHub PR checks，通过后 `gh pr merge --squash` 并确认
  `state=MERGED`；recoverable failure 返回 action-owned
  `errorCode: base-moved` 或 `errorCode: pr-checks-failed`，由 profile 的
  `merge-pr.recovery` 显式处理。
- `mohist/github-pr-status` 是只读 check：默认验证 PR 已 ready，
  `expect: merged` 验证 PR 已合入。

PR checks 属于 `mohist/merge-github-pr` 的内部前置条件，不是 stage-level
check。`pr-checks-failed` 在 `merge-pr.recovery` 里显式声明
（`recover:fix-pr-checks` → `recover:push` → `retrySelf: true`），失败后由
Inline Agent 自动修并重新合并；不依赖 stage hook 或隐式边界动作。

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

`requiresApproval` 的含义是阶段完成后需要审批。Workflow 只关心是否收到 approve / reject 决策，不关心审批者是 owner、脚本还是 Mohist Agent。

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

完整变量列表看 [`design/workflow/profile.md`](../design/workflow/profile.md)。
