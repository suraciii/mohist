# Agent 监管（Supervisor）

Mohist 是一条生产线：issue 进入 workflow 后，阶段之间要审批，失败要处理。这些事原本都等你动手。Agent 监管把这些一线判断委托给一个 Mohist Agent：它替你审批、替你分析和修复失败，**只有它处理不了的时候才轮到你**。

## 心智模型

- **Agent 当一线操作员，你当例外处理者**。审批通过或打回、失败修复后重试，都由 Agent 用和你相同的命令完成，不需要你在场。
- **通知是感知，不是任务**。到达审批点、workflow 失败时你仍会收到通知——那是「生产线上发生了一件事」，收到可以划掉。真正的行动召唤只有两种：Agent 的停手 comment，和生产线停住不动。
- **Agent 自身失败也会通知你**。如果它没能起跑或中途崩了，你会收到「Agent 响应失败」通知——不会出现你以为它在处理、其实没人管的静默死角。
- **issue 的 comment 区是交接面**。Agent 每次干预都写一条以 `[supervisor]` 开头的 comment：判断了什么、做了什么、为什么。你任何时候打开 issue，都能从 comment 直接接手。

## 快速开始

两条路，按信任程度选：

```bash
# 只让一个 issue 进入 autopilot
mo issue watch add 42 --agent supervisor

# 或者全项目监管（安装 supervisor 预设：Agent + 项目级路由规则）
mo agent install supervisor
```

Agent 需要在 issue 工作区里能发现 `mohist` skill（mo 命令面指南）。缺失时执行：

```bash
mo skill install --path <你的仓库路径>
```

## Issue 关注：每个 issue 的 autopilot 开关

「关注」是 issue 级的开关：被关注的 issue 到达审批门、终态失败时，Agent 自动响应——和项目级监管同一套行为，只是范围缩到这一个 issue。

`mo issue view 42` 直接显示谁在管这个 issue：

```text
关注:   supervisor        # 这个 issue 的 autopilot 是它
静音:   —                 # 被明确要求「别管这个 issue」的 Agent
```

反悔不需要区分关注是怎么来的，同一个命令覆盖两种情形：

```bash
mo issue watch remove 42 --agent supervisor
```

- 关注来自 `watch add` → 删除关注，这个 issue 回到你手里；
- 关注来自项目级监管规则 → 记一条**静音**：全局规则不动，其它 issue 不受影响，只有 #42 由你自己开。`watch add` 可以解除静音。

在 comment 里 `@supervisor 监督并推进这个issue` 也行——Agent 会自己执行 `mo issue watch add` 把持续关注兑现掉。

## 它怎么处理审批

阶段到达审批点时，Agent 阅读 issue 目标与本阶段产物，自己做出审批决定：产物服务了 issue 目标就 approve；有必须修改的问题就 reject 并写清改什么——reject 会触发反馈任务自动返工，之后它会再次收到审批请求，继续审。

如果它判断这是产品取向的取舍、或它掌握的信息不够，它不替你拍板：让审批保持等待，写 comment 说明疑点。审批停在这里就是你的出场信号。

## 它怎么处理失败

workflow 终态失败（系统的自动恢复已耗尽）时，Agent 先读自己之前的干预记录，再分析根因、决定怎么处理：有把握修好就在工作区修复并重试；判断继续干预不会有新进展——根因不明、修复超出范围、或同样的失败反复出现——就不重试，写 comment 说清根因结论、试过什么、需要你决策什么，然后停手。run 保持失败状态等你接手。

何时停手是 Agent 自己的判断，不是固定次数：它从 comment 记录里看到自己在一个问题上反复没有进展，就会把局面交给你，而不是靠重试碰运气。你仍能从通知里看到每一次失败，发现它纠缠不休时随时可以介入。

## 委托边界

「做得对不对」的判断归 Agent，「要不要做」的决定归你：

- **归 Agent**：审批产物、分析失败、修代码、重试重跑、打回并写返工意见。
- **归你**：放弃 issue（close）、停掉整条 run（stop）、改变 issue 的目标。这些是不可逆或改变方向的终局决定——Agent 遇到了只会写 comment 提议，由你拍板。

## 你什么时候出场，怎么接手

需要你出场的只有三种情况：Agent 停手（comment 等你决策）、Agent 响应失败（通知你）、生产线停滞（通知 + 停住不动）。你按正常方式接手——审批、拒绝、重试，或者先读 comment 了解上下文。Agent 不会锁死任何状态，你随时可以用正常命令越过它；`watch remove` 可以彻底把它请出这个 issue。

## 定制

- 改审批口味（更严/更松）：`mo agent edit supervisor` 调整身份指令；
- 把最终交付门（integrate）留给自己：`mo routing rule edit supervisor-approval`，匹配改为 `event.type == "com.mohist.workflow.stage.approval-requested" && event.stage != "integrate"`；
- 个别 issue 例外：`mo issue watch remove <issue> --agent supervisor` 静音；
- 加自己的规则：正常 `mo routing rule create`，用 `mo routing rule move` 调整次序——监管规则在表尾兜底，不会抢你的规则。

### 拆成专职 Agent

默认一个 `supervisor` 同时接审批和失败两件事：两类反应的差异已经写在各自的规则提示词里，身份（owner 的代理人）是共享的，一个 issue 的审批史和失败史也因此留在同一条记忆里。需要时再拆——典型理由是审批和修复要用不同的模型、想独立调两边的口味或并发限制。

拆法不需要新机制：再 `mo agent create` 一个 Agent，用 `mo routing rule edit` 把其中一条规则的响应 Agent 指向它。两个注意点：

- 给每个 Agent 自己的 comment 标记（如 `[approver]`、`[fixer]`），否则各自数不清自己干预过几次；
- 每个 Agent 的身份指令里都要写明：行动前读 issue 下**所有**监管 comment，不只读自己写的。审批反复打回、修复反复糊补丁的往返循环，拆开后任何一方只看自己的记录都看不见。

## 实装差距

`mo issue watch` 关注与静音、「Agent 响应失败」通知、审批决议的操作者记录均尚未实装，实施 issue 待创建。`mo agent install supervisor` 已实装；当前也可以手工达到近似效果：用 `mo agent create` 创建 Agent，再用 `mo routing rule create` 建立审批与失败两条规则，提示词写法见 [Agent 事件路由](event-routing.md) 的监管场景。路由表、Agent 启动、审批/失败事件和通知这些底座均已实装。
