## MODIFIED Requirements

### Requirement: IssueCard 左侧类型色带

IssueCard SHALL 在卡片左侧渲染一条 4px 宽的竖直色带，颜色由 issue 的类型标签决定。色带 SHALL 贯穿卡片全高。

颜色映射：
- `bug` → 红色 (#ef4444)
- `feature` → 绿色 (#22c55e)
- `enhancement` → 蓝色 (#3b82f6)
- `tech-debt` → 灰色 (#6b7280)
- `performance` → 黄色 (#eab308)
- 无类型标签或其他标签 → 灰色 (#6b7280)

#### Scenario: bug 标签显示红色色带

- **WHEN** issue labels 包含 "bug"
- **THEN** 卡片左侧色带颜色为 #ef4444（红色）

#### Scenario: feature 标签显示绿色色带

- **WHEN** issue labels 包含 "feature"
- **THEN** 卡片左侧色带颜色为 #22c55e（绿色）

#### Scenario: enhancement 标签显示蓝色色带

- **WHEN** issue labels 包含 "enhancement"
- **THEN** 卡片左侧色带颜色为 #3b82f6（蓝色）

#### Scenario: performance 标签显示黄色色带

- **WHEN** issue labels 包含 "performance"
- **THEN** 卡片左侧色带颜色为 #eab308（黄色）

#### Scenario: 多个类型标签取第一个

- **WHEN** issue labels 同时包含 "bug" 和 "feature"
- **THEN** 色带颜色按类型标签优先级决定：bug > feature > enhancement > tech-debt > performance

#### Scenario: 无类型标签显示灰色色带

- **WHEN** issue labels 不包含任何类型标签（bug/feature/enhancement/tech-debt/performance）
- **THEN** 卡片左侧色带颜色为 #6b7280（灰色）

### Requirement: IssueCard Label 颜色映射

IssueCard SHALL 对 labels 按类别使用不同的渲染样式：

**类型标签**（着色药丸）：
- `bug` → 红色背景 (#fee2e2) + 红色文字 (#ef4444)
- `feature` → 绿色背景 (#dcfce7) + 绿色文字 (#22c55e)
- `enhancement` → 蓝色背景 (#dbeafe) + 蓝色文字 (#3b82f6)
- `tech-debt` → 灰色背景 (#f3f4f6) + 灰色文字 (#6b7280)
- `performance` → 黄色背景 (#fef9c3) + 黄色文字 (#eab308)

**紧急度标签**（特殊样式）：
- `critical` → 深红背景 (#991b1b) + 白色文字

**区域标签**（小号灰色药丸）：
- agent, webui, api, frontend, logging, data-model, recovery, explore → 灰色背景 (#f3f4f6) + 灰色文字 (#6b7280)

**未知标签** → 灰色默认药丸

Labels SHALL 在卡片底部一行显示，overflow 时截断。

#### Scenario: 类型标签着色药丸

- **WHEN** issue labels 包含 "bug"
- **THEN** "bug" 标签渲染为红色着色药丸（背景 #fee2e2，文字 #ef4444）

#### Scenario: critical 标签特殊样式

- **WHEN** issue labels 包含 "critical"
- **THEN** "critical" 标签渲染为深红背景白色文字药丸（背景 #991b1b，文字 #ffffff）

#### Scenario: 区域标签灰色药丸

- **WHEN** issue labels 包含 "agent"
- **THEN** "agent" 标签渲染为小号灰色药丸（背景 #f3f4f6，文字 #6b7280）
- **AND** 字号小于类型标签

#### Scenario: feature 标签绿色着色药丸

- **WHEN** issue labels 包含 "feature"
- **THEN** "feature" 标签渲染为绿色着色药丸（背景 #dcfce7，文字 #22c55e）

#### Scenario: enhancement 标签蓝色着色药丸

- **WHEN** issue labels 包含 "enhancement"
- **THEN** "enhancement" 标签渲染为蓝色着色药丸（背景 #dbeafe，文字 #3b82f6）

#### Scenario: performance 标签黄色着色药丸

- **WHEN** issue labels 包含 "performance"
- **THEN** "performance" 标签渲染为黄色着色药丸（背景 #fef9c3，文字 #eab308）

### Requirement: IssueCard 显示相对时间

IssueCard SHALL 在卡片右下角显示 issue 最近更新时间的相对时间文本（如 "2d ago"、"3h ago"、"just now"）。文本颜色 SHALL 为灰色小字。

相对时间格式：
- < 1 分钟 → "just now"
- < 1 小时 → "Xm ago"
- < 24 小时 → "Xh ago"
- < 30 天 → "Xd ago"
- >= 30 天 → "Xmo ago"（月份取 Math.floor(days / 30)）

#### Scenario: 显示相对时间

- **WHEN** issue updatedAt 为 2 天前的时间戳
- **THEN** 卡片右下角显示灰色小字 "2d ago"

#### Scenario: 刚刚更新

- **WHEN** issue updatedAt 为 30 秒前
- **THEN** 卡片右下角显示灰色小字 "just now"

#### Scenario: 月份级时间

- **WHEN** issue updatedAt 为 60 天前的时间戳
- **THEN** 卡片右下角显示灰色小字 "2mo ago"

#### Scenario: 刚好 30 天

- **WHEN** issue updatedAt 为 30 天前的时间戳
- **THEN** 卡片右下角显示灰色小字 "1mo ago"

#### Scenario: 无 updatedAt 时间

- **WHEN** issue updatedAt 为 null 或 undefined
- **THEN** 使用 createdAt 作为回退
- **AND** 如果 createdAt 也为 null，SHALL 不显示时间文本

### Requirement: Label 颜色映射工具模块

SHALL 提供 `label-colors.ts` 工具模块，导出以下函数：

- `getTypeColor(label: string): { bg: string; text: string }` — 返回类型标签的背景色和文字色
- `getStripColor(labels: string[]): string` — 返回左侧色带颜色
- `isTypeLabel(label: string): boolean` — 判断是否为类型标签
- `isUrgencyLabel(label: string): boolean` — 判断是否为紧急度标签
- `isAreaLabel(label: string): boolean` — 判断是否为区域标签
- `getLabelStyle(label: string): { bg: string; text: string; size: 'sm' | 'md' }` — 返回任意标签的完整渲染样式

颜色值 SHALL 与色带 spec 和标签着色药丸 spec 完全一致：
- feature: text #22c55e, strip #22c55e, bg #dcfce7
- enhancement: text #3b82f6, strip #3b82f6, bg #dbeafe
- performance: text #eab308, strip #eab308, bg #fef9c3

区域标签列表 SHALL 包含：agent, webui, api, frontend, logging, data-model, recovery, explore

#### Scenario: 获取 bug 标签样式

- **WHEN** 调用 `getLabelStyle('bug')`
- **THEN** 返回 `{ bg: '#fee2e2', text: '#ef4444', size: 'md' }`

#### Scenario: 获取 critical 标签样式

- **WHEN** 调用 `getLabelStyle('critical')`
- **THEN** 返回 `{ bg: '#991b1b', text: '#ffffff', size: 'md' }`

#### Scenario: 获取区域标签样式

- **WHEN** 调用 `getLabelStyle('agent')`
- **THEN** 返回 `{ bg: '#f3f4f6', text: '#6b7280', size: 'sm' }`

#### Scenario: 获取 feature 类型颜色

- **WHEN** 调用 `getTypeColor('feature')`
- **THEN** 返回 `{ bg: '#dcfce7', text: '#22c55e' }`

#### Scenario: 获取 enhancement 类型颜色

- **WHEN** 调用 `getTypeColor('enhancement')`
- **THEN** 返回 `{ bg: '#dbeafe', text: '#3b82f6' }`

#### Scenario: 获取 performance 类型颜色

- **WHEN** 调用 `getTypeColor('performance')`
- **THEN** 返回 `{ bg: '#fef9c3', text: '#eab308' }`

#### Scenario: 区域标签列表包含所有 spec 标签

- **WHEN** 调用 `isAreaLabel('frontend')`
- **THEN** 返回 `true`

- **WHEN** 调用 `isAreaLabel('logging')`
- **THEN** 返回 `true`

- **WHEN** 调用 `isAreaLabel('data-model')`
- **THEN** 返回 `true`

- **WHEN** 调用 `isAreaLabel('recovery')`
- **THEN** 返回 `true`

- **WHEN** 调用 `isAreaLabel('explore')`
- **THEN** 返回 `true`

- **WHEN** 调用 `isAreaLabel('cli')`
- **THEN** 返回 `false`

### Requirement: 相对时间格式化工具模块

SHALL 提供 `relative-time.ts` 工具模块，导出 `formatRelativeTime(date: Date | string | null): string` 函数。

格式化规则：
- < 60 秒 → "just now"
- < 60 分钟 → "Xm ago"
- < 24 小时 → "Xh ago"
- < 30 天 → "Xd ago"
- >= 30 天 → "Xmo ago"（X = Math.floor(days / 30)）

#### Scenario: 格式化分钟级时间

- **WHEN** 调用 `formatRelativeTime` 传入 45 分钟前的时间戳
- **THEN** 返回 "45m ago"

#### Scenario: 格式化天数级时间

- **WHEN** 调用 `formatRelativeTime` 传入 7 天前的时间戳
- **THEN** 返回 "7d ago"

#### Scenario: 格式化月份级时间

- **WHEN** 调用 `formatRelativeTime` 传入 60 天前的时间戳
- **THEN** 返回 "2mo ago"

#### Scenario: 刚好 30 天

- **WHEN** 调用 `formatRelativeTime` 传入 30 天前的时间戳
- **THEN** 返回 "1mo ago"

#### Scenario: null 输入

- **WHEN** 调用 `formatRelativeTime(null)`
- **THEN** 返回空字符串 ""
