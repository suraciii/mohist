## ADDED Requirements

### Requirement: IssueCard 显示 Priority

IssueCard SHALL 在卡片头部显示 issue 的 priority 字段（P0-P4）。当 priority 不存在或为 null 时，SHALL 不显示 priority 文本。

#### Scenario: 显示 P0-P4 priority

- **WHEN** issue 的 `priority` 字段值为 `'p0'`、`'p1'`、`'p2'`、`'p3'` 或 `'p4'` 字符串
- **THEN** 卡片头部左侧显示 "P0"、"P1"、"P2"、"P3" 或 "P4" 文本
- **AND** 文本位于 issue number 同一行

#### Scenario: 无 priority 时不显示

- **WHEN** issue 的 `priority` 字段为 null、undefined 或空字符串
- **THEN** 卡片头部不显示 priority 文本
- **AND** 卡片布局不因此产生空白间隙

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

#### Scenario: 多个类型标签取第一个

- **WHEN** issue labels 同时包含 "bug" 和 "feature"
- **THEN** 色带颜色按类型标签优先级决定：bug > feature > enhancement > tech-debt > performance

#### Scenario: 无类型标签显示灰色色带

- **WHEN** issue labels 不包含任何类型标签（bug/feature/enhancement/tech-debt/performance）
- **THEN** 卡片左侧色带颜色为 #6b7280（灰色）

### Requirement: IssueCard 条件 badge 叠加

IssueCard SHALL 根据 issue 状态在卡片右上角区域显示条件 badge。多个条件可同时存在时，SHALL 按优先级显示最高优先级的单个 badge。

优先级从高到低：
1. Merge Conflict/Failed（红色 badge）— issue 在 Done 阶段 且 mergeState 非 "merged" 且非 null/undefined
2. Closed（灰色半透明叠加）— issue status 为 "blocked"（对应前端 IssueStatus.Blocked）
3. Approval Waiting（amber badge）— issue approvalState 为 "awaiting"（任何阶段）
4. Agent Running（蓝色脉冲 badge）— issue status 为 "active" 且有 agent session 在运行

#### Scenario: Agent Running badge

- **WHEN** issue status 为 "active"
- **AND** 有 agent session 正在运行
- **THEN** 卡片右上角显示蓝色脉冲动画 badge
- **AND** badge 文本为 "Running"

#### Scenario: Approval Waiting badge

- **WHEN** issue approvalState 为 "awaiting"
- **AND** issue 不在 Done 阶段
- **THEN** 卡片右上角显示 amber（#f59e0b）badge
- **AND** badge 文本为 "Approval"

#### Scenario: Merge Conflict/Failed badge

- **WHEN** issue stage 为 "done"
- **AND** issue mergeState 存在 且 不为 "merged" 且 不为 null
- **THEN** 卡片右上角显示红色 badge
- **AND** badge 文本为 mergeState 的值（如 "conflict"、"failed"）

#### Scenario: Closed 灰色叠加

- **WHEN** issue status 为 "blocked"（IssueStatus.Blocked）
- **THEN** 卡片整体覆盖灰色半透明叠加层（opacity 降低）
- **AND** 叠加层上显示 "Closed" 文字
- **AND** 卡片其他信息仍然可读

#### Scenario: 无特殊状态时无 badge

- **WHEN** issue 不满足上述任何条件
- **THEN** 卡片右上角不显示任何 badge
- **AND** 卡片无灰色叠加

### Requirement: IssueCard Label 颜色映射

IssueCard SHALL 对 labels 按类别使用不同的渲染样式：

**类型标签**（着色药丸）：
- `bug` → 红色背景 (#fee2e2) + 红色文字 (#ef4444)
- `feature` → 绿色背景 (#dcfce7) + 绿色文字 (#22c55e)
- `enhancement` → 蓝色背景 (#dbeafe) + 蓝色文字 (#3b82f6)
- `tech-debt` → 灰色背景 (#f3f4f6) + 灰色文字 (#6b7280)
- `performance` → 黄色背景 (#fef9c3) + 黄色文字 (#ca8a04)

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

### Requirement: IssueCard 显示相对时间

IssueCard SHALL 在卡片右下角显示 issue 最近更新时间的相对时间文本（如 "2d ago"、"3h ago"、"just now"）。文本颜色 SHALL 为灰色小字。

相对时间格式：
- < 1 分钟 → "just now"
- < 1 小时 → "Xm ago"
- < 24 小时 → "Xh ago"
- < 30 天 → "Xd ago"
- >= 30 天 → "Xmo ago"

#### Scenario: 显示相对时间

- **WHEN** issue updatedAt 为 2 天前的时间戳
- **THEN** 卡片右下角显示灰色小字 "2d ago"

#### Scenario: 刚刚更新

- **WHEN** issue updatedAt 为 30 秒前
- **THEN** 卡片右下角显示灰色小字 "just now"

#### Scenario: 无 updatedAt 时间

- **WHEN** issue updatedAt 为 null 或 undefined
- **THEN** 使用 createdAt 作为回退
- **AND** 如果 createdAt 也为 null，SHALL 不显示时间文本

### Requirement: IssueCard Title 两行截断

IssueCard 的 title SHALL 使用 line-clamp-2 截断为最多两行。超出部分 SHALL 显示省略号。

#### Scenario: 长标题截断

- **WHEN** issue title 文本长度超过卡片宽度可容纳的两行
- **THEN** title 显示为两行
- **AND** 第二行末尾显示省略号（...）

#### Scenario: 短标题正常显示

- **WHEN** issue title 文本长度在一行以内
- **THEN** title 正常显示为单行，不截断

### Requirement: 前端 Issue type 包含 priority 字段

前端 Issue interface SHALL 包含 `priority` 字段，类型为 `string | null`，匹配后端 `'p0'|'p1'|'p2'|'p3'|'p4'` 字符串格式。

#### Scenario: API 返回带 priority 的 issue

- **WHEN** API 返回 issue 对象包含 `priority: 'p1'`
- **THEN** 前端 Issue type 正确接收 priority 值为字符串 `'p1'`
- **AND** IssueCard 使用 formatPriority 将其转换为显示文本 "P1"

#### Scenario: API 返回无 priority 的 issue

- **WHEN** API 返回 issue 对象不包含 priority 字段或值为 null
- **THEN** 前端 Issue type 将 priority 解析为 null
- **AND** IssueCard 不显示 priority 文本

### Requirement: Label 颜色映射工具模块

SHALL 提供 `label-colors.ts` 工具模块，导出以下函数：

- `getTypeColor(label: string): { bg: string; text: string }` — 返回类型标签的背景色和文字色
- `getStripColor(labels: string[]): string` — 返回左侧色带颜色
- `isTypeLabel(label: string): boolean` — 判断是否为类型标签
- `isUrgencyLabel(label: string): boolean` — 判断是否为紧急度标签
- `isAreaLabel(label: string): boolean` — 判断是否为区域标签
- `getLabelStyle(label: string): { bg: string; text: string; size: 'sm' | 'md' }` — 返回任意标签的完整渲染样式

#### Scenario: 获取 bug 标签样式

- **WHEN** 调用 `getLabelStyle('bug')`
- **THEN** 返回 `{ bg: '#fee2e2', text: '#ef4444', size: 'md' }`

#### Scenario: 获取 critical 标签样式

- **WHEN** 调用 `getLabelStyle('critical')`
- **THEN** 返回 `{ bg: '#991b1b', text: '#ffffff', size: 'md' }`

#### Scenario: 获取区域标签样式

- **WHEN** 调用 `getLabelStyle('agent')`
- **THEN** 返回 `{ bg: '#f3f4f6', text: '#6b7280', size: 'sm' }`

### Requirement: 相对时间格式化工具模块

SHALL 提供 `relative-time.ts` 工具模块，导出 `formatRelativeTime(date: Date | string | null): string` 函数。

#### Scenario: 格式化分钟级时间

- **WHEN** 调用 `formatRelativeTime` 传入 45 分钟前的时间戳
- **THEN** 返回 "45m ago"

#### Scenario: 格式化天数级时间

- **WHEN** 调用 `formatRelativeTime` 传入 7 天前的时间戳
- **THEN** 返回 "7d ago"

#### Scenario: null 输入

- **WHEN** 调用 `formatRelativeTime(null)`
- **THEN** 返回空字符串 ""
