## Why

CLI 暴露了 `mo issue` / `mo project` 命令组，但 Epic 只能从 Web UI 管理——`mo epic list` 直接报错（"did you mean repo"），断了 CLI 工作流。更刺眼的是 issue 与 epic 共用编号命名空间却是两个独立实体：`mo issue show 8` 拿不到 epic #8 的 body，用户没有任何 CLI 路径访问 epic。Web UI 能做的 epic 操作，CLI 也应该能做；现有 `EpicRoutes.cs` 端点已稳定，只缺一层 CLI wiring。

## What Changes

- 新增 `mo epic` 顶层命令组（与 `mo issue` / `mo project` 同级），wiring 到已存在的 `EpicRoutes.cs` 端点，零服务端改动、零领域模型改动
- 8 个子命令：
  - `mo epic list` — 列出当前项目所有 epic
  - `mo epic create <title> [--description <text>] [--priority <p0|p1|p2|p3>]` — 创建 epic
  - `mo epic show <id|num>` — 显示 epic 详情（含关联 issue 列表），参数支持 id 或 number 双形态
  - `mo epic update <id|num> [--title] [--description] [--priority]` — 修改字段
  - `mo epic link <epic-id|num> <issue-id|num>` — 把 issue 加入 epic
  - `mo epic unlink <epic-id|num> <issue-id>` — 把 issue 移出 epic
  - `mo epic done <id|num>` — 标记 epic 完成
  - `mo epic close <id|num>` — 关闭 epic
- 所有命令支持 `--project <name>` / `--project-id <id>` 覆盖当前活动项目（同 `mo issue` 模式）
- 所有命令支持 `-o table|json`；table shape 走 `MohistCliApi.TableShape`
- 透传服务端 conflict 错误而非静默成功：`EPIC_NOT_READY_TO_MARK_DONE`（有未交付 issue）、`EPIC_ALREADY_TERMINAL`（已终结）、`DUPLICATE_EPIC_MEMBERSHIP`（issue 已属其他 epic）
- `mo epic --help` 列出全部子命令；每个子命令 `--help` 列出参数与选项
- CLI 集成测试覆盖：list 空/非空、create 缺 title 报错、link 重复归属冲突、done 未就绪 conflict
- 新文件 `MohistCliCommands.Epic.cs`，在 `MohistCliCommands.cs:10-29` 注册
- 命令命名以 `link` / `unlink` 取代既有 spec sketch 中的 `add-issue` / `remove-issue`（sketch 从未实现，pre-release 无版本兼容负担）

## Capabilities

### New Capabilities

无。Epic 的领域模型（`epic-tracking`）与 HTTP 端点（`http-api` 的 "Epic API Endpoints" requirement）均已存在且稳定，本 issue 仅在 CLI 层消费它们，不引入新能力。

### Modified Capabilities

- `cli-interface`: 把现有 "Epic CLI Commands" requirement 从泛化 sketch（`mo epic add-issue` / `remove-issue`，只描述 create/list/show/membership/done/close 的粗粒度行为）concretize 为本 issue 的 8 个具体命令（新增 `update`；`add-issue`/`remove-issue` 改名为 `link`/`unlink`）。明确补充：`<id|num>` 双形态参数、`-o table|json` 输出、`--project` / `--project-id` 项目覆盖、conflict / 状态转换错误码透传（`EPIC_NOT_READY_TO_MARK_DONE`、`EPIC_ALREADY_TERMINAL`、`DUPLICATE_EPIC_MEMBERSHIP`）、`mo epic --help` 与子命令 `--help`、以及 CLI 集成测试覆盖。

## Impact

- **代码**：新增 `MohistCliCommands.Epic.cs`（命令组实现）；`MohistCliCommands.cs:10-29` 注册新命令组；新增 CLI 集成测试（list 空/非空、create 缺 title、link 冲突、done 未就绪）
- **API**：无改动——消费现有 `EpicRoutes.cs` 端点（`GET/POST /api/projects/{p}/epics`、`GET/PATCH /api/projects/{p}/epics/{id}`、`POST/DELETE /api/projects/{p}/epics/{id}/issues`、`POST .../done`、`POST .../close`）
- **领域模型**：无改动——不改 epic 字段、状态机、归属规则（见 Non-Goals）
- **Web UI**：无改动——不动 epic 页面
- **依赖**：无新增
- **用户体验**：用户可在终端完成 epic 全生命周期管理（list / create / show / update / link / unlink / done / close），无需切到 Web UI；issue 与 epic 共用编号命名空间的痛点得到部分缓解——两种实体都能从 CLI 访问，`mo epic show 8` 终于能拿到 epic #8 而非 issue #8
- **风险**：`risk: low`——纯 CLI 新增，零服务端改动，不影响任何现有命令，不动业务逻辑；端点已存在且稳定
