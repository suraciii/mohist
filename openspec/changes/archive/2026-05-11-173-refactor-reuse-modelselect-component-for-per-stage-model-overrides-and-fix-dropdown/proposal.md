## Why

Issue Detail 页面的 per-stage model overrides dropdown 因 `@headlessui/react` v2 中 `Popover` + `Transition` + `portal={false}` 的状态同步冲突而无法打开，用户点击后无任何反应。Settings 页面内嵌的 `ModelSelect` 组件不使用 `Transition`，工作正常且具备搜索、键盘导航、provider 分组等完整功能。本次变更通过提取并复用该组件来修复 dropdown 问题，同时统一 Issue 页与 Settings 页的模型选择 UX。

## What Changes

- 从 `AiSettingsSection.tsx` 提取内嵌的 `ModelSelect` 组件到独立文件 `components/ModelSelect.tsx`
- 增强 `ModelSelect`：新增 `size` prop（`'default' | 'compact'`），支持 `Model[]` 和 `string[]` 两种 `models` 输入格式
- 重构 `IssueModelSelector.tsx`：删除 per-stage 内嵌的 `Popover` + `Transition` 实现（约 60 行），改用复用的 `ModelSelect` 组件
- 更新 `AiSettingsSection.tsx`：导入提取后的 `ModelSelect` 组件，保持 Settings 页面功能不变
- 不修改后端 API、模型发现逻辑、`IssueModelSelector` 的主模型选择器，也不添加新功能

## Capabilities

### New Capabilities

<!-- List new capabilities (kebab-case). Each becomes specs/<name>/spec.md. Leave empty if none. -->

### Modified Capabilities

<!-- List existing capabilities whose requirements change. Each needs a delta spec. Leave empty if none. -->

## Impact

- **前端文件**：`packages/cli/web/src/components/AiSettingsSection.tsx`、`packages/cli/web/src/components/IssueModelSelector.tsx`、新建 `packages/cli/web/src/components/ModelSelect.tsx`
- **API / 后端**：无改动
- **依赖**：无新增或移除
- **Breaking changes**：无
