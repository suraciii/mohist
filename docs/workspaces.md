# Workspace

Workspace 是 Project 下持久存在的执行环境：一组工作目录，加上若干仓库的访问权。
它跨会话、跨 Agent 存在——多个会话、多个 Agent 可以在同一个 workspace 里接力，
后加入者看到的是同一个目录：装好的依赖、检索到的资料、未提交的改动都在。

Workspace 是“工作”发生的地方，仓库只是它的材料：仓库检出位于 workspace 之下，
而计划、调研、笔记等工作产物直接属于 workspace，不属于任何一个仓库——一件横跨
多个仓库的工作，其成果在 workspace 层始终有安放之处。

## 两种来源

### Issue 工作流的 workspace

Issue 首次启动时自动获得自己的 workspace（命名为 `issue-<编号>`）。它**干净初始化**：
从目标仓库全新检出，不与其他 issue 共享任何目录——大量 issue 并行推进时互不干扰。

同一个 issue 的所有阶段、重试、会话和被拉进来的 Agent 共享这个 workspace；
issue 完成或取消时，workspace 随之归档。

### 交互入口的 workspace

从 Slack、Web 或 CLI 直接发起的会话同样落在 workspace 里。默认规则是**一个交互
场所一个 workspace**：

- **Slack**：一个 channel 一个 workspace。channel 里发起的所有会话、被拉进来的
  所有 Agent，都在同一个 workspace 里工作。
- **Web**：一个对话一个 workspace。
- **CLI**：`mo workspace create <name>` 显式创建，启动会话时用 `--workspace <name>`
  绑定。不带 `--workspace` 的 `mo agent launch` 绑定当前 Project 的默认 Workspace，必要时
  创建 `cli-current`（来源记录为 `cli`）；CLI 输出会显示实际绑定，避免隐藏默认作用域。

交互入口的 workspace 不做干净初始化：它持久累积。这正是跨会话复用的价值。

## 绑定与共享

- 新会话默认绑定所在场所解析出的 workspace；在同一个 channel 里再开新会话，
  落在同一个 workspace。
- 把别的 Agent 拉进会话或 channel，它就进入同一个 workspace，看到同样的文件。
- 会话里委托出的子会话继承同一个 workspace；需要隔离的子会话，委托时绑定另一个
  workspace，或由 Agent 在目录内自行 git worktree（git 范畴的工具，平台不提供
  “隔离工作空间”原语）。
- 任何时候，一个场所只对应一个活跃 workspace——“我在这里说话，Agent 在哪个目录”
  永远有唯一答案。

## 命令面

```bash
# 建立共享环境，两个 Agent 先后加入
mo workspace create payment-refactor --repo server --repo web
mo agent launch coder --workspace payment-refactor
mo agent launch reviewer --workspace payment-refactor

# 观察：来源、仓库、当前绑定的会话
mo workspace list --status active
mo workspace view payment-refactor
mo session list --workspace payment-refactor

# 调整仓库成员；归档（有活跃会话时会被拒绝并提示下一步）
mo workspace repo add payment-refactor infra
mo workspace close payment-refactor
```

完整命令契约见 [CLI 参考](cli-reference.md#workspace)。

## 仓库

Workspace 持有一组仓库引用（Project 已声明的仓库资源）。workflow 路径从 issue 的
目标仓库预填；交互路径按需挂载。挂载表示授予访问权和默认检出目标；真正的 clone、
分支与 worktree 组织由 Agent 在目录内自行完成，平台不规定目录内部结构。布局约定
（检出放 `repos/` 下、工作产物放 workspace 根）由 prompt 承载，不是平台强制。

## 生命周期终点

- Issue 完成或取消 → 其 workflow workspace 自动归档。
- `mo workspace close <name>` 归档任意 workspace。
- Slack channel 归档 → 对应 workspace 归档；channel 里的下一条消息自动开始一个
  全新的 workspace——"搞乱了重来"就是 close 后继续说话。
- 归档后 workspace 保留历史可查，不再接受新会话。

## 事件

Workspace 的创建与归档是平台事件（`workspace.created` / `workspace.archived`），
携带 Project、Workspace 名称与来源（issue / manual / slack / web / cli）。订阅方可以按
来源过滤——例如：渠道 Agent 在收到归档事件后收尾，创建事件触发依赖预装。
事件路由见 [事件路由](event-routing.md)，订阅语法见[事件协议](event-protocol.md)。

## 目录丢失

Workspace 的目录由执行它的 runner 承载。runner 故障或磁盘清理后，workspace 本身
仍在，但目录内容不保证恢复：再次使用时从空目录开始，未推送的工作随之丢失。
工作流靠推送到远端的 workflow branch 保留成果；交互 workspace 里重要的东西应该
提交并推送。

## 实装差距

Workspace 实体与生命周期（创建/归档）已实装；交互入口（Slack / Web 来源）的
动态创建、Slack channel 归档到 workspace 归档的接线尚未落地，当前交互入口只
覆盖 manual 来源。目录物化仍在 runner 侧按 WorkflowRun 组织，跨会话复用与
回收守卫的 Workspace 视角切换待推进。
