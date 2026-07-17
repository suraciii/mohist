# 复合 Issue 与子 Issue

一个需求有时大到没法作为一个 issue 交付——最典型的：改动横跨多个仓库，每个仓库的改动要各自走完整 workflow。复合 issue 让你在**一个 issue 里追踪需求整体**，把执行拆给若干**子 issue**。

## 心智模型

- 从一个 issue 拆出子 issue 后，它就成为**父 issue**（复合 issue）：父 issue 不再自己进 workflow，它的交付 = 全部子 issue 的交付。
- **子 issue 是完整的普通 issue**：有自己的目标仓库、自己的 workflow、自己的审批点、自己的 prerequisite。子 issue 的执行和单独创建的 issue 没有任何区别。
- 层级只有一层：子 issue 不能再拆出子 issue。
- 复合 issue 与 Epic **正交**：Epic 是产品目标的组织与供料层，复合 issue 是**一份工作的内部分工**。子 issue 不属于任何 Epic；父 issue 在 Epic 眼里就是一个普通 issue。

**什么时候用复合 issue，什么时候用 Epic**：如果各部分是独立有价值的交付物（每完成一个，产品就前进一步），用 Epic 组织普通 issue；如果各部分只是同一份需求的分工（只完成一半没有产品意义，比如 server 改了 API 而 web 还没接），用复合 issue。

## 拆分

需求背景、整体目标和验收标准写在父 issue 的 body；每个子 issue 的 body 写清楚自己负责的范围。

```bash
# 父 issue 描述需求整体
mo issue create "订阅通知功能" --body-file ./subscription-feature.md

# 拆出子 issue（假设父 issue 编号为 42）
mo issue create "server: 订阅 API 与事件推送" --parent 42 --repo server
mo issue create "web: 订阅管理页"             --parent 42 --repo web

# 需要先后顺序时用 prerequisite（web 等 server）
mo issue prereq add 44 43
```

- 也可以把已有的 backlog issue 挂为子 issue：`mo issue update 43 --parent 42`；解除用 `mo issue update 43 --parent none`。
- 子 issue 创建时不指定 priority 就继承父 issue 的。
- 拆分是你（或帮你操作的外部 agent）的决策，Mohist 不做自动拆分。
- 子 issue 进入 Plan 阶段时，父 issue 的标题与描述会作为背景上下文提供给 Inline Agent——共享背景不需要往每个子 issue 复制。

**拆分的约束**：

- 已进入 workflow 的 issue 不能拆分，也不能被挂为子 issue（先 stop 再拆）。
- 已到终态的父 issue 不能再加子 issue（先 reopen）。
- 有子 issue 的 issue 不能被挂为别人的子 issue（只有一层）。

## 推进

```bash
mo issue start 42    # 启动父 issue = 复合推进
```

启动父 issue 后：

1. 所有**可启动**的子 issue（在 backlog、prerequisite 已满足）**并行**启动，各自在各自仓库里走 workflow。
2. 之后每当一个子 issue 到达终态，被解锁的子 issue 自动启动，直到全部终态。
3. 并发上限约束照常生效，复合推进不会突破它。

复合推进是并行的——这与 Epic 的逐个供料**有意不同**：Epic 控制的是一个目标下的在制品数量；复合 issue 的各子项是同一份工作的分工，越早全部完成越好，顺序约束由 prerequisite 表达。

你也可以不启动父 issue，逐个手动 `mo issue start` 子 issue——复合推进是便捷方式，不是唯一入口。

审批、retry、rerun、force-stop 等所有 workflow 操作都发生在**子 issue** 上，父 issue 没有 workflow，也没有审批点。

## 状态与进度

父 issue 的状态由子 issue 汇总而来，不需要你手动维护：

| 父 issue 状态 | 何时 |
|---|---|
| `backlog` | 尚未启动，且没有子 issue 开始工作 |
| `in-progress` | 已启动复合推进，或任一子 issue 已开始工作 |
| `done` | 全部子 issue 到达终态，且至少一个 done（自动） |
| `cancelled` | 全部子 issue 都被取消（自动） |

- 父 issue 没有 workflow 阶段；详情页展示子 issue 清单与进度（X/Y done、blocked 计数），看板卡片显示进度徽标与异常提示。
- 子 issue blocked 时，恢复动作（retry / rerun / resume）在子 issue 上执行。

## 生命周期细则

- **close 父 issue**：要求全部子 issue 已到终态，否则拒绝——先逐个处理子 issue。不做隐式级联关闭。
- **reopen 父 issue**：回到 backlog，可以继续加子 issue、再次启动。
- **归档**：归档父 issue 时子 issue 一并归档；子 issue 不单独归档。
- **解除父子关系**：子 issue 变回普通 issue，父 issue 的汇总立即重算；全部子 issue 都解除后，父 issue 回到普通 issue（可以自己进 workflow）。
- 子 issue 被 reopen 时，已完成的父 issue 自动回到 in-progress。

## 与 Epic 的关系

- **子 issue 不能加入 Epic**，尝试 link 会被拒绝。Epic 的自动推进永远不会触碰子 issue。
- **父 issue 可以加入 Epic**：Epic 把它当普通 issue 对待——轮到它时启动它（即触发复合推进），它 done 时计入 Epic 进度。Epic 不感知复合结构，Epic 的行为不因本能力发生任何变化。

## 与 prerequisite 的关系

prerequisite 的规则不变，复合结构下的常见用法：

- **子 issue 之间**：表达拆分内部的顺序（先 server 后 web），复合推进会遵守。
- **外部 issue 依赖父 issue**：等需求整体完成再开始。
- **父 issue 依赖外部 issue**：复合推进的启动被 gate 住。

## 端到端验收

子 issue 各自 Integrate 后，跨仓库的联调验证不会自动发生。需要时，把"联调验证"本身建成最后一个子 issue，prerequisite 指向其它全部子 issue。多仓库变更的发布协同（同时上线）是 Non-goal，见 [仓库](repositories.md)。

## 实装差距

本篇是产品 spec，所述能力当前均未实装（父子关系、复合推进、状态汇总、Plan 背景上下文注入），由对应 issue 立项推进。历史决策沿革见 [`design/issue-breakdown.md`](../design/issue-breakdown.md)。
