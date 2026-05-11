## Context

Issue Detail 的 per-stage model overrides（`explore`/`plan`/`build`/`check`/`integrate`）使用 `@headlessui/react` v2 的 `Popover` + `Transition` + `portal={false}` 组合实现，存在状态同步冲突：`Transition` 的 enter/leave 动画与 `Popover.Panel` 的条件渲染竞争，导致面板从未实际渲染到 DOM 中。用户点击 dropdown 按钮后无任何视觉反馈。

与此同时，Settings 页面的 `AiSettingsSection` 内嵌了一个功能更完整的 `ModelSelect` 组件：支持搜索过滤、键盘导航（↑↓ Enter）、provider 分组、clear 按钮，且**不使用 `Transition`**，只依赖 `Popover` + `portal={false}`，工作正常。该组件目前是 `AiSettingsSection` 的局部函数，未被复用。

本次变更将其提取为共享组件，并增强以适配 Issue 页面的紧凑布局需求，从而在不修改后端的前提下修复 dropdown 并统一两处 UX。

## Goals / Non-Goals

**Goals:**
- 修复 Issue Detail per-stage model overrides dropdown 无法打开的问题
- 统一 Issue 页与 Settings 页的模型选择交互体验（搜索、键盘导航、provider 分组）
- 提取可复用的 `ModelSelect` 组件，消除重复实现

**Non-Goals:**
- 修改后端 API（`PATCH /api/issues/:number` 的 `stageModels` 接口不变）
- 修改 `IssueModelSelector` 的主模型选择器（Coder Model dropdown）
- 修改模型发现逻辑（`/opencode/models`）
- 添加新功能（模型推荐、智能选择等）

## Decisions

### D1: 复用 `AiSettingsSection` 的内嵌 `ModelSelect` 而非重写或引入第三方库

提取 Settings 页面已有的 `ModelSelect`（基于 `Popover` 无 `Transition`）作为共享组件，替代 Issue 页面有问题的 `Popover` + `Transition` 实现。

**Alternatives considered:**
- **重写一个基于原生 `<select>` 或 `<datalist>` 的组件**：无法提供搜索、分组、clear 等已存在的交互，且会引入第三种样式体系。
- **升级 `@headlessui/react` 或降级到 v1**：v2 的 `Transition` + `portal={false}` 问题在已知 issue 中未明确修复时间表；升级/降级可能引入其他回归，且超出本 issue 范围。
- **保持 `Transition` 但改为 `portal={true}`**：portal 会导致 dropdown 在移动端的 fixed 定位脱离原容器，与现有响应式布局冲突。

### D2: `size='compact'` 通过 Tailwind 类名切换而非 CSS-in-JS 或内联样式

组件接收 `size?: 'default' | 'compact'` prop，内部根据 size 拼接不同的 Tailwind className（如 `text-xs px-2 py-1` vs `text-sm px-3 py-2`）。这与项目中已有的 Tailwind 使用方式一致。

**Alternatives considered:**
- **内联 style 对象**：与项目中全部使用 Tailwind className 的惯例不一致。
- **CSS Modules**：项目未使用 CSS Modules，引入会增加构建复杂度。

### D3: `models` prop 支持 `Model[] | string[]` 以兼容两种调用方

Settings 页面已有 `Model[]`（带 `id`/`name` 等字段）；Issue 页面从 API 直接获取 `string[]`（model ID 列表）。组件内部通过类型守卫或运行时检查自动将 `string[]` 转换为 `Model[]`（`name` 取 `id.split('/').pop()`），避免调用方做重复转换。

**Alternatives considered:**
- **强制调用方统一转换为 `Model[]`**：会增加 `IssueModelSelector` 的代码量，违背最小改动原则。

## Risks / Trade-offs

- **[Risk] `ModelSelect` 提取后 Settings 页面行为意外改变** → **Mitigation**：`AiSettingsSection` 的调用方式保持不变（`Model[]` + `size='default'`），提取后仅将组件定义从局部函数移到独立文件，逻辑零改动。提取后立即验证 Settings 页 Stage Model Overrides 功能正常。
- **[Risk] Issue 页面的紧凑样式与现有 `ModelSelect` 的固定样式冲突** → **Mitigation**：通过 `size='compact'` 控制，仅影响按钮和面板内文字/间距，不影响 Popover 定位逻辑。视觉回归通过截图或手动测试确认。
- **[Risk] `string[]` 自动转换后 `name` 显示不一致** → **Mitigation**：转换逻辑与 Issue 页面当前 `modelDisplayName` 函数一致（`id.split('/').pop()`），行为无变化。

## Migration Plan

1. 将 `AiSettingsSection.tsx` 中 `ModelSelect` 的定义（含内部图标组件 `SearchIcon`、`ChevronDownIcon`、`XIcon`）完整复制到 `components/ModelSelect.tsx`
2. 在 `ModelSelect.tsx` 中：
   - 导出 `ModelSelect` 组件
   - 添加 `size?: 'default' | 'compact'` prop
   - 添加 `models: Model[] | string[]` 支持及自动转换逻辑
   - 调整 `Popover.Button` 和面板内元素的 className 根据 `size` 切换
3. 更新 `AiSettingsSection.tsx`：删除内嵌 `ModelSelect`，改为 `import { ModelSelect } from './ModelSelect'`
4. 更新 `IssueModelSelector.tsx`：
   - 删除 per-stage 的 `Popover` + `Transition` 块（约 60 行）
   - 在 stage map 中替换为 `<ModelSelect size="compact" models={allModels} ... />`
   - 保留 `handleSetStageModel` / `handleClearStageModel` 及 clear 按钮
5. 构建并运行前端测试，验证：
   - Settings 页面 Stage Model Overrides 功能正常
   - Issue Detail per-stage dropdown 可正常打开、搜索、选择、清除

## Open Questions

无
