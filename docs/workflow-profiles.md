# Workflow Profile

Workflow Profile 定义"Issue 怎么从 Draft 走到 Done"。当前 Mohist 自带一个默认 profile（`mohist/default`）。只有当 profile 的描述和实际执行定义一致时，系统才会把它暴露给用户选择。

## 默认 Profile

文件位置：`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml`

结构（简化）：

```yaml
variables:
  agent:
    type: opencode

stages:
  - stage: plan
    requiresApproval: true
    tasks:
      - id: proposal
        title: Generate proposal
        uses: mohist/acp-agent
        with:
          prompt: ${{ prompts.proposal }}
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
      - name: proposal-complete
        # ...

  - stage: build
    requiresApproval: false      # 默认 build 不审批
    tasks:
      # 按 tasks.json 执行

  - stage: check
    requiresApproval: true
    tasks:
      - id: review
        # AI review 自己的产出

  - stage: integrate
    requiresApproval: false
    tasks:
      - id: merge
        # 合并到 base branch
```

## 关键字段

### stage

阶段名。默认 5 个：`plan`、`build`、`check`、`integrate`、`done`。

### requiresApproval

`true` = 阶段完成后暂停等你审批。
`false` = 阶段完成后自动进入下一阶段。

默认：
- Plan: `true`（必审批）
- Build: `false`（自动跑）
- Check: `true`（必审批）
- Integrate: `false`（自动合并）

### tasks

阶段里要执行的任务序列。每个 task：

- `id`：唯一标识
- `title`：人读的名字
- `uses`：用哪个 action（如 `mohist/acp-agent`、`core/artifact-exists`）
- `with`：传给 action 的参数（prompt、expect 等）
- `artifacts`：声明产出哪些文件

### checks

阶段完成前的验证。比如"proposal.md 文件存在"。Check 不过 → task 视为失败。

### variables

可复用变量。最常见的是 `agent`（coder agent 类型）。

### prompts

引用 prompt 模板（来自 Settings → Templates）。模板支持变量插值。

## 改 Workflow Profile

### 通过 Web UI

Settings → Workflows → 选 profile → 编辑 yaml。

### 通过文件（开发场景）

直接改 `mohist-default.workflow.yaml`，重启 server 生效。

## 常见定制场景

### 1. 让 Build 也审批

把 build stage 的 `requiresApproval` 改为 `true`。适合你想中间介入的场景。

### 2. 去掉 Check 阶段

适合简单项目不想多一层审批。直接删掉 check stage 的整段。

注意：去掉 check 意味着你信任 build 的产出，没有 AI 二次 review。

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

每个 task 的 `with.agent` 可以指定不同模型：

```yaml
- id: proposal
  uses: mohist/acp-agent
  with:
    agent:
      type: opencode
      model: claude-sonnet-4   # 用强模型做规划
```

或通过 Web UI Settings → Coder Agent → Stage Model Overrides。

## 创建新 Profile

当前版本不支持通过 UI 创建新 profile（roadmap）。临时方案：

1. 复制 `mohist-default.workflow.yaml` 为 `<your-name>.workflow.yaml`
2. 放在 WorkflowProfiles 目录下
3. 修改内容
4. 重启 server

之后 `mo issue create --workflow-profile <your-name>` 就能用。

## Profile ID 约定

- `mohist/default` → 官方默认
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

完整 issue 内容由 Agent 通过 CLI 获取，例如：

```bash
mo issue show ${{ issue.number }} --project-id ${{ project.id }}
```

完整变量列表看 [`design/workflow/profile.md`](../design/workflow/profile.md)。

---

对应源码：`Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml`、[`design/workflow/profile.md`](../design/workflow/profile.md)。
