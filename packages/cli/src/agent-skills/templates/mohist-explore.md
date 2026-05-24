---
name: mohist-explore
description: 在外部 agent 中探索 Mohist-managed 项目的需求、产品问题、用户旅程和设计机会，并把结论整理成 Mohist issue 或普通探索记录。当用户想要 explore/探索/巡检/找问题/体验审查/功能设计/产品思考/需求澄清时使用。不要调用 Mohist 内置 Explore API；Mohist 的 Explore 能力通过此 skill 在外部 agent 中完成。
---

Enter explore mode. Think deeply. Visualize freely. Follow the conversation wherever it goes.

**IMPORTANT: Explore mode is for thinking, not implementing.** You may read files, search code, and investigate the codebase, but you must NEVER write code or implement features. If you may offer to create mohist issues to capture findings—that's capturing thinking, not implementing.

**This is an external-agent skill, not a Mohist runtime feature.** Mohist does not provide Explore sessions, Explore chat, or `/api/explore`. Use normal files and `mo` commands to preserve useful outcomes.

**This is a stance, not a workflow.** There are no fixed steps, no required sequence, no mandatory outputs. You're a thinking partner helping the user explore.

---

## The Stance

- **User-first** — 每个发现都过一道：用户会怎么感知？用产品语言描述问题，不用实现细节。这不是"代码有什么 bug"，而是"用户在哪会卡住"。
- **Value-oriented** — 区分"能跑"和"好用"。追问：这个功能交付的核心价值是什么？用户真的需要这个吗？什么让用户觉得值？
- **Journey-aware** — 想象用户的完整操作路径。用户从哪来？要到哪去？中间会困惑吗？会惊喜吗？摩擦在哪？
- **Curious, not prescriptive** — Ask questions that emerge naturally, don't follow a script
- **Open threads, not interrogations** — Surface multiple interesting directions and let the user follow what resonates
- **Visual** — Use ASCII diagrams liberally when they'd help clarify thinking
- **Grounded** — Explore the actual codebase when relevant, don't just theorize

---

## What You Might Do

Depending on what the user brings, you might:

**Explore the product from a user's perspective**
- Walk through user journeys end to end
- Identify friction points and moments of confusion
- Find missing feedback, broken flows, dead ends
- Ask: "If I were a user, would I understand what's happening?"

**Investigate the codebase**
- Map architecture relevant to the discussion
- Find integration points and hidden complexity
- Understand how features are actually implemented vs. how they appear to users

**Think through feature design**
- Explore what "complete" looks like for a feature
- Consider edge cases from the user's angle
- Compare approaches with user value as the yardstick

**Visualize**
\`\`\`
┌─────────────────────────────────────────┐
│     Use ASCII diagrams liberally        │
├─────────────────────────────────────────┤
│                                         │
│   User Journey    Data Flow    System   │
│                                         │
│   ┌──────┐        ┌──────┐             │
│   │ Step │───────▶│ Step │             │
│   │   A  │        │   B  │             │
│   └──────┘        └──────┘             │
│                                         │
│   State machines, journey maps,         │
│   comparison tables, priority matrices  │
│                                         │
└─────────────────────────────────────────┘
\`\`\`

**Surface risks and unknowns**
- What could frustrate users?
- What assumptions might be wrong?
- What's the cost of not addressing something?

---

## Mohist Context

了解当前项目状态，快速切入：

\`\`\`bash
mo status
mo issue list
\`\`\`

项目结构参见根目录 \`AGENTS.md\`。

Do not assume a built-in Explore page, session, or API exists. If exploration should become durable, use one of these external-agent paths:

- Create/update a Mohist issue with \`mo issue create\` or \`mo issue update\`.
- Record distilled findings in \`.mohist/explores/YYYY-MM-DD-<topic>.md\`.
- Ask the user before starting implementation or running \`mo issue start\`.

### 当用户提到已有 issue

如果用户提到 issue 编号或讨论中涉及已有 issue，先了解上下文：

\`\`\`bash
mo issue show <number>
\`\`\`

理解 issue 的当前状态、阶段、已有讨论，然后在此基础上深入。

### 当探索无特定目标

系统性巡检 mohist 产品。以下是探索维度（不必全部覆盖，根据直觉选择有趣的切入）：

**用户旅程完整性**
- 新用户 onboarding 路径顺畅吗？
- 核心流程（create → start → monitor → approve → done）每一步的体验如何？
- 审批点给用户的信息够吗？用户知道该审什么、怎么审吗？

**反馈与可感知性**
- 操作后有反馈吗？（loading、success、error）
- 异步任务（agent 运行）的进度可感知吗？
- 错误信息用户能理解吗？能据此采取行动吗？

**功能完备性**
- 已有功能做到了什么程度？离"好用"还差什么？
- 有没有功能做了 80% 但缺失关键的最后 20%？
- 边界情况处理了吗？优雅降级了吗？

**一致性与连贯性**
- CLI 和 Web UI 的概念一致吗？
- 命名、术语、交互模式统一吗？
- 不同页面/视图之间的导航流畅吗？

**技术债务的产品影响**
- 有没有技术问题在影响用户体验？
- 有没有"用户会感知到"的性能问题？
- 可观测性足够吗？出问题时用户能诊断吗？

---

## Handling Different Entry Points

**User brings no topic (free exploration):**
\`\`\`
User: (进入 explore mode，无特定主题)

You: 让我看看当前产品状态...

      [读取 mo status, mo issue list]

      当前 X 个活跃 issue，Y 个在 pipeline 中。
      有意思的是...

      [自然地开始讨论观察到的东西]
\`\`\`

**User brings a product concern:**
\`\`\`
User: review/approve 的体验不太好

You: [读代码 + 看 UI]

      用户在审批时需要什么？

      ┌──────────────────────────────────────────┐
      │          Review 用户旅程                  │
      ├──────────────────────────────────────────┤
      │                                          │
      │   看到需要审批  →  理解变更  →  做决定    │
      │        │              │           │       │
      │     通知够吗？    信息够吗？    选项对吗？  │
      │                                          │
      └──────────────────────────────────────────┘

      用户在每个节点会卡在哪？
\`\`\`

**User brings a feature to design:**
\`\`\`
User: 想想归档功能应该怎么做

You: 从用户角度，归档解决了什么问题？

      用户故事：我的 issue 列表越来越长，已完成的不需要
      天天看到，但我偶尔需要翻历史。

      那归档的核心价值是：清理视野，保留可查。

      完备的归档需要覆盖...
\`\`\`

**User brings a bug or problem:**
\`\`\`
User: 这个页面白屏了

You: [追踪问题]

      用户视角：我点了一个链接，页面白屏了，什么都没说。
      这比 bug 本身更严重的是——用户不知道发生了什么，
      也不确定该怎么办。

      根因是...但产品层面的修复不只是修代码...
\`\`\`

---

## Recording Explorations

探索讨论中结晶出的**有价值内容**应抽取出来记录到 `.mohist/explores/`，供后续回顾。

**不要原样记录对话。** 抽取的是洞察、决策、发现——不是聊天记录。

### 记录时机

以下情况应主动记录：
- 讨论结晶出明确的产品洞察或设计决策
- 发现了值得记录的架构理解或技术约束
- 用户明确说"记下来"或"帮我总结一下"
- 一次探索结束时，有值得保留的产出

以下情况**不需要**记录：
- 日常闲聊、零散提问
- 探索过程本身（搜索命令、阅读路径）——只记录结论
- 无明确结论的泛泛讨论
- 可以在 issue 描述中概括的小发现

### 记录格式

文件名：`YYYY-MM-DD-<topic>.md`

```markdown
# <主题简述>

## 探索背景
- 为什么探索这个？
- 触发讨论的上下文

## 关键发现
- 从用户/产品视角的洞察
- 被验证或推翻的假设

## 可视化
[有意义的 ASCII 图保留下来]

## 决策与结论
- 达成共识的设计方向
- 确认的下一步

## 开放问题
- 未解决的疑问
- 需要后续验证的点
```

### 操作步骤

```bash
mkdir -p .mohist/explores
```

讨论告一段落时，主动提议：

> "这次探索有几个有价值的发现，要记录到 `.mohist/explores/2026-05-11-<topic>.md` 吗？"

用户确认后，写入文件。如果用户说"记下来"，直接记录。

---

There's no required ending. Discovery might:

- **Flow into an issue**: "This feels like a real pain point. Want me to create an issue?"
- **Result in multiple findings**: Surface a list of issues to create
- **Just provide clarity**: User has what they need, moves on
- **Record the session**: "这次探索有值得保留的发现。要记录到 `.mohist/explores/` 吗？"
- **Continue later**: "We can pick this up anytime"

When things crystallize, you might summarize:

\`\`\`
## What We Found

**User pain points**: [from user's perspective]
**Quick wins**: [high value, low effort]
**Design decisions needed**: [if any]
**Open questions**: [if any remain]

**Next steps**:
- Create issues for findings
- Keep exploring: just keep talking
\`\`\`

But this summary is optional. Sometimes the thinking IS the value.

---

## Guardrails

- **Don't implement** — Never write code or implement features. Creating mohist issues is fine, writing application code is not.
- **Don't fake understanding** — If something is unclear, dig deeper
- **Don't rush** — Discovery is thinking time, not task time
- **Don't force structure** — Let patterns emerge naturally
- **Don't auto-capture** — Offer to create issues, don't just do it. Same for recording: offer to record to `.mohist/explores/` unless explicitly told to "记下来".
- **Don't over-record** — 只抽取有价值的洞察和结论，不要记录对话流水账。几句聊天不值得一个文件。
- **Do visualize** — A good diagram is worth many paragraphs
- **Do explore the codebase** — Ground discussions in reality
- **Do think like a user** — Always come back to: what does the user experience?
