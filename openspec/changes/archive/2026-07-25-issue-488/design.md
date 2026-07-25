## Context

Agent 监管（supervisor）把生产线的审批与终态失败处理委托给一个 Mohist Agent。今天要达成它必须手工拼装 `mo agent create` + 两条 `mo routing rule create`，而匹配表达式、身份指令、响应提示词才是这套模式的真正产品内容——写错就退化成规则引擎。本 issue 用一条命令 `mo agent install supervisor` 把这套权威内容装好。

底座均已实装，本 change 只动 CLI：

- Agent 创建：`POST /api/projects/{projectId}/agents`（`AgentDefinitionRoutes.cs:19`），`name`/`instructions` 必填，重名返回 409 `AGENT_NAME_CONFLICT`（`AgentDefinitionRoutes.cs:44-47`）。
- 规则创建：`POST /api/projects/{projectId}/routing/rules`（`RoutingRulesRoutes.cs:22`），不传 `before`/`after` 即追加到表尾（`CreateAsync(rule, null, null, ...)`），重名 409 `routing_rule_name_conflict`（`RoutingRulesRoutes.cs:97`），非法 match 400 `invalid_match_expression`。
- CLI 资源发布范式：`skill-data/` 作为 `Content` 复制到输出目录并打进工具包（`Mohist.Cli.csproj`），运行期由 `SkillAssetRootResolver` 解析（`AppContext.BaseDirectory/skill-data` 或 `~/.mohist/cli/skill-data`），见 `SkillAssetRootResolver.cs:43,97`。
- 通知配置：`Mohist:Notifications:Hermes:EnabledTypes`（`HermesNotificationOptions.cs:11,17`），落在 `~/.mohist/config.jsonc`，CLI 端入口 `NotifyCommands.DefaultConfigPath()` + 可测 `ConfigPathOverride`（`MohistCliCommands.Notify.cs:53-58`）。默认三项 `approval_requested`/`workflow_failed`/`issue_completed`（`MohistCliCommands.Notify.cs:32-34`）。
- 默认仓库：项目仓库带 `IsDefault` 标记，服务端 `IssueRepositoryResolver` 取 `Repositories.FirstOrDefault(r => r.IsDefault)`（`IssueRepositoryResolver.cs:96`）。
- skill stub 落点：`mo skills install` 写到 `<repo>/.agents/skills/mohist/SKILL.md`（默认）或 `.claude/skills/mohist/`（`SkillInstallService.cs:73-81`）。

规格见 `specs/agent-preset-install/spec.md`，动机见 `proposal.md`。

## Goals / Non-Goals

**Goals:**
- 新增 `mo agent install <preset>`，当前实现 `supervisor` 一种预设。
- 用现有创建 API 幂等地装出 Agent `supervisor` 与规则 `supervisor-approval`/`supervisor-failure`，规则落在路由表尾。
- 预设文本（身份指令 + 两条响应提示词）作为随 CLI 发布的权威资源原样写入。
- 只检查不修复的前置提示：默认仓库的 `mohist` skill stub、owner 默认通知是否保留。

**Non-Goals:**
- 不改 server 端（无新 API、无数据迁移）。
- 不引入 `escalate` 命令或新事件类型；升级靠通知 + `[supervisor]` comment 纪律（见 `design/agent-supervision.md`）。
- 不追踪漂移：装出的产物与预设脱钩，用户可自由 `mo agent edit`/`mo routing rule edit`，`install` 不回写、不"升级"已存在的指令。
- 不含 `mo issue watch`（#489 已完成）、"Agent 响应失败"通知、审批 `--author` 落库（独立 issue）。

## Decisions

### D1：安装是纯 CLI 编排，复用既有创建 API

`install` 解析项目后，依次调用现有 `POST .../agents` 与 `POST .../routing/rules`，不做任何 server 改动。预设只是"精心写好的文本 + 固定匹配表达式"，编排逻辑放 CLI 最薄。

- 备选：服务端加一个 `POST .../agent-presets/supervisor/install` 端点做原子安装。
- 否决理由：底座已把 Agent/规则暴露为一流资源，服务端再开一条特例路径只会增加耦合与测试面，且把产品文本搬进 server。CLI 编排足够——单用户交互、可观察的逐步输出、失败可中途诊断。

### D2：预设资源走 `skill-data` 同款 content-file 发布

新增 `presets/` 目录，结构镜像 `skill-data/`：

```
presets/
  manifest.json                         # catalog: name → 描述 + 文件引用
  supervisor/
    instructions.md                     # Agent 身份指令
    approval.md                         # supervisor-approval 的 responsePrompt
    failure.md                          # supervisor-failure 的 responsePrompt
```

`Mohist.Cli.csproj` 增一条 `<Content Include="presets\**\*" CopyToOutputDirectory="PreserveNewest" Pack="true" PackagePath="presets/..." />`，与现有 `skill-data` 条目并列。运行期用既有的 `SkillAssetRootResolver` 思路解析根目录（`AppContext.BaseDirectory/presets` 兜底，`MOHIST_SKILLS_DIR` 同源时不耦合——预设独立解析）。`manifest.json` 是预设名目录，未知名拒绝时直接列出其中的 name。

- 备选 A：`<EmbeddedResource>` 编译进程序集，`GetManifestResourceStream` 读取。
- 否决理由：与既有 `skill-data` 约定不一致，且打包后不可被 `mo skills sync` 式本地编辑巡检；content-file 保持仓库内可见、可 diff、可被测试用 `FakeFileSystem` 直接喂入。
- 备选 B：复用 `skill-data/` 目录放预设。
- 否决理由：skill 是 Agent 发现 `mo` 命令面的运行期产物，预设是安装期编排输入，语义不同；分目录避免 `SkillAssetService` 的可见性/清单逻辑被污染。

文本是权威源：`design/agent-supervision.md` 的「预设文本」小节为说明性副本，实装以 `presets/supervisor/*.md` 文件内容为准。`{{event.*}}` 占位符原样存入（运行期由路由派发替换），CLI 不渲染。

### D3：幂等靠「先列后建」，冲突作安全网

每步先取列表判断同名是否存在，存在则跳过并报 `exists, skipped`；不存在才 POST 创建。

- Agent：`GET .../agents?all=true`，按 `name == "supervisor"` 判定（复用 `AgentCommands.ResolveAgentAsync` 的名匹配，`MohistCliCommands.Agent.cs:819-828`）。
- 规则：`GET .../routing/rules`，按 `name` 判定。
- 安全网：若列表与创建之间发生并发安装导致 409（`AGENT_NAME_CONFLICT` / `routing_rule_name_conflict`），捕获并按"已存在，跳过"处理，不报错。

- 备选：直接 POST、靠 409 判定跳过。
- 否决为主路径：把正常情况（已装过）走错误通道会让输出全是 conflict 噪音，且无法干净区分"我装的"与"用户同名先在的"。先列后建让 `created`/`skipped` 语义清晰；并发是边角，靠 409 兜底。

绝不覆盖：已存在的 Agent/规则一律跳过、不 PATCH。用户对已装预设的编辑由此保留（与 Non-Goal「不追踪漂移」一致）。

### D4：规则表尾追加，顺序固定

两条规则都不带 `before`/`after` 创建（API 默认表尾追加，`RoutingRulesRoutes.cs:34`），顺序 `supervisor-approval` → `supervisor-failure`。两条均不设 `Continue`（独占响应）。用户既有规则天然排在其上、优先命中，无需 `move`。

- 备选：用 `--after` 显式链接两条规则位置。
- 否决理由：表尾是默认且正是兜底位置（design 文档明确「表尾是兜底」）；显式链接增加编排状态、且当用户表为空时无锚点可用。

### D5：前置检查只读、不阻断、给具体补救

两项检查在任何一项失败时仍完成安装，仅在输出末尾追加 `warning:` 行。

1. **mohist skill stub**：解析项目 → 取默认仓库（`IsDefault`）→ 检查本地 `<workspacePath>/.agents/skills/mohist` 是否存在（`IFileSystem.DirectoryExists`）。缺失 → `warning: ... run \`mo skills install --path <repo>\``。
2. **默认通知**：读 `NotifyCommands.DefaultConfigPath()` 指向的 `~/.mohist/config.jsonc`（测试用 `ConfigPathOverride` + `FakeFileSystem`），解析 `Mohist:Notifications:Hermes:EnabledTypes`，若 `approval_requested`/`workflow_failed`/`issue_completed` 任一缺失 → `warning: 通知已关闭时 owner 只能主动查看 ...`。

- 备选：skill stub 检查同时覆盖 `.claude/skills/mohist` 变体。
- 取舍：design 文档只点名 `.agents/skills/mohist`，本期只查该路径；claude 变体留作后续微调（见 Open Questions）。

## Risks / Trade-offs

- [配置文件与运行中 server 的实际配置可能不一致] → 通知检查读的是规范路径 `~/.mohist/config.jsonc`，server 可能被环境变量覆盖或跑在另一台机。缓解：检查是非阻断 warning，措辞定为"best-effort，请核对 server 实际配置"；准确性提升需服务端暴露生效配置 API（见 Open Questions）。
- [skill stub 检查只能看本地文件系统] → CLI 宿主 ≠ runner/server 宿主时看不到工作区。缓解：非阻断 warning；检查针对默认仓库 workspace path，纯属建议性。
- [权威文本在两处（仓库 `presets/` 与 `design/agent-supervision.md` 副本）] → 漂移风险。缓解：以 `presets/supervisor/*.md` 为唯一权威源，design 文档副本标注"说明性"。修改走仓库文件 + 对应测试。
- [无漂移追踪] → 用户编辑后再 `install` 不会"升级"提示词。缓解：这是 design 文档的有意决定（产物与预设脱钩）；在命令输出与 docs 里写明。
- [并发安装竞态] → 两次 `install` 同时跑都尝试创建。缓解：server 名唯一约束（409）兜底，捕获即视为跳过。
- [默认仓库缺失或无 workspace path] → skill stub 检查无法定位。缓解：跳过该项检查并附说明行，不阻断安装。

## Migration Plan

纯 CLI 增量，server 与数据无变更：

1. 合并 CLI 改动（`presets/` 资源 + `agent install` 子命令 + 测试），发版工具包。
2. 用户按需 `mo agent install supervisor` 启用；既有手工搭的 supervisor 配置不受影响（同名即跳过）。
3. 回滚：CLI 侧回退版本即可；已装出的 Agent/规则是普通资源，用 `mo agent archive supervisor`、`mo routing rule archive supervisor-approval|supervisor-failure` 清理。无需 feature flag。

文档同步：落地后把 `docs/agent-supervision.md`「实装差距」与 `design/agent-supervision.md`「Status」里关于 `mo agent install supervisor` 的"未实装"标注改为已实装。

## Open Questions

- skill stub 前置检查是否要同时覆盖 `.claude/skills/mohist` 变体？本期默认只查 `.agents`（design 文档点名），claude 变体作后续微调。
- 通知准确性：读 `~/.mohist/config.jsonc` 够不够，还是要等服务端暴露"生效通知配置"只读 API 再做精准检查？当前 best-effort warning 可接受，精确化可单开 follow-up。
- 是否提供 `mo agent install supervisor --dry-run` 预览将创建/跳过哪些资源？非本范围，视采纳反馈再定。
