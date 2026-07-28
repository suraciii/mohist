# Skill 机制

Skill 是可复用的 Agent 能力说明，在 Mohist 中有两种使用方式：

- 第三方的外部 Agent 安装 Mohist Skill，在 Slack、IDE 或其他 Agent host 中判断场景，
  再用 `mo` 查询状态、委托工作或执行操作。它保留自己的对话上下文。
- Mohist Agent 在配置中选择 Skills。无论从 Web、CLI、Slack Agent 接入还是事件启动，
  都使用同一组 Skills；入口不能替 Agent 临时增加或删除能力。

外部 Agent 不是 Mohist Agent：前者在 Mohist 之外与用户交互，后者是 Project 内由 Mohist
独立运行的稳定 Agent 资源。Slack Bot 也不是外部 Agent，它只是 Agent 接入中代表某个
Mohist Agent 的客户端身份。Inline Agent 则由 Workflow 直接调用。完整术语见
[核心概念](concepts.md)。

需求挖掘、产品思考这类**需要实时互动**的工作，可以由外部 Agent 通过 Skill 完成，也
可以由配置了 `mohist-explore` 的 Mohist Agent 在 Web 或 Slack 中完成。探索的结果应当是
可进入 Workflow 的 ready Issue。

探索的输入不限形态：一句话的想法、一次讨论的结论、一份现成的需求材料，都可以直接交给探索。Skill 只补齐还没想清楚的部分，不会把已经想清楚的重新问一遍。

## 为什么日常入口在外部

用户已经在 Slack、IDE 或其他场所拥有连续的对话和工作上下文。Mohist 不复制这些工作
场所；它让 Mohist Agent 通过 Agent 接入出现在这些场所，也让外部 Agent 通过 Skill 调用
执行层。探索式对话尤其需要：

- **实时**进行（你边说 AI 边想）
- **互动式**进行（AI 反问、确认、调整）
- **托管不了**（不像 workflow 那样可以丢进去不管）

所以 Mohist 把主要交互入口放在外部，但 Agent 可以由 Mohist 自己运行：

```text
你 + Slack Bot ── Agent 接入 ── Mohist Agent + 已配置 Skills
                                      │
你 + 外部 Agent ── Mohist Skill + mo ┤
                                      ▼
                         Issue / Workflow / AgentJob 执行与留痕
```

## Mohist 分发的 Skill

`mo skill list` 看可分发的 Skill。当前有四个：

### `mohist`

操作 Mohist 本身的 Skill，也是场景分发入口。让外部 Agent 能：

- 创建 / 查看 / 启动 / 审批 Issue，驱动 Epic
- 汇总项目推进、待处理事项、阻塞和异常
- 查看日志与执行证据，选择确定的恢复动作
- 调用 `mo` CLI
- 按场景加载下面的专用 Skill

适用场景：日常从外部 Agent 查询和操作 Mohist，例如“当前哪些 Issue 在推进，推进是否有
问题”或“把这个需求交给 Mohist 执行”。

### `mohist-explore`

从产品视角把需求想清楚的 Skill。接受任何成熟度的输入——一句模糊的想法，或一份已经定稿的需求材料——引导你：

- 对照它的思考问题清单，标出哪些已经有答案、哪些还没有；已有答案的不再重问
- 只就缺口提问：用户价值、产品边界、领域约束
- 决定拆成一个 issue 还是多个（epic）；每个 issue 必须能独立交付价值，并给出依赖顺序

适用场景：你有一个模糊的想法，想理清楚边界和验收条件；或你已有成形的需求材料，想把它切成能独立交付的 issues。

### `mohist-create-issue`

创建 Issue 的执行 Skill：选模板、填内容、推荐 Workflow 与风险、打标签、确认后执行 `mo issue create`。创建前会核对每个 Issue 是否能独立交付价值——不论需求内容来自哪条路径。

### `mohist-create-epic`

创建 Epic 的执行 Skill：写里程碑描述、关联 Issue、设置前置依赖、驱动 autopilot 生命周期。

## 安装 Skill 到外部 Agent

```bash
mo skill install
```

这会把 Skill 内容同步到你的外部 Agent 配置目录。具体位置看 `mo skill install --help`。

安装后，在外部 Agent 里就能触发：

- OpenCode：自动从 Skill description 决定何时调用
- Claude Code：通过 Skill description 匹配
- 其他 Agent 工具：使用各自的 Skill 加载机制

## 获取 Skill 完整内容

`mo skill list` 给的是 discovery stub（简短描述）。完整内容用：

```bash
mo skill view mohist
mo skill view mohist-explore
mo skill view mohist-create-issue
mo skill view mohist-create-epic
```

这会输出与当前 Mohist 版本匹配的完整 Skill 指令。每次 `mo skill install` 都会刷新。

## 典型工作流：从想法到 Issue

### 场景：你想加个搜索功能，但细节没想清楚

1. **在已有工作场所与外部 Agent 对话**：

   ```
   我想给任务列表加个搜索功能，帮我探索一下应该怎么做
   ```

2. **外部 Agent 触发 `mohist-explore` Skill**：

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

4. **用 `mohist` Skill 创建 Issue**：

   ```
   把这个 issue 创建到 mohist-local 项目
   ```

   AI 调用 `mo issue create` 把探索产物作为 body 创建。

5. **让外部 Agent 启动并跟踪 Issue**：

   ```text
   启动这个 Issue，后续告诉我是否需要处理。
   ```

   外部 Agent 使用 `mo issue start` 启动，Workflow 接管执行；用户继续留在原来的交互场所。
   需要完整状态、执行证据或人工接管时，再打开 Web UI。

## 典型查询：项目是否正常推进

在外部 Agent 中直接询问：

```text
@mohist 当前哪些 Issue 在推进，推进是否有问题？
```

外部 Agent 通过 Mohist Skill 读取项目、Issue、Workflow 和 Runner 状态，区分正常推进、
等待决策、阻塞和异常，再返回结论与可执行的下一步。用户不需要先进入 Web UI 或自己拼接
多个状态字段。

## 直接使用 Mohist Agent、CLI 或 Web UI

外部交互场所是默认入口，不代表只能使用外部 Agent。你可以在 Web UI 或 CLI 中直接启动
一个配置了 Skills 的 Mohist Agent；也可以直接使用同一套 `mo` 领域命令。需要全局可视化、
复杂证据或人工接管时，可以使用 Web UI。Web UI 是备用操作和可视化平面，关键操作保持
完整。

直接创建 Issue 时仍建议参考 explore Skill 的结构
（Background / Goal / Non-goals / Acceptance），这个结构直接决定 Plan 质量。

## Skill 的边界

Skill **能**做：

- 调用 `mo` CLI 操作 Issue、Workflow 和其他 Mohist 资源
- 写普通文件（探索笔记、Issue body 草稿）
- 读取项目状态

Skill **不能**做：

- 直接写 Mohist 数据库
- 依赖 Mohist 内部 runtime session
- 替代 Mohist workflow 执行

Agent 接入同样不能改变 Skill：它只传递消息和展示结果。一个 Agent 在 Web 中测试时能用
哪些 Skills，接入 Slack 后仍然只能用这些 Skills。

## 自定义 Skill

你完全可以写自己的 skill，比如：

- 项目特定的需求模板
- 团队 code review checklist
- 特定类型 issue 的探索流程

Skill 是普通文件（放在外部 Agent 的 Skill 目录下）。看 `mo skill view mohist-explore` 的输出学结构。

分发自己的 Skill 当前需要手动复制到外部 Agent 目录（roadmap：通过 `mo skill` 统一管理）。

## 实装差距

当前 `mo skill install` 和 `mo skill view` 已能服务外部 Agent。Mohist Agent 的 Skills
字段已经可以配置，但 Runner 尚未按 AgentJob 快照加载对应 Skill；因此“同一 Agent 在
所有入口使用同一组 Skills”仍是目标行为，实施应先补执行语义，再接 Slack。

---

对应源码：`packages/cli/Mohist.Cli/skill-data/`、`design/architecture.md`（Agent Skill Boundary）。
