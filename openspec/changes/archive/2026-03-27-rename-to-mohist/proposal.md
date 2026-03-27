## Why

`crawlph` 这个名字没有辨识度，需要一个更独特的品牌标识。同时，当前代码结构不支持未来异构技术栈（如 CLI 用 Node，Server 用 Go）的扩展。

选择 `mohist`：简短、易记，CLI 命令 `mo` 输入友好。

## What Changes

- **BREAKING** 项目重命名：`crawlph` → `mohist`
- **BREAKING** CLI 命令：`crawlph` → `mo`
- **BREAKING** 数据目录：`~/.crawlph/` → `~/.mohist/`
- 目录重组：`crawlph-cli/` → `packages/cli/` (monorepo 结构)
- 根目录新增 workspace 配置

## Capabilities

### New Capabilities

(无新增 capability)

### Modified Capabilities

- `cli-interface`: CLI 命令从 `crawlph` 改为 `mo`
- `server-daemon`: 数据目录从 `~/.crawlph/` 改为 `~/.mohist/`
- `state-persistence`: 数据库路径从 `crawlph.db` 改为 `mohist.db`

## Impact

- **目录结构**：`crawlph-cli/` 移动到 `packages/cli/`
- **源码**：~84 处硬编码字符串需要更新
- **文档**：README, AGENTS.md, openspec specs
- **兼容性**：用户需迁移 `~/.crawlph/` 到 `~/.mohist/`
