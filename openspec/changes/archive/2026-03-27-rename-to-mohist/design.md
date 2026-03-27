## Context

当前 `crawlph-cli/` 是一个独立的 npm 包，位于项目根目录下。代码、测试、配置都在这个目录内。

目标：
1. 重命名为 `mohist`，CLI 命令为 `mo`
2. 迁移到 monorepo 结构，支持未来异构技术栈

## Goals / Non-Goals

**Goals:**
- 完成品牌重命名
- 建立 monorepo 结构
- 保持功能不变
- 数据迁移路径清晰

**Non-Goals:**
- 不拆分 CLI 和 Server（后续 change）
- 不改变任何业务逻辑
- 不添加新功能

## Decisions

### D1: 使用 `packages/` 作为 monorepo 目录名

**理由**：`packages/` 是最通用的命名，语言无关，被广泛理解。

**备选**：`src/`（语义上有 src 里的 src，不清晰）、`apps/`+`packages/`（过度设计）

### D2: CLI 包名为 `mohist`，不是 `@mohist/cli`

**理由**：单一包时无需 scope，更简洁。

### D3: 数据目录使用 `~/.mohist/`

**理由**：与品牌一致，用户明确知道是 mohist 的数据。

### D4: 不自动迁移旧数据

**理由**：当前用户量小，手动迁移成本可接受。自动迁移增加复杂度和风险。

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| 用户忘记迁移数据 | 首次运行时检测 `~/.crawlph/` 并提示 |
| 遗漏硬编码引用 | 全局搜索 + grep 验证 |
| tsconfig 路径问题 | 编译验证 |
