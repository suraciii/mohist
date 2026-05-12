# Session Transcript 页面与 Opencode 的差距分析

## 探索背景

Mohist 的 session transcript 页面（issue/xxx/session/xxx）经过多次修复，仍存在三个核心问题：
1. Thinking 内容全堆在页面最上方
2. 大量 tool 显示为 "unknown"
3. Tool 的 input/output 是原始 JSON，不可读

本次探索通过深入分析 opencode 源码（`opensrc/opencode`），找出差距的根因和改进路径。

## 页面布局对比

### Mohist 当前布局

```
┌─────────────────────────────────────────────────────────────┐
│  Sticky Session Title                                        │
│  "Session" · 3 turns · Running                               │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  User Prompt                           │ ← 右侧对齐        │
│  │  Task prompt · Show full prompt        │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  ▼ Thinking... 12.3KB · 10:23:45      │ ← 全在最上面！    │
│  │  让我想想...首先检查...然后分析...      │                  │
│  │  1. 查看文件结构                        │                  │
│  │  2. 搜索相关代码...                     │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  Assistant text...                      │ ← 然后才是正文   │
│  │  我来帮你...                            │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  ● unknown  failed                      │ ← unknown！      │
│  │  {"question":"What...","options":[]}     │ ← JSON！         │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  ● read  src/App.tsx                    │ ← 没有分组       │
│  └────────────────────────────────────────┘                  │
│  ┌────────────────────────────────────────┐                  │
│  │  ● grep  pattern="function"            │ ← 没有分组       │
│  └────────────────────────────────────────┘                  │
│  ┌────────────────────────────────────────┐                  │
│  │  ● read  src/utils.ts                   │ ← 没有分组       │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  ● apply_patch                          │                  │
│  │  {"patchText":"*** Add File:..."}        │ ← 还是 JSON！    │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  3 files changed ▼                      │ ← 只有列表       │
│  │  A src/App.tsx                          │                  │
│  │  M src/utils.ts                         │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ● Thinking...                                              │ ← 静态 pulsing dot
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Opencode 布局

```
┌─────────────────────────────────────────────────────────────┐
│  Session Title  ·  Agent · Claude 3.7                       │
│  [share] [more ▼]                                           │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  User Message                          │ ← 左侧对齐        │
│  │  Attached: image.png                    │                  │
│  │  Ref: src/App.tsx                       │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  我来帮你...                           │ ← text inline    │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  [reasoning content inline]            │ ← inline 穿插    │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  让我先查看一下代码...                  │ ← text inline    │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ▼ Gathering context · 3 reads · 2 searches   [v]   │    │ ← Context Group！
│  │    ├─ Read  App.tsx                                   │    │
│  │    ├─ Read  utils.ts                                  │    │
│  │    ├─ Read  config.json                               │    │
│  │    ├─ Search  pattern="function"                      │    │
│  │    └─ Search  pattern="export"                        │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ┌─ [v] Shell  npm test                              │    │ ← BasicTool！    │
│  │  │  $ npm test                                        │    │                  │
│  │  │  PASS  src/App.test.ts                             │    │                  │
│  │  │  ...                                               │    │                  │
│  │  └───────────────────────────────────────────────────┘    │                  │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ┌─ [v] Edit  App.tsx  +23 -5  [diff viewer]         │    │ ← Diff Viewer！  │
│  │  │                                                   │    │                  │
│  │  │  before                    after                  │    │                  │
│  │  │  ┌──────────────┐         ┌──────────────┐        │    │                  │
│  │  │  │ import React │    →    │ import React │        │    │                  │
│  │  │  │ -            │    →    │ + import {   │        │    │                  │
│  │  │  │              │    →    │ +   useState │        │    │                  │
│  │  │  └──────────────┘         └──────────────┘        │    │                  │
│  │  └───────────────────────────────────────────────────┘    │                  │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  这就是修改后的效果...                  │                  │
│  │                              [copy] · 2s · Claude 3.7 │ ← copy + meta！   │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  Thinking...  ▓▓▓░░                                   │    │ ← TextShimmer！  │
│  │  Analyzing test results...                            │    │ ← 动态 subtitle！│
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│                    ┌──────────┐                              │
│                    │  ↓       │ ← 精致 Jump to bottom       │
│                    └──────────┘                              │
└─────────────────────────────────────────────────────────────┘
```

### 我们想要做出的 Mohist 目标布局

```
┌─────────────────────────────────────────────────────────────┐
│  Issue #189 / Session  ·  Build · Running                   │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  Task prompt · Show full prompt        │  ← 右侧对齐        │
│  │  Output: openspec/changes/...          │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  我来帮你解决这个问题...                │  ← text inline   │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  [reasoning content inline]            │  ← inline 穿插   │
│  │  (默认折叠，点击展开)                   │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ▼ Gathering context · 3 reads · 2 searches   [v]   │    │ ← Context Group
│  │    ├─ 📄 Read  App.tsx                                 │    │
│  │    ├─ 📄 Read  utils.ts                                │    │
│  │    ├─ 📄 Read  config.json                             │    │
│  │    ├─ 🔍 Search  "function"                            │    │
│  │    └─ 🔍 Search  "export"                              │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ┌─ [v] 🖥 Shell  npm test                            │    │ ← BasicTool
│  │  │  $ npm test                                       │    │
│  │  │  PASS  src/App.test.ts                            │    │
│  │  └───────────────────────────────────────────────────┘    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  经过测试，现在我来修改代码...          │  ← text inline   │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ┌─ [v] 📝 Edit  App.tsx  +23 -5                    │    │ ← Diff Viewer
│  │  │                                                   │    │
│  │  │  before                    after                  │    │
│  │  │  ┌──────────────┐         ┌──────────────┐        │    │
│  │  │  │ import React │    →    │ import React │        │    │
│  │  │  │ -            │    →    │ + import {   │        │    │
│  │  │  │              │    →    │ +   useState │        │    │
│  │  │  └──────────────┘         └──────────────┘        │    │
│  │  └───────────────────────────────────────────────────┘    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  修改完成！这就是修改后的效果...        │  ← 打字机效果   │
│  │                              [copy] · 2s · claude-3-7 │    │ ← copy + meta
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  🎯 Question  选择测试策略              │  ← 人类可读     │
│  │     A. 运行所有测试                     │                  │
│  │     B. 只运行相关测试                   │  ← 不再 unknown│
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌────────────────────────────────────────┐                  │
│  │  3 files changed ▼                      │  ← Turn Diffs  │
│  │  A src/App.tsx  +23 -5                  │                  │
│  │  M src/utils.ts  +5 -2                  │                  │
│  │  D src/old.ts                           │                  │
│  └────────────────────────────────────────┘                  │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  🧠 Thinking...  ▓▓▓░░                                │    │ ← TextShimmer
│  │  Analyzing test results...                            │    │ ← 动态 subtitle
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│                    ┌──────────┐                              │
│                    │  ↓       │  ← Jump to bottom           │
│                    └──────────┘                              │
└─────────────────────────────────────────────────────────────┘
```

关键改进标注：
- **inline reasoning**：thinking 穿插在 text 中，默认折叠
- **Context Group**：read/grep 自动分组，汇总统计
- **BasicTool**：每个工具有 icon + 人类可读 title + subtitle
- **语义渲染**：bash→pre/code，edit→diff viewer，read→markdown
- **TextShimmer**：running 状态动态闪烁 + 动态 subtitle
- **Copy + Meta**：text 底部显示 copy 按钮 + 模型 + 耗时
- **Unknown 修复**：显示原始 toolName + 关键参数提取

## 数据流对比

### Opencode：亲历者模型

```
┌─────────────────────────────────────────────────────────────┐
│              Opencode 数据流（直接消费 LLM stream）           │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│   LLM API                                                    │
│     │                                                        │
│     ▼                                                        │
│   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐   │
│   │reasoning│──▶│ text    │──▶│ tool    │──▶│reasoning│   │
│   │chunk 1  │   │chunk 1  │   │call    │   │chunk 2  │   │
│   └─────────┘   └─────────┘   └─────────┘   └─────────┘   │
│        │            │              │              │         │
│        ▼            ▼              ▼              ▼         │
│   ┌─────────────────────────────────────────────────────┐   │
│   │              message.parts 数组（保持真实顺序）       │   │
│   │  [reasoning, text, tool, reasoning, text, ...]      │   │
│   └─────────────────────────────────────────────────────┘   │
│        │                                                    │
│        ▼                                                    │
│   前端按数组索引顺序渲染 parts                               │
│   → text / reasoning / tool 都 inline 展示                  │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Mohist：旁观者模型

```
┌─────────────────────────────────────────────────────────────┐
│            Mohist 数据流（通过 ACP 协议间接观察）            │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│   LLM API                                                    │
│     │                                                        │
│     ▼                                                        │
│   ┌─────────┐   ┌─────────┐   ┌─────────┐   ┌─────────┐   │
│   │reasoning│──▶│ text    │──▶│ tool    │──▶│reasoning│   │
│   │chunk 1  │   │chunk 1  │   │call    │   │chunk 2  │   │
│   └─────────┘   └─────────┘   └─────────┘   └─────────┘   │
│        │            │              │              │         │
│        ▼            ▼              ▼              ▼         │
│   ┌─────────────────────────────────────────────────────┐   │
│   │  ACP 协议层打包成独立事件序列                         │   │
│   │  ┌─ agent_thought_chunk × N（reasoning 序列）       │   │
│   │  ├─ agent_message_chunk × N（message 序列）         │   │
│   │  └─ tool_call × N（tool 序列）                      │   │
│   └─────────────────────────────────────────────────────┘   │
│        │                                                    │
│        ▼                                                    │
│   ┌─────────────────────────────────────────────────────┐   │
│   │  SQLite 存储：created_at = datetime('now')          │   │
│   │  → 精度：秒级                                        │   │
│   │  → 排序：ORDER BY created_at ASC, rowid ASC         │   │
│   │  → 同一秒内按插入顺序（先 thought 后 text）         │   │
│   └─────────────────────────────────────────────────────┘   │
│        │                                                    │
│        ▼                                                    │
│   查询结果：[thought, thought, thought, text, text, tool]   │
│   前端"忠实回放"数组顺序 → thinking 全在最上面              │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

## ToolRegistry 架构对比

### Opencode：注册表模式

```
┌─────────────────────────────────────────────────────────────┐
│              Opencode ToolRegistry 架构                      │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│   ToolRegistry.register({                                    │
│     name: "read",                                            │
│     render: (props) => (                                     │
│       <BasicTool                                             │
│         icon="glasses"                                       │
│         trigger={{ title: "Read", subtitle: filename }}      │
│       >                                                      │
│         <Markdown text={props.output} />                    │
│       </BasicTool>                                           │
│     )                                                        │
│   })                                                         │
│                                                              │
│   ToolRegistry.register({                                    │
│     name: "bash",                                            │
│     render: (props) => (                                     │
│       <BasicTool                                             │
│         icon="console"                                       │
│         trigger={{ title: "Shell", subtitle: command }}     │
│       >                                                      │
│         <pre><code>{output}</code></pre>                     │
│       </BasicTool>                                           │
│     )                                                        │
│   })                                                         │
│                                                              │
│   // fallback                                                │
│   GenericTool: icon="mcp", title=toolName, subtitle=label()│
│                                                              │
│   ┌─────────────────────────────────────────────────────┐   │
│   │  新增工具 = 写一个新的 register 调用                  │   │
│   │  不需要改任何核心代码                                  │   │
│   │  不需要改推断逻辑                                      │   │
│   └─────────────────────────────────────────────────────┘   │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### Mohist：白名单模式

```
┌─────────────────────────────────────────────────────────────┐
│              Mohist 当前推断逻辑                             │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│   function inferNormalizedToolName(d) {                      │
│     if (toolName === 'read') return 'read';                  │
│     if (toolName === 'glob') return 'glob';                  │
│     if (toolName === 'grep') return 'grep';                  │
│     if (toolName === 'bash') return 'bash';                  │
│     if (toolName === 'apply_patch') return 'apply_patch';    │
│     if (input.patchText) return 'apply_patch';               │
│     if (input.command) return 'bash';                        │
│     // ... 只认识这些                                        │
│     return 'unknown';  ← ← ← 这里！                          │
│   }                                                          │
│                                                              │
│   ┌─────────────────────────────────────────────────────┐   │
│   │  新增工具 = 修改 inferNormalizedToolName              │   │
│   │  + 修改前端 inferToolName                             │   │
│   │  + 修改 TOOL_ICONS                                    │   │
│   │  + 修改 displayTitles                                 │   │
│   │  + 修改 ToolRowView（可能没有对应的展示逻辑）         │   │
│   │  = 改 5 个地方，还容易漏                              │   │
│   └─────────────────────────────────────────────────────┘   │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

## 完整差距清单（23 项）

### 数据层（根因级）

| # | 差距 | Opencode | Mohist | 影响 |
|---|------|----------|--------|------|
| 1 | **Reasoning 存储模型** | `message.parts` 数组中的同级元素，与 text 真正交错 | `agent_thought_chunk` 独立事件，秒级时间戳排序 | Thinking 全堆在最上面 |
| 2 | **时间戳精度** | `Date.now()` 毫秒级 | `datetime('now')` 秒级 SQLite | 同一秒内的事件顺序丢失 |

### 推断层（命名级）

| # | 差距 | Opencode | Mohist | 影响 |
|---|------|----------|--------|------|
| 3 | **Tool 名称推断** | `getToolInfo` 注册表，20+ 工具全覆盖 | 白名单 `inferNormalizedToolName`，只认识 6 种 | 大量 unknown |
| 4 | **Unknown fallback** | 显示**原始 tool 名** + 智能提取关键信息 | 显示 `"unknown"` | 用户完全不知道是什么工具 |
| 5 | **前后端推断统一** | 前端直接用后端存储的 `tool` 字段 | 后端推断一遍，前端再推断一遍，逻辑不同 | 同一个工具前后端名字不一致 |

### 展示层（渲染级）

| # | 差距 | Opencode | Mohist | 影响 |
|---|------|----------|--------|------|
| 6 | **Tool input 可读性** | `ToolRegistry.render()` 按类型解析：文件名/搜索模式/命令描述 | 直接 `JSON.stringify()` dump | 用户看到的是 JSON 不是语义 |
| 7 | **Tool output 可读性** | read→Markdown, bash→pre/code, edit→diff viewer | 统一 `pre` tag dump | output 完全不可读 |
| 8 | **Tool 卡片结构** | `BasicTool`：icon + title + subtitle + args + 可展开 content | `ToolRowView`：状态点 + 图标 + 名称 + 路径 | 信息密度低，没有 icon，subtitle 简陋 |
| 9 | **Tool running 动画** | `TextShimmer` 动态闪烁标题 + 动态 subtitle | 静态 blue pulsing dot | 缺少"正在执行"的生动感 |
| 10 | **Context tool grouping** | 自动分组 read/glob/grep/list + 汇总统计（"3 reads · 2 searches"） | 有类似逻辑但分组规则不完全，汇总信息弱 | 上下文读取显得零散 |
| 11 | **Diff viewer** | 完整 diff viewer：before/after + sticky accordion + syntax highlight | 简单文件列表（路径 + 操作类型） | 无法查看具体修改内容 |
| 12 | **Text streaming 效果** | `PacedMarkdown` 打字机效果，24ms/步进 | 直接渲染完整文本 | 流式输出没有"生成中"的感知 |
| 13 | **Copy button + meta** | text part 底部有 copy 按钮 + model + agent + duration | 没有 | 用户无法复制 assistant 回复，看不到用了什么模型 |
| 14 | **Reasoning 展示模式** | inline 渲染，支持 `showReasoningSummaries` 设置（默认隐藏） | `details` 折叠，默认展开 | reasoning 默认可见，占用大量空间 |

### 交互层（体验级）

| # | 差距 | Opencode | Mohist | 影响 |
|---|------|----------|--------|------|
| 15 | **Auto-scroll** | `createAutoScroll`：overflow anchor + 智能跟踪 + 自动/手动切换 + nested scrollable 豁免 | 简单 scrollTop 赋值 + `isNearBottom` 判断 | 滚动体验粗糙，容易打断用户阅读 |
| 16 | **Jump to bottom** | 精致浮动按钮，有阴影和 hover 效果 | 有类似按钮但样式简单 | 视觉 polish 差距 |
| 17 | **User settings** | `showReasoningSummaries`, `shellToolDefaultOpen`, `editToolDefaultOpen` | 无用户偏好 | 无法按需调整展示 |
| 18 | **Retry 状态** | `SessionRetry` 组件：倒计时 + attempt 数 + 错误信息截断 + tooltip | 简单 error part | retry 时用户不知道在等什么 |
| 19 | **Prompt 展示** | attachments + agents + model 信息 + highlighted text references | kind label + title/subtitle + expand/collapse | prompt 的上下文信息展示不够丰富 |
| 20 | **Session title 操作** | 支持重命名、归档、分享 | 只读展示 | 作为工作流工具可能不需要，但 opencode 有 |

### 性能层

| # | 差距 | Opencode | Mohist | 影响 |
|---|------|----------|--------|------|
| 21 | **Timeline staging** | `createTimelineStaging`：初始只渲染最近 N 个 turn，滚动时逐步加载 | 一次性渲染所有 turns | 历史会话长时首屏慢 |
| 22 | **Content-visibility** | 两者都有 `content-visibility: auto` | ✅ 已对齐 | — |
| 23 | **Tool content defer** | `BasicTool` 支持 `defer` 模式，展开后才渲染内容 | 无 defer | 大量 tool 卡片时渲染开销大 |

## 改进路径

```
┌─────────────────────────────────────────────────────────────────┐
│                    修复层次 vs 投入产出                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   P0（立即见效，低投入）                                         │
│   ├── Unknown fallback：显示原始 toolName + 提取关键信息         │
│   ├── Tool trigger 可读化：复用 getToolLabel/getToolArgs         │
│   └── Reasoning 默认折叠                                         │
│        │                                                         │
│        ▼ 用户感知：unknown 少了，JSON 有标题了                    │
│                                                                  │
│   P1（架构升级，中等投入）                                       │
│   ├── 建立 ToolRegistry：注册表替代白名单                        │
│   ├── 按 tool 类型渲染 content                                   │
│   ├── Tool running shimmer 动画                                  │
│   └── Context Group 汇总统计                                     │
│        │                                                         │
│        ▼ 用户感知：每个 tool 都像人话，有动画，有分组             │
│                                                                  │
│   P2（数据重构，较大投入）                                       │
│   ├── 时间戳精度提升到毫秒                                       │
│   ├── 或前端按语义重排 reasoning                                 │
│   ├── Diff Viewer：before/after                                  │
│   └── Text pacing：打字机效果                                    │
│        │                                                         │
│        ▼ 用户感知：thinking 在正确位置，能看 diff，流式感知      │
│                                                                  │
│   P3（体验打磨，长期投入）                                       │
│   ├── Auto-scroll 智能升级                                       │
│   ├── Copy + meta（model, duration）                             │
│   ├── 用户偏好设置                                               │
│   └── Timeline staging                                           │
│        │                                                         │
│        ▼ 用户感知：滚动不烦人，能复制，能定制，长会话不卡        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## 核心结论

> 每次修复只处理了表象（CSS、折叠按钮），没有触及根因。
>
> Opencode 的强大在于**统一数据模型**（message.parts 数组）和**注册表架构**（ToolRegistry）。
>
> Mohist 需要：
> - 数据层：保留 reasoning/text 交错关系
> - 推断层：建立统一注册表，替代白名单
> - 展示层：建立 ToolRegistry，按类型语义渲染

## 参考源码

- `opensrc/opencode/packages/opencode/src/session/processor.ts` — reasoning/text/tool 流处理
- `opensrc/opencode/packages/opencode/src/session/message-v2.ts` — message/part 数据模型
- `opensrc/opencode/packages/opencode/src/session/session.sql.ts` — 数据库 schema
- `opensrc/opencode/packages/ui/src/components/message-part.tsx` — 消息 part 渲染（含 ToolRegistry）
- `opensrc/opencode/packages/ui/src/components/basic-tool.tsx` — tool 通用结构
- `opensrc/opencode/packages/ui/src/components/session-turn.tsx` — turn 级组件
- `opensrc/opencode/packages/ui/src/hooks/create-auto-scroll.tsx` — 智能 auto-scroll
- `opensrc/opencode/packages/app/src/pages/session/message-timeline.tsx` — 时间线页面
