# #190 剩余问题设计决策分析

## 当前系统架构速览

```
┌──────────────────────────────────────────────────────────────────────────┐
│                           Agent Session (后端)                             │
│  ┌─────────────┐    agent_thought_chunk                                  │
│  │  LLM 推理    │ ──▶ handleSessionUpdate ──▶ onSessionEvent            │
│  │  (thinking)  │         │                                               │
│  └─────────────┘         ▼                                               │
│  ┌─────────────┐    agent_message_chunk                                   │
│  │  LLM 输出    │ ──▶ handleSessionUpdate ──▶ onSessionEvent            │
│  │  (text)      │         │                                               │
│  └─────────────┘         ▼                                               │
│                    SessionStreamLogRepo.insert()                          │
│                         │                                                │
│                         ▼                                                │
│                    session_stream_log (SQLite)                            │
│                    created_at: '2026-05-13T10:23:45.123Z' (毫秒)           │
└──────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                      SessionTranscriptAssembler (后端)                     │
│  assemble(events)                                                        │
│    ├── handleReasoningChunk() ──▶ activeParts.reasoningPart += text       │
│    ├── handleTextChunk()      ──▶ activeParts.textPart += text            │
│    └── handleToolCall()       ──▶ toolPartsById.set()                     │
│                                                                          │
│  输出: turn.assistant = [reasoningPart(12KB), textPart, toolPart...]     │
│        ↑ 一个 giant reasoningPart，不会拆分为交错的小块                    │
└──────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                         前端投影层                                        │
│  SessionPage.tsx                                                         │
│    ├── useQuery(coder-sessions/:id) ──▶ CoderSessionDetail.turns          │
│    ├── useSessionTranscript()                                          │
│    │     ├── 实时: onAgentEvent('coder_text_chunk')                       │
│    │     ├── 实时: onAgentEvent('coder_tool_call')                        │
│    │     └── 没有: onAgentEvent('coder_thought_chunk') ❌                 │
│    └── projectSessionToDisplayTurns()                                    │
│          └── applyReasoningReorder()  ← 面对 giant parts 无效             │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 问题 1: Thinking Inline（核心问题）

### 问题描述
当前 `SessionTranscriptAssembler` 把所有 reasoning chunk 合并成一个 giant part，所有 text chunk 合并成另一个 giant part。`turn.assistant = [reasoning(12KB), text(8KB), tool...]`。前端 `applyReasoningReorder` 无法重排两个 giant parts，导致用户看到的仍然是 "thinking wall" 在最上面。

### 方案对比

#### 方案 A：修改 Assembler — 收到不同类型事件时切换 Part（推荐）

**改动**：在 `handleTextChunk` 中，如果当前有 `activeParts.reasoningPart`，先关闭它（设 completedAt）再创建新的 textPart。反之亦然。

```ts
// session-transcript-service.ts
private handleTextChunk(text: string, createdAt: string): void {
  this.ensureActiveTurn(createdAt);
  
  // 新增：关闭 reasoning part
  if (this.activeParts.reasoningPart) {
    this.activeParts.reasoningPart.completedAt = createdAt;
    this.activeParts.reasoningPart = null;
  }
  
  if (this.activeParts.textPart) {
    this.activeParts.textPart.text += text;
  } else {
    const textPart: TextPart = { ... };
    this.activeParts.textPart = textPart;
    this.currentTurn!.assistant.push(textPart);
  }
}
```

**同理修改 `handleReasoningChunk`**：收到 reasoning 时关闭 textPart。

| 维度 | 评估 |
|------|------|
| **改动范围** | 2 个函数（~30 行），零 schema/API 变化 |
| **优点** | 根本性解决，数据层保留真实交错顺序；前端 T-004 的 reorder 可以对 legacy session 继续工作 |
| **缺点** | 会产生大量小的 reasoning/text part，增加数组长度；极端情况下同一 turn 可能有几十个 part |
| **风险** | 前端渲染性能 — 如果一 turn 有 50 个 part，React re-render 成本？需测试 |
| **工作量** | 小（2-4 小时）|

#### 方案 B：纯前端重排 — 修改 `applyReasoningReorder`

**改动**：不再比较 `sameSecond`，而是采用更激进的启发式：
- 如果一个 turn 只有一个 reasoning block 且在 text 之前，且 reasoning 长度 > text 长度 × 2（暗示"thinking wall"），则将 reasoning block 拆分为小块并尝试与 text 交错。

```ts
function applyReasoningReorder(parts) {
  // 检测 "thinking wall" 模式
  const firstReasoningIdx = parts.findIndex(p => p.type === 'reasoning');
  const firstTextIdx = parts.findIndex(p => p.type === 'text');
  
  if (firstReasoningIdx !== -1 && firstTextIdx !== -1 
      && firstReasoningIdx < firstTextIdx
      && parts[firstReasoningIdx].text.length > parts[firstTextIdx].text.length * 2) {
    // 尝试按语义拆分 reasoning 并穿插到 text 中
    // ... 复杂启发式
  }
}
```

| 维度 | 评估 |
|------|------|
| **改动范围** | 前端投影层一个函数 |
| **优点** | 不碰后端，安全 |
| **缺点** | 本质上是"猜"，无法准确还原真实顺序；复杂启发式容易出错 |
| **风险** | 可能把本来正确的顺序搞乱；维护成本高 |
| **工作量** | 中（1-2 天，需要大量测试覆盖边界）|

#### 方案 C：混合 — 后端轻量标记 + 前端按标记重排

**改动**：
1. 后端 assembler：在每个 part 上增加 `chunkCount` 字段（该 part 由多少个 chunk 合并而成）
2. 前端：如果 `reasoningPart.chunkCount > 1 && textPart.chunkCount > 1`，判定为"未保留交错结构"，触发重排

| 维度 | 评估 |
|------|------|
| **改动范围** | 后端 part 类型 + 前端逻辑 |
| **优点** | 前后端职责清晰 |
| **缺点** | 增加了字段，API 契约变化 |
| **风险** | 引入新字段后 legacy session 没有该字段 |
| **工作量** | 中（1 天）|

### 推荐：方案 A

理由：
1. **根本解决** — 数据层直接保留真实顺序，前端不需要猜
2. **改动极小** — 只在 assembler 两个函数中加 6 行关闭逻辑
3. **兼容 legacy** — 旧 session 的 giant parts 仍然存在，前端 T-004 的 reorder 可以继续为这些旧 session 工作（虽然效果有限，但不退化）
4. **风险可控** — 可以通过测试验证极端 case（比如一 turn 产生 100 个 part 时的性能）

---

## 问题 2: 实时 Thinking（SSE 事件管道）

### 问题描述
`useSessionTranscript.ts` 通过 SSE 实时接收 text 和 tool 事件，但没有 thinking 事件。运行中的 session 前端只显示 text，不显示 thinking。回放时才有 thinking（因为后端 assembler 从 `session_stream_log` 读取了 `agent_thought_chunk`）。

### 当前事件管道

```
后端 agent-session.ts          后端 event-bus.ts            前端 api/events.ts           前端 agent-events.ts
    handleSessionUpdate ──▶    emit('coder_text_chunk')  ──▶  SSE push                 ──▶ onAgentEvent('coder_text_chunk')
    handleSessionUpdate ──▶    emit('coder_tool_call')   ──▶  SSE push                 ──▶ onAgentEvent('coder_tool_call')
    handleSessionUpdate ──▶    ❌ 没有 emit('coder_thought_chunk')                       ──▶ ❌ 前端没有该事件类型
```

### 方案对比

#### 方案 A：完整管道 — 添加 `coder_thought_chunk` 事件（推荐）

**改动**：
1. `session-observers.ts:97`：在 `onTextChunk` 旁边添加 `onThoughtChunk` observer
2. `event-bus.ts`：添加 `coder_thought_chunk: { issueId; projectId; executionId; acpSessionId; text; coderSessionId?; model? }`
3. `api/events.ts`：在 `ALL_EVENT_TYPES` 中添加 `coder_thought_chunk`
4. `web/src/lib/types.ts`：在 `AgentDetailEventMap` 中添加 `coder_thought_chunk`
5. `web/src/lib/agent-events.ts`：在 `AGENT_DETAIL_EVENTS` 中添加 `coder_thought_chunk`
6. `web/src/hooks/useSessionTranscript.ts`：订阅 `coder_thought_chunk`，追加到 turn.assistant

| 维度 | 评估 |
|------|------|
| **改动范围** | 6 个文件（前后端各 3 个），约 60 行 |
| **优点** | 与 `coder_text_chunk` 完全对称，管道清晰；实时和回放体验一致 |
| **缺点** | SSE 流量略微增加（thinking 通常比 text 长）|
| **风险** | 极低，完全复用现有管道模式 |
| **工作量** | 小（半天）|

#### 方案 B：复用 `coder_text_chunk` — 前端区分 text/reasoning

**改动**：
1. 后端：在 `coder_text_chunk` 事件中增加 `isReasoning?: boolean` 字段
2. 前端：`useSessionTranscript.ts` 中根据 `isReasoning` 决定创建 textPart 还是 reasoningPart

| 维度 | 评估 |
|------|------|
| **改动范围** | 后端 1 处 + 前端 1 处 |
| **优点** | 改动最小 |
| **缺点** | 语义不纯净 — thinking 不是 text；与现有 `agent_thought_chunk` 事件类型重复；后端需要判断 chunk 类型 |
| **风险** | 后端 `handleSessionUpdate` 中 `agent_message_chunk` 和 `agent_thought_chunk` 是不同的事件，需要额外映射 |
| **工作量** | 极小（1 小时）|

#### 方案 C：不做实时 thinking

**改动**：无

**理由**：运行中的 session 已经有 "Thinking..." placeholder（`ThinkingPlaceholder` 组件），用户知道 AI 在思考。详细 thinking 内容可以在 session 完成后回放时查看。

| 维度 | 评估 |
|------|------|
| **改动范围** | 零 |
| **优点** | 零工作量 |
| **缺点** | 实时和回放体验不一致；运行中无法看到 AI 的中间思考过程 |
| **风险** | 用户可能在 long-running session 中感到困惑 |
| **工作量** | 零 |

### 推荐：方案 A

理由：
1. **对称性** — 与 `coder_text_chunk` 完全对称的管道，没有额外设计负担
2. **体验一致性** — 实时和回放看到的内容完全一致
3. **工作量极小** — 完全是"依葫芦画瓢"，复用现有模式
4. **风险极低** — 新增事件类型不会影响任何现有逻辑

---

## 问题 3: Diff Viewer（before/after 对比）

### 问题描述
当前 `PatchDiffView` 只显示文件列表（路径、操作类型、加减行数），没有目标布局中的 "before/after 两栏对比"。

### 当前实现
```
PatchDiffView
├── file1.tsx  +23 -5  [expand]
├── file2.ts   +5 -2   [expand]
└── file3.ts   [deleted]
    └── 点击 expand 后显示 raw patch text
```

### 目标体验
```
Diff Viewer
┌─────────────────┬─────────────────┐
│  before         │  after          │
│  import React   │  import React   │
│  -              │  + import {     │
│                 │  +   useState   │
│                 │  + }            │
└─────────────────┴─────────────────┘
```

### 方案对比

#### 方案 A：Unified Diff（单行模式，git diff 风格）（推荐）

**实现**：基于现有 `rawDetail` 中的 patch text，用 `diff` 算法解析出 before/after，渲染 unified diff（带 +/- 前缀的代码块）。

```tsx
function UnifiedDiffView({ patchText }: { patchText: string }) {
  const lines = parseUnifiedDiff(patchText); // [" import React", "-", "+ import { useState }"]
  return (
    <pre>
      {lines.map((line, i) => (
        <div key={i} className={line.startsWith('+') ? 'bg-green-50' : line.startsWith('-') ? 'bg-red-50' : ''}>
          {line}
        </div>
      ))}
    </pre>
  );
}
```

| 维度 | 评估 |
|------|------|
| **改动范围** | 新增一个组件 + diff 解析函数 |
| **优点** | 紧凑、熟悉（GitHub diff 风格）、适合小 patch |
| **缺点** | 对于大 patch 可读性一般 |
| **风险** | patch 格式不统一（apply_patch 用 `***` 格式，edit/write 用 oldString/newString），需要多种解析器 |
| **工作量** | 中（1 天）|

#### 方案 B：Side-by-Side（两栏对比）

**实现**：解析 patch 为 before/after 两个独立代码块，左右分栏显示。

```tsx
function SideBySideDiffView({ before, after }: { before: string; after: string }) {
  return (
    <div className="grid grid-cols-2 gap-0">
      <pre className="bg-red-50/30">{before}</pre>
      <pre className="bg-green-50/30">{after}</pre>
    </div>
  );
}
```

| 维度 | 评估 |
|------|------|
| **改动范围** | 新增组件 + 更复杂的 patch 解析（需要行级 diff）|
| **优点** | 最直观，适合审查 |
| **缺点** | 宽屏要求；小屏幕体验差；实现复杂（需要行级对齐算法）|
| **风险** | 行级对齐算法容易出 bug；移动端几乎不可用 |
| **工作量** | 大（2-3 天）|

#### 方案 C：保持现状，只做增强

**实现**：在现有 `PatchDiffView` 基础上，点击 expand 后直接渲染原始 patch text（带语法高亮），不增加 before/after 对比。

| 维度 | 评估 |
|------|------|
| **改动范围** | 极小 |
| **优点** | 安全 |
| **缺点** | 用户仍需在 patch text 中手动寻找变更点 |
| **工作量** | 极小（1 小时加语法高亮）|

### 推荐：方案 A（Unified Diff）

理由：
1. **最实用** — 大多数 patch 是几行到几十行的修改，unified diff 足够清晰
2. **最熟悉** — 开发者每天都在看 git diff，零学习成本
3. **工作量合理** — 1 天可以完成，包括测试
4. **可扩展** — 后续可以在此基础上增加 side-by-side 切换按钮

---

## 问题 4: Text Pacing（打字机效果）

### 问题描述
T-007 声称实现了 "Streaming assistant text uses visible pacing or typing behavior"，但代码中没有逐字显示逻辑。当前 text chunk 到达时直接追加到 DOM，用户看到的是"跳变"而非"打字"。

### 方案对比

#### 方案 A：前端 useTypewriter Hook（推荐）

**实现**：前端维护一个 "已显示字符数" 状态，用 `setInterval` 或 `requestAnimationFrame` 逐步增加显示长度。

```tsx
function useTypewriter(text: string, speed: number = 20) {
  const [displayed, setDisplayed] = useState(text);
  const prevLenRef = useRef(0);
  
  useEffect(() => {
    if (text.length <= prevLenRef.current) {
      setDisplayed(text);
      return;
    }
    // 新增的部分需要 pacing
    const newPart = text.slice(prevLenRef.current);
    let i = 0;
    const timer = setInterval(() => {
      i++;
      setDisplayed(text.slice(0, prevLenRef.current + i));
      if (i >= newPart.length) clearInterval(timer);
    }, speed);
    return () => clearInterval(timer);
  }, [text]);
  
  return displayed;
}
```

| 维度 | 评估 |
|------|------|
| **改动范围** | 新增 hook + AssistantTextPartView 集成 |
| **优点** | 纯前端，零后端改动；与现有 streaming 机制兼容 |
| **缺点** | 大量文字时 pacing 可能很慢（如 2000 字 × 20ms = 40 秒）|
| **风险** | pacing 期间用户滚动体验；需要可配置/可跳过 |
| **工作量** | 小（半天）|

#### 方案 B：服务端 pacing

**实现**：后端在发送 `coder_text_chunk` 事件时控制发送频率，模拟打字速度。

| 维度 | 评估 |
|------|------|
| **改动范围** | 后端 event bus + SSE 推送层 |
| **优点** | 前端零改动 |
| **缺点** | 所有用户被迫接受相同 pacing；后端复杂度增加；影响 API 响应速度 |
| **风险** |  pacing 期间用户可能感到系统"卡" |
| **工作量** | 中（1 天）|

#### 方案 C：不做 pacing

**改动**：无

**理由**：
- 当前大多数 AI 工具（ChatGPT、Claude Web）都是流式输出，不是逐字 pacing
- "跳变"在流式场景下其实是正常的 — 用户看到的是"实时生成"而非"预先生成好的打字"
- #190 的目标布局中虽然有 "打字机效果"，但这可能是 opencode 的设计，不一定是 mohist 的刚需

| 维度 | 评估 |
|------|------|
| **改动范围** | 零 |
| **优点** | 零工作量 |
| **缺点** | 与 #190 目标布局有差距 |
| **工作量** | 零 |

### 推荐：方案 C（不做 pacing）

理由：
1. **不是真问题** — 当前流式输出已经是行业标配体验，"跳变"不等于"差体验"
2. **引入 pacing 有副作用** — 长文本 pacing 很慢，用户会不耐烦；需要添加"跳过"按钮，增加复杂度
3. **#190 的验收标准可以调整** — "Text pacing" 是 P3（长期优化），且 T-007 的实现描述是 "Streaming assistant text uses visible pacing or typing behavior"，当前 `isStreaming` 状态 + `animate-pulse` 已经提供了"可见的 streaming 行为"
4. **如果真要做** — 建议作为独立 issue，研究更现代的 pacing 方案（如按 token 边界 pacing 而非按字符）

---

## 优先级建议

| 优先级 | 问题 | 方案 | 工作量 | 用户价值 |
|--------|------|------|--------|----------|
| **P0** | Thinking Inline | 方案 A（修改 Assembler） | 2-4 小时 | 🔴 极高 — 解决核心痛点 |
| **P0** | 实时 Thinking | 方案 A（完整管道） | 半天 | 🔴 极高 — 实时/回放一致性 |
| **P1** | Diff Viewer | 方案 A（Unified Diff） | 1 天 | 🟡 高 — 显著提升代码审查体验 |
| **P2** | Text Pacing | 方案 C（不做） | 零 | 🟢 低 — 当前体验已足够 |

---

## 开放问题

1. **Assembler 修改后的性能**：如果一 turn 产生 50+ 个 part，前端渲染性能是否可接受？需要测试。
2. **实时 thinking 的 SSE 流量**：thinking 通常比 text 长（5-20KB），频繁推送是否会影响 SSE 连接稳定性？
3. **Diff Viewer 的 patch 格式兼容性**：apply_patch 用 `***` 格式，edit/write 用 JSON oldString/newString，需要两种解析器还是统一为一种？
