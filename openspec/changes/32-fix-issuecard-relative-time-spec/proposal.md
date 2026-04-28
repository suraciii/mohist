## Why

Issue #30 的实现审查发现 5 处 spec 偏差：`formatRelativeTime` 缺少月份格式（60 天显示 "60d ago" 而非 "2mo ago"）、三个颜色值与 spec 不匹配、`getTypeColor` 未导出、area labels 列表不一致、以及 tasks.json 元数据未同步。这些偏差导致 UI 与设计稿不一致，需要统一修复。

## What Changes

- 在 `relative-time.ts` 中添加月份计算逻辑：`>= 30 天 → "Xmo ago"`
- 修正 `label-colors.ts` 中三组颜色值：feature `#16a34a→#22c55e`、enhancement `#2563eb→#3b82f6`、performance `#ca8a04→#eab308`（同步更新 TYPE_LABEL_COLORS 和 TYPE_STRIP_COLORS）
- 同步修正 spec 中 `performance` 文字色为 `#eab308`（当前 spec 第 109 行写的 `#ca8a04` 与第 28 行色带 spec `#eab308` 自相矛盾，以色带值为准统一）
- 补充导出 `getTypeColor(label: string): { bg: string; text: string }` 函数
- 更新 `AREA_LABEL_COLORS` 列表对齐 spec：添加 frontend、logging、data-model、recovery、explore，移除 cli、db、infra
- 更新 `openspec/changes/30-feat-kanban-issuecard-priority/tasks.json` 中 T-004 的 passes/attempts 字段

## Capabilities

### New Capabilities

### Modified Capabilities

- `kanban-issue-card` — 修正 relative-time 月份格式、颜色值、area labels 列表、补导 getTypeColor

## Impact

- `packages/cli/web/src/lib/relative-time.ts` — 添加月份级计算分支
- `packages/cli/web/src/lib/label-colors.ts` — 修正 3 组颜色 + 补 area labels + 补导出函数
- `openspec/changes/30-feat-kanban-issuecard-priority/tasks.json` — T-004 元数据修正
- `openspec/changes/30-feat-kanban-issuecard-priority/specs/kanban-issue-card/spec.md` — 修正 performance 文字色 `#ca8a04→#eab308` 统一色带与文字色
