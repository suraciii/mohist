---
name: mohist
description: 执行 mohist CLI 操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 "mo" 命令的操作时使用。触发词包括 "create issue"、"创建 issue"、"list issues"、"start issue"、"approve"、"reject"、"mo issue"、"mo status"、"查看 issue"、"issue 日志"。
---

# mohist CLI

mohist 命令前缀为 `mo`。操作前确认 server 在运行：`mo server status`

## Issue 命令

```
mo issue create <title> [--body <text>|@file.md|-] [--body-file <path>] [--label <label>] [--priority <P0|P1|P2>]
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
mo issue update <number> [--title <text>] [--body <text>|@file.md|-] [--label <+add|-remove>]
mo issue archive <number>
mo issue archive --all-completed
mo issue unarchive <number>
```

## 其他命令

```
mo status                          当前项目概览
mo project list / use <name>       项目管理
mo attach [-f]                     实时跟踪 agent 事件（交互式 REPL）
mo server start / stop / status    服务管理
mo server update                   重新构建并重启（源码模式）
mo instructions [<label>]          查看 issue 模板说明
```

### Issue 创建流程

在创建或修改 issue 之前，运行以下命令获取对应 label 的模板：

```bash
mo instructions <label>
```

支持的 label 包括：`bug`、`feature`、`improvement`、`refactor`、`design`、`docs`、`ui-feature`、`ui-improvement`。

**Label 对应关系：**

| Label | 模板说明 |
|-------|---------|
| bug / feature / improvement | 产品形态模板 |
| refactor | 技术重构模板 |
| design | 设计探索模板 |
| docs | 文档变更模板 |
| ui-feature / ui-improvement | UI 原型模板（含 ASCII 原型图要求）|

获取模板后，按照模板结构填充内容。完整模板通过 `mo skills get mohist --full` 动态提供，避免本地副本过期。

高质量 issue body 是 Plan 阶段的输入，不是完整 PRD、探索记录或技术设计文档。默认结构：

```markdown
## Problem
[用户可见的问题]

## User Goal
[可选。压缩后的用户目标；不要写长篇模板化用户故事]

## Product Shape
[目标产品形态和设计约束；先说明用户最终看到/使用什么]

## Key Domain Model
[只保留理解需求必要的关键概念和不变量]

## Acceptance Criteria
- [ ] [可验证的产品行为]

## Non-Goals
- [明确不做的范围]
```

不要在 issue body 中写文件、函数、数据库表或逐步实现任务；这些属于 Plan 阶段。探索过程也不要原样粘贴，只沉淀结论、边界和验收。

### Label

| Label | 用途 |
|-------|------|
| bug | 功能不符合预期或错误行为 |
| feature | 新能力/新功能 |
| improvement | 已有功能的增强或体验优化 |
| design | 需要先设计（探索后进入） |
| docs | 文档变更 |
| refactor | 纯技术重构，用户不可见 |
| ui-feature | UI 新功能（含 ASCII 原型要求） |
| ui-improvement | UI 改进（含 ASCII 原型要求） |

### Priority

| Priority | 含义 | 典型场景 |
|----------|------|----------|
| P0 | 阻塞性 | 核心流程中断、数据丢失、无法使用 |
| P1 | 重要 | 影响主要用户体验，需近期解决 |
| P2 | 优化 | 锦上添花，有了更好 |

---

## 常用模式

创建并启动：
```bash
mo issue create "Fix X" --body "描述" --label bug --priority P1
mo issue start <number>
```

长 Markdown 内容使用文件或管道：
```bash
mo issue create "Fix X" --body @issue-body.md
mo issue create "Fix X" --body - < issue-body.md
cat issue-body.md | mo issue create "Fix X" --body -
```

heredoc 作为兼容性备选：
```bash
mo issue create "Fix X" --body "$(cat <<'EOF'
## Problem

- code block
- special chars: $()|'"` 都可以
EOF
)"
```

监控进度：
```bash
mo issue show <number>     # 查看状态
mo issue logs <number> -f  # 实时日志
mo attach -f               # 全局事件流
```

审批或拒绝：
```bash
mo issue approve <number>
mo issue reject <number> -m "原因"
```

审查变更：
```bash
mo issue diff <number>     # 查看代码差异
```

## 注意

- `mo issue start` 会启动 agent 自动处理，需要 server 运行
- `mo attach` 是交互式 REPL，用于审批和回答 agent 问题
- `logs -f` 跟踪实时输出，不带 `-f` 看历史
- `diff` 在 issue 进入 Build 阶段后才有内容
- 创建 issue 前务必运行 `mo instructions <label>` 获取对应模板
