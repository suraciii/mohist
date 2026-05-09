---
name: mohist
description: 执行 mohist CLI 操作。当用户要求创建、查看、启动、审批、关闭 issue，查看项目状态或日志，或任何涉及 "mo" 命令的操作时使用。触发词包括 "create issue"、"创建 issue"、"list issues"、"start issue"、"approve"、"reject"、"mo issue"、"mo status"、"查看 issue"、"issue 日志"。
---

# mohist CLI

mohist 命令前缀为 `mo`。操作前确认 server 在运行：`mo server status`

## Issue 命令

```
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
```

## 其他命令

```
mo status                          当前项目概览
mo project list / use <name>       项目管理
mo attach [-f]                     实时跟踪 agent 事件（交互式 REPL）
mo server start / stop / status    服务管理
mo server update                   重新构建并重启（源码模式）
```

## 常用模式

创建并启动：
```bash
mo issue create "Fix X" --body "描述" --label bug --priority P1
mo issue start <number>
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
