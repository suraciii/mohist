## Context

Settings 页面当前有 110+ 个 builtin provider，通过 `GET /api/providers` 获取，在 `SettingsPage.tsx` 中分三段平铺渲染：ConnectedProvidersList、AvailableProvidersList、CustomProvidersSection。无搜索、无分组、无折叠。前端 Provider 接口有 `id`、`name`、`configured`、`isBuiltin` 等字段，但缺少分类信息。后端数据源来自 `models-snapshot.js`（同步）和 ModelsDev API（异步缓存），当前只提取 `sdk`、`name`、`baseURL`、`envVars`。

已有可复用的 UI 模式：`ModelSelector.tsx` 使用 `fuzzysort` 做模糊搜索，`StageColumn.tsx` 有 "Show more/less" 折叠模式。

## Goals / Non-Goals

**Goals:**
- Provider 列表支持实时搜索过滤
- Provider 按分组展示（已连接→推荐→Coding Plan→中国区→国际区→Custom）
- 每组默认折叠显示前 5 个，可展开
- API 响应包含分类元数据（`category`、`region`）

**Non-Goals:**
- 字母索引/快速跳转（投入产出比低，延迟到后续迭代）
- Provider 图标和描述覆盖范围扩大（独立优化，不阻塞本次变更）
- 修改 models-snapshot 数据源结构（上游自动同步，不应侵入）
- 分组状态持久化（刷新后回到默认折叠即可）

## Decisions

### D1: 分类映射维护在前端常量文件

分类映射定义在前端 `provider-categories.ts` 文件中，不修改后端 API 响应结构。理由：
- `models-snapshot.js` 从上游自动同步，修改它会在每次同步时被覆盖
- 分组是纯 UI 关注点，后端 API 不需要感知分组逻辑
- 新增 provider 时只需更新一个前端常量文件

映射结构：
```typescript
// provider-categories.ts
export const PROVIDER_CATEGORIES: Record<string, {
  category: 'recommended' | 'coding-plan' | 'china' | 'international'
  region: 'china' | 'international'
}> = {
  openai: { category: 'recommended', region: 'international' },
  anthropic: { category: 'recommended', region: 'international' },
  deepseek: { category: 'recommended', region: 'china' },
  // ...
}
```

**Alternatives considered:**
- **后端 API 扩展 ProviderListItem**：需改动 `builtin-providers.ts` + `providers.ts` API，侵入性强，且分类是 UI 概念
- **models-snapshot 增加字段**：上游同步会覆盖，不可行

### D2: 搜索使用 fuzzysort

复用 `ModelSelector.tsx` 已引入的 `fuzzysort` 库，对 `name` 和 `id` 做模糊搜索。不引入新依赖。

**Alternatives considered:**
- **纯 string.includes()**：够用但体验不如模糊搜索（如 "ds" 匹配 "DeepSeek"）
- **新引入 fuse.js**：额外依赖，fuzzysort 已足够

### D3: 分组组件复用 StageColumn 折叠模式

使用与 `StageColumn.tsx` 相同的 `useState + slice + "Show all (N)"` 模式实现折叠。不引入 accordion 组件库。

每个分组渲染为独立的 `ProviderGroup` 组件：
```
ProviderGroup
  ├── 标题行（分组名 + 计数）
  ├── Provider 卡片列表（slice(0, expanded ? all : 5)）
  └── "Show all (N)" / "Show less" 按钮
```

**Alternatives considered:**
- **@headlessui Disclosure**：可用但增加复杂度，现有手写模式足够简单

### D4: SettingsPage 重构为单层分组列表

移除当前三段式结构（ConnectedProvidersList + AvailableProvidersList + CustomProvidersSection），统一为 `ProviderGroup[]` 渲染。分组逻辑封装在 `useProviderGroups(providers, searchQuery)` hook 中。

```
useProviderGroups hook:
  1. 搜索过滤（如果有 searchQuery）
  2. 将 providers 分配到 6 个分组（按优先级：已连接 > 推荐 > coding-plan > china > international > custom）
  3. 每组内部按 name 排序
  4. 返回 GroupedProviders[]
```

**Alternatives considered:**
- **保留三段式 + 分组叠加**：逻辑交错复杂，不如统一分组清晰

## Risks / Trade-offs

- **[Provider 数量增长导致分类映射维护成本]** → 映射文件集中管理，未在映射中的 provider 归入"国际区"兜底分组，不会丢失
- **[搜索时大列表性能]** → 110+ 个 provider 的过滤是纯内存操作，fuzzysort 在这个量级无需虚拟化
- **[前端映射与后端数据不同步]** → 未匹配的 provider 默认归入 international 分组，不丢失；定期 review 即可

## Migration Plan

1. 创建 `provider-categories.ts` 前端分类映射
2. 创建 `useProviderGroups` hook
3. 创建 `ProviderGroup` 组件（标题 + 折叠列表）
4. 重构 `SettingsPage.tsx`：替换三段式为搜索框 + 分组列表
5. 保留现有 Dialog 组件（ProviderConnectDialog、CustomProviderDialog）不变
6. 无需数据迁移，无 API 变更，无需回滚策略

## Open Questions

- 推荐列表的具体 provider 清单需确认（初步：openai、anthropic、deepseek、google、groq、mistral 共 6 个）
