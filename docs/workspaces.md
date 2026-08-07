# Workspace

Workspace 是 Project 下持久存在的执行环境：一组工作目录，加上若干仓库的访问权。
它跨会话、跨 Agent 存在——多个会话、多个 Agent 可以在同一个 workspace 里接力，
后加入者看到的是同一个目录：装好的依赖、检索到的资料、未提交的改动都在。

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
  绑定。不带 `--workspace` 的 `mo agent launch` 不绑定任何 workspace——沿用 runner
  默认工作目录，没有跨会话连续性，也不产生 workspace 实体。

交互入口的 workspace 不做干净初始化：它持久累积。这正是跨会话复用的价值。

## 绑定与共享

- 新会话默认绑定所在场所解析出的 workspace；在同一个 channel 里再开新会话，
  落在同一个 workspace。
- 把别的 Agent 拉进会话或 channel，它就进入同一个 workspace，看到同样的文件。
- 会话里委托出的子会话继承同一个 workspace。
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
分支与 worktree 组织由 Agent 在目录内自行完成，平台不规定目录内部结构。

## 生命周期终点

- Issue 完成或取消 → 其 workflow workspace 自动归档。
- `mo workspace close <name>` 归档任意 workspace。
- Slack channel 归档 → 对应 workspace 归档；channel 里的下一条消息自动开始一个
  全新的 workspace——"搞乱了重来"就是 close 后继续说话。
- 归档后 workspace 保留历史可查，不再接受新会话。

## 目录丢失

Workspace 的目录由执行它的 runner 承载。runner 故障或磁盘清理后，workspace 本身
仍在，但目录内容不保证恢复：再次使用时从空目录开始，未推送的工作随之丢失。
工作流靠推送到远端的 workflow branch 保留成果；交互 workspace 里重要的东西应该
提交并推送。

## 实装差距

当前 workspace 仅是 runner 侧按 WorkflowRun 物化的临时 worktree：没有独立身份、
不能跨会话复用、没有交互入口来源，也没有归档概念。本文描述的是目标形态。
