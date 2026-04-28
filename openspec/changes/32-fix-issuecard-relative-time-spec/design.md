## Context

Issue #30 实现了 IssueCard 看板卡片，但审查发现 5 处实现与 spec 的偏差。所有修改集中在两个前端工具文件和一个元数据文件，无架构变更。

当前状态：
- `relative-time.ts` — 只到 days 级别，缺少 months 分支
- `label-colors.ts` — 3 组颜色值偏暗（用了 Tailwind-600 而非 spec 要求的 Tailwind-500），area labels 列表与 spec 不匹配，缺少 `getTypeColor` 导出
- `tasks.json` T-004 — passes=false/attempts=0，但实际已完成

## Goals / Non-Goals

**Goals:**
- 修正所有 spec 偏差，使实现与 spec 完全一致
- 补齐缺失的 `getTypeColor` 导出函数
- 同步 tasks.json 元数据

**Non-Goals:**
- 不修改 IssueCard.tsx 组件本身（卡片渲染逻辑无变化）
- 不引入 i18n 或更复杂的相对时间库
- 不修改后端 API

## Decisions

### D1: 月份计算用 Math.floor(days / 30)

spec 定义 `>= 30 天 → Xmo ago`，使用简单的 `Math.floor(days / 30)` 而非 `Date` 对象的月份差计算。这避免了跨月边界（如 1/31 → 2/28）的复杂性，且 spec 明确写的是 30 天阈值。

**Alternatives considered:**
- `(now.getFullYear() - then.getFullYear()) * 12 + now.getMonth() - then.getMonth()` — 日历月份精度更高，但 spec 用 30 天阈值，过度工程化

### D2: getTypeColor 从 TYPE_LABEL_COLORS 派生

新增 `getTypeColor` 函数从已有的 `TYPE_LABEL_COLORS` 常量中提取 `{ bg, text }`，不引入新常量。找不到标签时返回 `{ bg: '#f3f4f6', text: '#6b7280' }`（与 DEFAULT_STYLE 的 bg/text 一致）。

**Alternatives considered:**
- 单独维护 TYPE_COLOR_MAP — 冗余数据源，易再次偏差

### D3: Area labels 全量替换

将 AREA_LABEL_COLORS 的键从 `[agent, webui, api, cli, db, infra]` 替换为 spec 定义的 `[agent, webui, api, frontend, logging, data-model, recovery, explore]`。直接替换而非合并，因为 spec 是权威来源。旧的 cli/db/infra 标签将回退到 DEFAULT_STYLE 灰色药丸，视觉上无差异。

### D4: 颜色值统一使用 Tailwind-500 系

当前代码用了 Tailwind-600 系（#16a34a, #2563eb, #ca8a04），spec 要求 Tailwind-500 系（#22c55e, #3b82f6, #eab308）。500 系更亮更鲜明，是 Tailwind 默认色阶，视觉上更突出。

## Risks / Trade-offs

- [Area labels 替换后旧标签 cli/db/infra 的卡片回退默认样式] → 无影响，area 标签默认样式与之前一致（灰色小号药丸），仅 isAreaLabel 返回 false
- [30 天月份精度不够精确（如 365 天显示 "12mo" 而非 "1yr"）] → 当前 spec 无年份格式要求，可后续扩展

## Migration Plan

纯前端静态值替换，无数据迁移。直接修改文件，构建后即生效。
