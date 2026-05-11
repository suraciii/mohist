---
name: mohist
description: 执行 mohist CLI 操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 "mo" 命令的操作时使用。触发词包括 "create issue"、"创建 issue"、"list issues"、"start issue"、"approve"、"reject"、"mo issue"、"mo status"、"查看 issue"、"issue 日志"。
---

# mohist CLI

mohist 命令前缀为 \`mo\`。操作前确认 server 在运行：\`mo server status\`

## Issue 命令

\`\`\`
mo issue create <title> --body <text> --label <label> --priority <P0|P1|P2>
mo issue list [-s <stage>] [-l <label>] [-p <priority>] [--all] [--archived]
mo issue show <number>
mo issue start <number>
mo issue approve <number>
mo issue reject <number> -m <reason>
mo issue close <number>
mo issue reopen <number>
mo issue comment <number> <text>
mo issue logs <number> [-f]
mo issue diff <number>
mo issue update <number> --title <text> --body <text> --label <+add|-remove>
mo issue archive <number>
mo issue archive --all-completed
mo issue unarchive <number>
\`\`\`

## 其他命令

\`\`\`
mo status                          当前项目概览
mo project list / use <name>       项目管理
mo attach [-f]                     实时跟踪 agent 事件（交互式 REPL）
mo server start / stop / status    服务管理
mo server update                   重新构建并重启（源码模式）
\`\`\`

### Issue 创建规范

mohist 的 issue 是 AI agent 的工作入口。body 是**用户价值和验收标准的契约**——agent 拿到就能开工，不需要猜。

### 核心原则：PM 思维而非开发者思维

| 开发者思维 | PM 思维 |
|-----------|---------|
| 要改什么代码 | 用户遇到了什么问题 |
| "添加 diff 组件" | "审批时看不到变更，没法做决定" |
| 以模块为中心 | 以用户旅程为中心 |
| 验收标准隐含 | 验收标准明确、可验证 |
| 跳过为什么，直奔怎么做 | 先说清为什么，怎么做留给 Plan |

### 五步写 issue

**1. 用户故事** — 谁、要什么、为什么
> 作为 [角色]，我想 [做什么]，以便 [达到什么目的]。

**2. 当前体验** — 用场景说话，让读的人感同身受
> 用户收到审批通知 → 打开详情页 → 只看到 title 和 body → **不知道改了什么** → 切 terminal。

**3. 期望体验** — 用户感受到什么，不涉及实现细节
> 用户在同一个页面完成"理解变更 → 审批决定"。

**4. 验收标准** — 可验证、用户能感知
> - [ ] 详情页在 Check 阶段展示代码变更摘要
> - [ ] 摘要含文件列表和 diff 视图
> - [ ] 无变更时展示"暂无可审变更"而非空白

**5. 优先级依据** — 为什么要现在做
> 审批是核心流程，影响 100% 审批场景。P1。

### Body 模板

\`\`\`markdown
## 用户故事
作为 [角色]，我想 [做什么]，以便 [达到什么目的]。

## 当前体验
[用户现在遇到了什么困境，用场景描述]

## 期望体验
[做完后用户感受到什么，不涉及实现方案]

## 探索发现（可选）
[探索/讨论中发现的与实现相关的关键信息]

### 约束与机会
- [已存在的数据/模块/接口，可直接复用]
- [已知的技术约束，如"diff 数据在 Build 阶段已生成并存库"]

### 设计决策
- [讨论中达成共识的方向，如"diff 展示为只读摘要，代码级 review 另做"]

## 验收标准
- [ ] [可验证的条件，用户能感知到的变化]

## 优先级依据
[为什么现在做？不做会怎样？做了带来什么？]
\`\`\`

**关于"探索发现"字段**：Plan agent 拿到两种上下文——用户价值定义 + 探索中已明确的约束和方向。不等于限制方案，只等于"别重复探索已知的东西"。区分：
- ❌ 预设方案：`用 WebSocket 推送 diff 数据`
- ✅ 传递发现：`diff 数据在 Build 阶段已生成并存库`
- ✅ 传递决策：`diff 为只读摘要，不在此处做代码级 review`

### 完整示例

\`\`\`markdown
## 用户故事
作为审批者，我想在 issue 详情页看到代码变更摘要，以便做出审批决定而不必离开页面。

## 当前体验
用户收到审批通知 → 打开 issue 详情页 → 看到 title 和 body → **不知道改了什么** → 切到 terminal 运行 `mo issue diff` → 回到 Web UI 点 approve/reject。Web UI 和 CLI 之间来回切换，流程断裂。

## 期望体验
用户在 issue 详情页的 review 区域直接看到变更的文件列表、关键代码片段的 diff 视图、变更意图的简要说明。在同一个页面完成"理解变更 → 审批决定"。

## 探索发现

### 约束与机会
- diff 数据在 Build 阶段已生成并存库（issue_diffs 表），Check 阶段可直接读取
- 前端已有 SSE event stream，可复用推送 diff 就绪事件
- 详情页使用 TanStack Query 管理数据，新增查询即可

### 设计决策
- diff 展示为只读摘要（文件 + diff），代码级逐行 review 留到专门的 review 页
- 变更摘要与 approve/reject 操作同页展示，不拆分到不同 tab

## 验收标准
- [ ] issue 详情页在 Check 阶段展示代码变更摘要
- [ ] 摘要包含文件列表和 diff 视图
- [ ] 摘要页面提供 approve/reject 操作
- [ ] 无变更时展示"暂无可审变更"，而非空白区域

## 优先级依据
审批是 mohist 核心流程，当前每次审批都需要 CLI ↔ Web UI 切换。影响 100% 的审批场景。P1 — 应在下个迭代解决。
\`\`\`

### Label

| Label | 用途 |
|-------|------|
| bug | 功能不符合预期或错误行为 |
| feature | 新能力/新功能 |
| improvement | 已有功能的增强或体验优化 |
| design | 需要先设计（探索探索后进入） |
| docs | 文档变更 |
| refactor | 纯技术重构，用户不可见 |

### Priority

| Priority | 含义 | 典型场景 |
|----------|------|----------|
| P0 | 阻塞性 | 核心流程中断、数据丢失、无法使用 |
| P1 | 重要 | 影响主要用户体验，需近期解决 |
| P2 | 优化 | 锦上添花，有了更好 |

---

## 常用模式

创建并启动：
\`\`\`bash
mo issue create "Fix X" --body "描述" --label bug --priority P1
mo issue start <number>
\`\`\`

监控进度：
\`\`\`bash
mo issue show <number>     # 查看状态
mo issue logs <number> -f  # 实时日志
mo attach -f               # 全局事件流
\`\`\`

审批或拒绝：
\`\`\`bash
mo issue approve <number>
mo issue reject <number> -m "原因"
\`\`\`

审查变更：
\`\`\`bash
mo issue diff <number>     # 查看代码差异
\`\`\`

## 注意

- \`mo issue start\` 会启动 agent 自动处理，需要 server 运行
- \`mo attach\` 是交互式 REPL，用于审批和回答 agent 问题
- \`logs -f\` 跟踪实时输出，不带 \`-f\` 看历史
- \`diff\` 在 issue 进入 Build 阶段后才有内容