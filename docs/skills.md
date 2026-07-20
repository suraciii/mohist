# Skill 机制

Mohist 不内置对话式探索。需求挖掘、产品思考这类**需要实时互动**的工作，由外部 agent 通过 Skill 完成。探索的结果应当是可进入 workflow 的 ready issue。

探索的输入不限形态：一句话的想法、一次讨论的结论、一份现成的需求材料，都可以直接交给探索。Skill 只补齐还没想清楚的部分，不会把已经想清楚的重新问一遍。

## 为什么探索在外部

探索式对话的本质是**挖掘用户需求**。这件事必须：

- **实时**进行（你边说 AI 边想）
- **互动式**进行（AI 反问、确认、调整）
- **托管不了**（不像 workflow 那样可以丢进去不管）

所以架构上，Mohist 把探索划到外部：

```
你 + 外部 agent（OpenCode/Claude Code/Hermes 等）
  ↓ 探索（用 mohist-explore skill）
  ↓ 整理出结构化需求
ready issue
  ↓ 进入 Mohist workflow
Mohist runtime（workflow 执行）
```

## Mohist 分发的 Skill

`mo skills list` 看可分发的 skill。当前有四个：

### `mohist`

操作 Mohist 本身的 skill，也是场景分发入口。让外部 agent 能：

- 创建 / 查看 / 启动 / 审批 issue，驱动 epic
- 查项目状态、日志
- 调用 `mo` CLI
- 按场景加载下面的专用 skill

适用场景：你在 OpenCode 里写代码，想顺手建个 Mohist issue，不想切到 Web UI。

### `mohist-explore`

从产品视角把需求想清楚的 skill。接受任何成熟度的输入——一句模糊的想法，或一份已经定稿的需求材料——引导你：

- 对照它的思考问题清单，标出哪些已经有答案、哪些还没有；已有答案的不再重问
- 只就缺口提问：用户价值、产品边界、领域约束
- 决定拆成一个 issue 还是多个（epic）；每个 issue 必须能独立交付价值，并给出依赖顺序

适用场景：你有一个模糊的想法，想理清楚边界和验收条件；或你已有成形的需求材料，想把它切成能独立交付的 issues。

### `mohist-create-issue`

创建 issue 的执行 skill：选模板、填内容、推荐 workflow 与风险、打标签、确认后执行 `mo issue create`。创建前会核对每个 issue 是否能独立交付价值——不论需求内容来自哪条路径。

### `mohist-create-epic`

创建 epic 的执行 skill：写里程碑描述、关联 issues、设置前置依赖、驱动 autopilot 生命周期。

## 安装 Skill 到外部 Agent

```bash
mo skills install
```

这会把 skill 内容同步到你的外部 agent 配置目录。具体位置看 `mo skills install --help`。

安装后，在外部 agent 里就能触发：

- OpenCode：自动从 skill description 决定何时调用
- Claude Code：通过 skill description 匹配
- 其他 agent：看该 agent 的 skill 加载机制

## 获取 Skill 完整内容

`mo skills list` 给的是 discovery stub（简短描述）。完整内容用：

```bash
mo skills get mohist
mo skills get mohist-explore
mo skills get mohist-create-issue
mo skills get mohist-create-epic
```

这会输出与当前 Mohist 版本匹配的完整 skill 指令。每次 `mo skills install` 都会刷新。

## 典型工作流：从想法到 issue

### 场景：你想加个搜索功能，但细节没想清楚

1. **打开外部 agent**（如 OpenCode）：

   ```
   我想给任务列表加个搜索功能，帮我探索一下应该怎么做
   ```

2. **外部 agent 触发 `mohist-explore` skill**：

   AI 会问你：
   - 搜索范围（title、description、标签？）
   - 是否需要高亮
   - 是否需要历史搜索
   - 性能要求（100 条 vs 10000 条）
   - 等

3. **探索完成**，AI 产出结构化 issue body：

   ```markdown
   ## Background
   用户反馈找不到旧任务，需要搜索能力...

   ## Goal
   按标题实时模糊搜索...

   ## Non-goals
   - 不搜索 description
   - 不做高级筛选
   ...

   ## Acceptance criteria
   - 输入即过滤（< 100ms 响应）
   - 大小写不敏感
   ...
   ```

4. **用 `mohist` skill 创建 issue**：

   ```
   把这个 issue 创建到 mohist-local 项目
   ```

   AI 调用 `mo issue create` 把探索产物作为 body 创建。

5. **回到 Mohist**：

   ```bash
   mo issue start <new-number>
   ```

   workflow 接管，开始执行。

## 不想用外部 agent 怎么办

你也可以直接用 Web UI 或 CLI 创建 issue。Skill 只是让"探索 → ready issue"更顺滑，不是强制路径。

但**强烈建议**写 issue body 时参考 explore skill 的结构（Background/Goal/Non-goals/Acceptance）。这个结构直接决定 plan 质量。

## Skill 的边界

Skill **能**做：

- 调用 `mo` CLI 操作 issue/workflow
- 写普通文件（探索笔记、issue body 草稿）
- 读取项目状态

Skill **不能**做：

- 直接写 Mohist 数据库
- 依赖 Mohist 内部 runtime session
- 替代 Mohist workflow 执行

## 自定义 Skill

你完全可以写自己的 skill，比如：

- 项目特定的需求模板
- 团队 code review checklist
- 特定类型 issue 的探索流程

skill 是普通文件（放在你外部 agent 的 skill 目录下）。看 `mo skills get mohist-explore` 的输出学结构。

分发自己的 skill 当前需要手动复制到外部 agent 目录（roadmap：通过 `mo skills` 统一管理）。

## 参考

- skill 源文件：`packages/cli/Mohist.Cli/skill-data/<skill-name>/SKILL.md`
- 架构边界：[`design/architecture.md`](../design/architecture.md) 的 "Agent Skill Boundary" 章节

---

对应源码：`packages/cli/Mohist.Cli/skill-data/`、`design/architecture.md`（Agent Skill Boundary）。
