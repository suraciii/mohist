## Why

Settings 页面列出 110+ 个 provider，全部平铺无分组无搜索。用户找到一个 provider 需要大量滚动，体验差。随着 provider 数量持续增长（通过 ModelsDev 自动同步），这个问题只会恶化。

## What Changes

- Settings Provider 列表顶部添加实时搜索框，按名称/id 过滤 provider
- Provider 按分组展示：已连接（顶部）、推荐/常用、Coding Plan 类、按区域分类（中国/国际）、Custom（底部）
- 每组默认折叠，只展示前 N 个，点击展开全部
- Provider 数据增加分类元数据（category、region、recommended 等字段）
- Provider 图标和描述覆盖范围扩大（当前仅 7 个有描述、5 个有图标颜色）

## Capabilities

### New Capabilities

- `provider-search` — Settings 页面 provider 列表的实时搜索/过滤功能
- `provider-categorization` — Provider 分类元数据定义与分组展示逻辑

### Modified Capabilities

<!-- List existing capabilities whose requirements change. Each needs a delta spec. Leave empty if none. -->

## Impact

<!-- Affected code, APIs, dependencies, or systems. -->

- **前端**: `SettingsPage.tsx` 重构 provider 列表渲染逻辑，添加搜索组件和分组折叠组件
- **前端数据**: 新增 `provider-categories.ts` 前端分类映射表，不修改后端 API
- **无 Breaking Change**: 纯 UI 增强，无 API 变更
