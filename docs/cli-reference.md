# CLI 参考

`mo` 是 Mohist 的命令行入口。和 Web UI 功能等价，适合脚本、自动化、远程 SSH 场景。首次安装上手走 [快速上手](getting-started.md)；本文是完整命令参考。

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
mo project repo ...                # 仓库管理（见 Repository 管理）
mo project workflow template ...   # Workflow 模板管理
mo project workflow config ...     # Workflow 配置管理
```

### Workflow 模板管理

```bash
mo project workflow template list                      # 列出所有 workflow 模板
mo project workflow template create --yaml <yaml|@file> # 创建 workflow 模板
mo project workflow template show <template-id>         # 查看 workflow 模板详情
mo project workflow template update <template-id> --yaml <yaml|@file> # 更新 workflow 模板
mo project workflow template delete <template-id>       # 删除 workflow 模板
```

`--yaml` 接受 inline YAML 或 `@file`（从文件读取）。所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。

### Workflow 配置管理

```bash
mo project workflow config get                               # 查看完整配置（默认模板、变量、提示词覆盖）
mo project workflow config set [flags]                       # 复合写入（默认模板/变量/提示词）
mo project workflow config clear [flags]                     # 复合清除（默认模板/变量/提示词）
mo project workflow config preview <key>                     # 预览渲染后的提示词
```

`config set` 支持的 flags：

| Flag | 含义 |
|------|------|
| `--default-template <id>` | 设置默认 workflow 模板（PUT /default-template） |
| `--var <k=v>` | 增量设置顶层变量（可重复，PATCH /variables） |
| `--stage-var <stage.k=v>` | 增量设置阶段变量（可重复，PATCH /variables） |
| `--vars-file <file>` | 全量替换所有变量（PUT /variables，JSON 文件，与 `--var`/`--stage-var` 互斥） |
| `--prompt <key=body\|@file>` | 设置提示词覆盖（可重复，PUT /prompts/{key}，`@file` 从文件读） |

`config clear` 支持的 flags：

| Flag | 含义 |
|------|------|
| `--default-template` | 清除默认模板（DELETE /default-template） |
| `--var <k>` | 清除指定变量（可重复，PATCH /variables 设 null） |
| `--prompt <key>` | 清除指定提示词覆盖（可重复，DELETE /prompts/{key}） |

所有子命令支持 `-o table|json` 和 `--project`/`--project-id`。

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
mo issue archive --all-completed [options]
mo issue unarchive <number>
mo issue comment <number> <body>
mo issue prerequisite-add <number> <prereq>
mo issue prerequisite-remove <number> <prereq>
mo issue logs <number>
mo issue events <number>
mo issue diff <number>
mo issue commits <number>
mo issue sessions <number>
mo issue session show <number> <name>
mo issue session transcript <number> <name>
mo issue session compact <number> <name>
mo issue session reset <number> <name>
mo issue session followup <number> <name> [options]
mo issue workflow [subcommand]
```

常用选项：

| 选项 | 适用命令 | 含义 |
|---|---|---|
| `--body <text>` | create, update | inline body |
| `--body-file <path>` | create, update | body 从文件读（推荐长 body） |
| `--body-stdin` | create, update | body 从 stdin 读 |
| `--priority p0-p4` | create, update | 优先级 |
| `--label <key=value>` | create, update | 标签：`key=value` 设置、`-key` 移除，可多次 |
| `--model <id>` | create | 指定 AI 模型 |
| `--workflow-profile <id>` | create | 指定 workflow profile |
| `--all-completed` | archive | 批量归档所有已完成且未归档的 issue |
| `--project <name>` | 所有 | 指定 project |
| `--archived` | list | 看归档 |
| `--all` | list | 看全部（含归档） |

Session 子命令：

```bash
mo issue session show <number> <name>            # 查看 session 元数据
mo issue session transcript <number> <name>      # 查看 session 对话摘要
mo issue session compact <number> <name>         # 压缩 session 上下文
mo issue session reset <number> <name>           # 重置 session 上下文
mo issue session followup <number> <name> --text <text>  # 向运行中的 session 推送后续指令
```

`followup` 的文本源选项（三者选一）：

| 选项 | 含义 |
|------|------|
| `--text <text>` | inline 后续文本 |
| `--text-file <path>` | 从 UTF-8 文件读取 |
| `--text-stdin` | 从标准输入读取 |

## Epic 管理

完整命令在 [用 Epic 规划](epics.md)。这里给速查表：

```bash
mo epic create <title> [options]
mo epic list [options]
mo epic show <epic-id-or-number>
mo epic update <epic-id-or-number> [options]
mo epic link <epic-id-or-number> <issue-id-or-number>
mo epic unlink <epic-id-or-number> <issue-id>
mo epic start <epic-id-or-number>
mo epic pause <epic-id-or-number>
mo epic resume <epic-id-or-number>
mo epic done <epic-id-or-number>
mo epic close <epic-id-or-number>
```

`start`、`pause`、`resume` 是幂等的 — 详情和生命周期语义见 [Epic 生命周期](epics.md#epic-的生命周期)。

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
mo runner status             # 在线 runner 摘要（id、心跳、idle/busy）
mo runner service-status     # runner 托管服务状态（systemd / Scheduled Task）
mo runner list               # 当前 project 的 runner 详情列表
```

详见 [Runner 指南](runner.md)。

## 只读诊断

```bash
mo system info               # 服务端系统诊断（identity/source/install/update/services/paths）
mo opencode models           # 当前 project 可用 coder 模型 ID，每行一个
```

以上命令支持 `-o table|json`。

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
# 安装为受管理服务（Linux: systemd user service；Windows: 计划任务）
mo install server            # 写 unit、enable、启动、enable-linger
mo install runner

# 从源码更新（重建并以受管理方式重启）
mo update                    # 更新全部（CLI + server + runner）
mo update server             # 只更新 server
mo update runner             # 只更新 runner
mo update cli                # 只更新 mo CLI
```

首次安装 `mo` 本身：仓库内 `bash scripts/install-mo.sh`。详见 [Self-host 部署](self-host.md)。

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

## 退出码

| Code | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 一般错误（参数错、API 返回错误等） |
| 2 | 命令解析失败 |

---

对应源码：`packages/cli/`。
