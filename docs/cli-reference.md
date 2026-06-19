# CLI 参考

`mo` 是 Mohist 的命令行入口。和 Web UI 功能等价，适合脚本、自动化、远程 SSH 场景。

## 全局

```bash
mo --version
mo --help
mo status              # 当前 project 状态、agent 状态、runner 状态
mo logs                # 最近日志
mo use <project>       # 切换 active project
```

所有命令都接受 `--project <name>` 和 `--project-id <id>` 在不切换 active project 的情况下指定目标 project。

## 输出格式

大多数 list/show 命令支持 `--output` 选项：

```bash
mo issue list --output json       # JSON（脚本友好）
mo issue list --output table      # 表格（默认，人读）
mo issue list --output compact    # 紧凑表格
```

JSON 输出适合 piping 到 `jq`：

```bash
mo issue list --output json | jq '.[] | select(.status=="backlog") | .number'
```

## 项目管理

```bash
mo project create <name> --path <repo-path>
mo project list
mo project show <name-or-id>
mo project use <name>              # 等同于 mo use
mo project delete <name>
```

## Issue 管理

完整命令在 [Issue 管理](issues.md)。这里给速查表：

```bash
mo issue create <title> [options]
mo issue list [options]
mo issue show <number>
mo issue update <number> [options]
mo issue start <number>
mo issue approve <number>
mo issue reject <number>
mo issue close <number>
mo issue reopen <number>
mo issue retry <number>
mo issue rerun <number>
mo issue force-stop <number>
mo issue resume <number>
mo issue stop <number>
mo issue rebase <number>
mo issue archive <number>
mo issue unarchive <number>
mo issue comment <number> <body>
mo issue prerequisite-add <number> <prereq>
mo issue prerequisite-remove <number> <prereq>
mo issue logs <number>
mo issue events <number>
mo issue diff <number>
mo issue commits <number>
mo issue sessions <number>
mo issue workflow [subcommand]
```

常用选项：

| 选项 | 适用命令 | 含义 |
|---|---|---|
| `--body <text>` | create, update | inline body |
| `--body-file <path>` | create, update | body 从文件读（推荐长 body） |
| `--body-stdin` | create, update | body 从 stdin 读 |
| `--priority p0-p4` | create, update | 优先级 |
| `--label <name>` | create, update | 标签（可多次） |
| `--model <id>` | create | 指定 AI 模型 |
| `--workflow-profile <id>` | create | 指定 workflow profile |
| `--project <name>` | 所有 | 指定 project |
| `--archived` | list | 看归档 |
| `--all` | list | 看全部（含归档） |

## Server 管理

```bash
mo server start              # 前台启动 server
mo server stop               # 停止 server（如果 daemon）
mo server status             # 检查 server 状态
```

详见 [Self-host 部署](self-host.md)。

## Runner 管理

```bash
mo runner start              # 前台启动 runner
mo runner status             # runner 状态
```

详见 [Runner 指南](runner.md)。

## Repository 管理

```bash
mo repo list                 # 当前 project 的仓库列表
mo repo add <name> --git-url <url> --base-branch <branch>
mo repo remove <name>
mo repo set-default <name>
```

一个 project 可以关联多个 repo（如 monorepo 多模块）。Issue 时指定 repo。

## 配置

```bash
mo config get <key>
mo config set <key> <value>
mo config list
```

详见 `mo config --help`。

## Skills

```bash
mo skills list               # 列出可分发 skill
mo skills install            # 安装/更新 skill 到外部 agent
mo skills get <name>         # 获取 skill 的完整内容
```

详见 [Skill 机制](skills.md)。

## 安装与更新

```bash
mo install                   # 从源码构建并安装 Mohist 组件
mo update                    # 更新到最新版本
```

## 典型工作流脚本

### "睡前丢 10 个 backlog 进去"

```bash
for n in 42 43 44 45 46 47 48 49 50 51; do
  mo issue start $n
done
```

### "把所有 blocked 的 issue 重试一遍"

```bash
mo issue list --output json | jq '.[] | select(.health=="blocked") | .number' | \
  while read n; do mo issue retry $n; done
```

### "看今天 delivered 了哪些"

```bash
mo issue list --output json | \
  jq '.[] | select(.status=="done" and .updatedAt >= "'$(date -u +%Y-%m-%dT00:00:00Z)'")'
```

## 命令找不到？

- 看完整命令树：`mo --help`
- 看子命令选项：`mo <command> --help`
- 当前 CLI 不支持的：Epic 管理（用 API，见 [用 Epic 规划](epics.md)）

## 退出码

| Code | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 一般错误（参数错、API 返回错误等） |
| 2 | 命令解析失败 |

---

对应源码：`packages/cli/`。
