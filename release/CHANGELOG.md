# Release Changelog

发布说明。面向使用者，记录每个发布对外可见的变更（含命令路径迁移、不兼容变更、新增能力）。

## Unreleased — issue #383 CLI: repo 命令组统一

### 命令路径迁移（破坏性）

| 旧路径 / 旧参数 | 新路径 / 新参数 | 影响 |
|---|---|---|
| `mo project repo ...` | `mo repo ... --project <项目>` | 仓库管理统一到顶层 `mo repo`，项目作用域通过 `--project` / `--project-id` 表达。旧嵌套路径不再可用。 |
| `mo repo add <名> --default` | `mo repo add <名> --set-default` | “设为默认”flag 统一为 `--set-default`。旧 `--default` 不再可用。 |
| `mo repo remove <名>` | `mo repo delete <名>` | 删除动词正名为 `delete`；`remove` / `rm` 仍作为别名保留。 |

### 新增命令面

- `mo repo set-default <名> --project <项目>`：将仓库设为项目默认仓库。
- `mo repo update <名> ... --project <项目>`：与 list/add/set-default/delete 一起支持 `--project` 与 `--project-id`。

## Unreleased — issue #381 CLI: workflow 命令组完善

### 命令路径迁移（唯一破坏点）

| 旧路径 | 新路径 | 影响 |
|---|---|---|
| `mo workflow list` | `mo project workflow profile list` | 列出 WorkflowProfile。`mo workflow` 顶层不再承载 profile 语义，让位给 WorkflowRun（核心域聚合根）。脚本里把 `mo workflow list` 替换为 `mo project workflow profile list` 即可。flags（`--described`、`--project`/`--project-id`、`-o`）与降级 fallback 行为不变。 |

无其他命令路径变更；既有的 `mo project workflow template` / `mo project workflow config` 子组路径、flags、行为均不变。

### 新增命令面

**`mo workflow <control> <runId>`**（按 workflowRunId 直接寻址，无需 issue 号）：

- `mo workflow approve <runId>`
- `mo workflow reject <runId> --message <message>`
- `mo workflow retry <runId>`
- `mo workflow rerun <runId>`（`--from-stage <stage>` 走同一命令，flag 形式）
- `mo workflow resume <runId>`
- `mo workflow pause <runId>`（可恢复）
- `mo workflow stop <runId>`（终态）

**`mo workflow <read> <runId>`**（直接寻址 WorkflowRun）：

- `mo workflow show <runId>`（`-o table|json|yaml`，`-o yaml` 渲染模板定义）
- `mo workflow status <runId>`
- `mo workflow variables <runId>`（`--stage` / `--key`）
- `mo workflow events <runId>`（`--limit`）
- `mo workflow list-sessions <runId>`

### 配套文档

- `docs/cli-reference.md` 更新：新增 `mo workflow` 控制与读子命令速查；`mo project workflow profile list` 文档化与旧路径迁移提示。
- `design/cli.md`（新增）：记录 `mo` 命令面两条耐久原则——命名归属（顶层 `mo <noun>` 归核心域聚合根，子资源挂在父资源下）；输出格式 / 子资源 / 关联资源三类不可混用，输出格式绝不创造命令。
- `docs/agent-subscriptions.md` / `design/agent-subscriptions.md`：「`mo workflow` 命令套件」前置依赖标记为已交付（`mo workflow show <runId>` 含关联 issue，满足 Agent 自拉上下文需求）。

### 注意事项

- 单 session 子动作（show / transcript / compact / reset / followup）的 workflowRunId-direct 入口**不在本次发布**——继续走 `mo issue session ...`（按 issue 号寻址）。
- 任务注入（`mo workflow add-task`）按 Tier 2 裁定属于 state-changing 入口，但因服务端守卫未收敛，**延后**到后续 issue 落地（详见 `design/cli.md` 末尾）。
