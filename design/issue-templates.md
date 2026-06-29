---
purpose: "三类 issue 模板（Feature / Bug / Refactor）的设计依据：边界判据、section 结构、AI-consumed 约束、metadata 与两段加载、改进路线。"
include:
  - "三类 issue 的边界判据与共享骨架。"
  - "每个 section 的 Guidance 与 Placeholder（资产文件为内容真源）。"
  - "业界实践映射、Mohist 特化点、使用场景与加载模型。"
  - "改进路线与 issue 拆分。"
exclude:
  - "加载器/registry 的 C# 实现细节（落到代码时再定）。"
  - "Web/CLI 模板选择器的交互设计。"
style:
  - "正文讲设计依据；资产文件（templates/*.md）是内容真源，本文不逐字复制。"
  - "每段 Guidance 遵循 What to write / What NOT to write / How to write。"
---

# Issue Templates

issue template 定义"一个 issue 的 body 长什么样"，与 issue workflow profile（"怎么执行"）正交——前者是写什么，后者是怎么跑。本文定义三类内置模板（Feature / Bug / Refactor）的设计依据。

三类模板已落为资产文件：`packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/templates/{feature,bug,refactor}.md`（frontmatter = metadata，正文 = body）。**本文是设计依据；资产文件是内容真源。**

## Metadata：只有 name 与 description

每个模板的 frontmatter 只有两字段：

| 字段 | 作用 |
|---|---|
| `name` | 展示名（如 Feature） |
| `description` | 一句话描述——**模板选择的唯一依据** |

- **没有 suitableFor / defaults / 显式 id**。文件名即模板 id（`feature.md` → `feature`）。
- **选择由 AI 或人读 description 判断，不是程序匹配**（见"使用场景"）。所以三类模板的 description 必须互相区分清楚，核心是"外部行为变了吗"判据——这也是 `SuitableForMatcher` 不参与模板选型的原因。
- workflow / risk 不进模板 metadata——它们是 issue 自己的 frontmatter（由 create-issue skill 另行推荐）。

## 使用场景：两段、按需、判断在 agent 侧

模板有两个消费者，都是同一种三段式：

| 步骤 | 人（Web 创建对话框） | AI（mohist-create-issue skill） |
|---|---|---|
| ① 拉目录 | `GET /issue-templates`（只 metadata） | `mo issue template list`（只 metadata） |
| ② 判断选哪个 | 人看下拉框 | AI 读 description 判断（**非程序匹配**） |
| ③ 拿完整 | `GET /issue-templates/{id}` → `composeIssueTemplateBody` | `mo issue template get <name>` |

核心：**② 发生在 agent/人脑里，喂给它的是 metadata；body 只在选定后才加载。** 因此加载分两段：

| 阶段 | 触发 | 解析 | 产出 |
|---|---|---|---|
| 发现 | list / 选模板 | **只 frontmatter** | name + description |
| 详情 | get / compose body | frontmatter + **完整 body** | sections |

仿 skill 发现机制（`SkillAssetService.TryReadFrontmatter`）：发现层绝不解析 body。

> **现状缺口**：AI 流目前**绕过**模板系统——`mohist-create-issue` skill 用一个随包静态文件 `references/issue-templates.md` 拼 body，不调 `mo issue template list/get`。模板系统目前只服务 Web 人流。要让三类模板到达 AI，必须把 skill 改用 list/get（见"改进路线"）。

## 三类 issue 与边界判据

Mohist 的 issue 全部要产出代码并 integrate 进仓库（plan→build→check→integrate），没有"只产出决策不产出代码"的 lane。因此只有三类：

| 类 | 含义 | 判据 |
|---|---|---|
| **Feature** | 产品功能开发：新功能或对现有功能的改造迭代 | 外部行为**发生了用户可感知的变化** |
| **Bug** | 修复：功能性 bug（行为不对）或非功能性 bug（性能/可靠性/资源） | 行为偏离正确状态——**被违反的不变量** |
| **Refactor** | 内部质量建设：重构、测试覆盖、优化 | **外部行为不变**，价值是内部的（可维护性/可靠性/性能上限） |

一句话判据：**外部行为变了吗？变了→Feature。没变、但在修"不对的东西"→Bug。没变、本来就对、只是内部改→Refactor。**

关键边界：**对现有功能的改造迭代归 Feature，不归 Refactor**——只要外部行为变了就是 Feature。Refactor 的定义属性是"行为不变"（Fowler：改变内部结构，外部行为不变）。

## Mohist 特化点：body 的主要消费者是 AI planner

这是 Mohist 模板与通用 GitHub issue 模板的根本差异。人在读到"有点慢""代码有点乱"时会追问；plan 阶段的 AI agent 不会，它会把含糊直接吃下去、产出含糊的计划。

因此三类模板共享一条铁律：**每一段都必须是 planner-actionable 的——可复现、可观测、可量化。** 这就是为什么"证据"和"量化"在下面被抬到很高：

- Bug 的症状段禁止"有点慢"，必须给当前测量值 + 目标值。
- Refactor 的完成段禁止"代码更清爽"，必须给结构指标。
- Refactor 的行为契约段必须明确"什么不能变"+ 安全网，否则 AI 极易在重构时顺手改了行为。

## 业界实践映射

| 模板 | 借力的实践 |
|---|---|
| Feature | Agile User Story（Cohn）+ **INVEST** 质量闸 + **Dual-Track Agile / Opportunity-Solution Tree**（Torres，发现轨与交付轨分离）+ 可选 **BDD/Gherkin** 验收。Mohist 三声部 PRD ≈ Dual-Track 的产品化。 |
| Bug | ISTQB 缺陷报告范式（Symptom / 复现 / Expected vs Actual / Severity）；非功能性 bug 借 **DORA + SPACE**——强制量化；DDD 视角：**bug 本质是被违反的不变量**，所以领域段对 Bug 是刚需。 |
| Refactor | Fowler 重构定义（行为不变）+ **characterization test / golden master** 安全网 + 重构的 **Definition of Done**（行为不变 + 结构指标改善）。 |

## 共享骨架

三个模板用**同一套 5 段节奏**，差异只在中段语义——Feature 是"创造新行为"，Bug 是"修正错行为"，Refactor 是"保护旧行为不变"。对应软件工程的三种基本变更动机。

| 段 | Feature | Bug | Refactor |
|---|---|---|---|
| ① 为什么 | **User Voice** | **Symptom & Evidence**（两态） | **Motivation** |
| ② 做成/怎么改 | **Product Shape** | **Fix Shape** | **Change Scope** |
| ③ 领域核心 | **Domain Model**（可选） | **Domain Context**（必需） | **Behavior Contract**（必需） |
| ④ 验收 | **Acceptance Criteria** | **Acceptance Criteria** | **Done When** |
| ⑤ 边界 | **Non-Goals** | **Non-Goals** | **Non-Goals**（抗镀金） |

**对称性的价值**：人 和 AI 都只用记一种结构；模板选择只决定"中段讲什么"。

> Severity / Priority / Risk 不在 body 里重复——它们是 issue 的 frontmatter 字段。body 只放 planner 需要的语义。

## 内联指引注释（每个 section 必带）

每个 section 的 `Placeholder` 字段以一个 HTML 注释 `<!-- ... -->` 开头，作为填写作时的内联指引。`composeIssueTemplateBody` 会把 `## {title}\n{placeholder}` 拼进 body，所以这条注释会出现在新生成 body 的每个 section 顶部。

约定：

- 注释只写**一句可操作的指引**：该写什么、不该写什么。
- **可选 section**：注释里必须给出"何时删除整段"的判定条件（如 Feature 的 Domain Model、Bug 在纯 typo 下的 Domain Context）。
- 注释是给填作者（人或 AI）看的；填好后可保留也可删除，但可选段若不适用**必须整段删除**，不要留空标题。
- 注释对渲染不可见，但 raw text 里 AI planner 可读——它同时承担"提醒 planner 这段该怎么理解"的作用。

---

## Feature

资产文件：`templates/feature.md`。结构：User Voice → Product Shape → Domain Model（可选）→ Acceptance Criteria → Non-Goals。

它取代现有 `mohist/default`（三声部 PRD），并把 **Domain Model 改为可选**（对齐 `mohist-explore` skill，修掉 builtin 强制 vs skill 可选的不一致）：只在需求触及非平凡业务域（不变量、生命周期、跨聚合约束）时写；纯 UI/文案/技术修正省略。Acceptance Criteria 保留 `- [ ]` checklist；复杂交互可选用 Given-When-Then，但不强制。

各 section 的内联指引注释：

| Section | 内联注释 |
|---|---|
| User Voice | `<!-- 第一人称写用户自己的需求：想完成什么、现在卡在哪、怎样算成功。不写实现方案、不用产品术语。 -->` |
| Product Shape | `<!-- PM 视角的产品决策：改完后用户能看到/能做到什么、in/out scope、权衡。不写文件/函数/表结构。 -->` |
| Domain Model（可选） | `<!-- 可选：仅当触及非平凡业务域（不变量/生命周期/跨聚合约束）时保留；纯 UI/文案/技术修正请删除整段。写领域概念与不变量，不写实现方案。 -->` |
| Acceptance Criteria | `<!-- 用户视角可观测、可验证的条件，每条一个 [ ]。不写实现层校验（"单测通过"）。 -->` |
| Non-Goals | `<!-- 显式越界项：读者可能期望但刻意不做的事，让边界更清晰。 -->` |

---

## Bug

与 Feature 同骨架，但前两段从"向往"翻转为"纠正"。Domain Context 对 Bug 是**必需**——Mohist 的 bug 多是模型/不变量缺口（如完成时间未持久化、容量口径不一致），不是错别字，必须讲清"哪条不变量被违反了"。

### ① Symptom & Evidence（两态）

What to write:
- 功能性 bug：从**已知状态**出发的复现步骤 + Expected vs Actual（ISTQB 范式）。写明触发条件、涉及的对象/数据状态。
- 非功能性 bug（性能/可靠性/资源）：**当前测量值 + 目标值 + 测量法**（DORA/SPACE）。例如"epics 列表 200 issue 时首屏 3s，目标 <300ms，用 X 测量"。

What NOT to write:
- "有点慢""偶尔会崩""体验不好"——不可复现、不可量化的主观描述。
- 直接跳到根因或修法（那是 Domain Context / Fix Shape 的事）。
- 把 priority/severity 写进正文（它们是 frontmatter 字段）。

How to write it:
- 先判定功能性还是非功能性，选对应形态。
- 复现步骤假设读者对你的系统零了解，从干净状态写起。
- 非功能性必须给出**数字**和**怎么测出来的**，否则 plan 无法落地。

Placeholder:
```
<!-- 先判定类型再写：功能性=复现步骤+Expected vs Actual；非功能性=当前测量值+目标值+测量法。禁止"有点慢"。 -->
<Functional: repro steps from a known state + Expected vs Actual.
 Non-functional: current measured value + target + how it was measured.>
```

### ② Domain Context

What to write:
- 被违反的不变量 / 被破坏的契约——"系统本应满足 X，实际满足了 Y"。
- 涉及的领域概念与它们应有的关系；必要时引用承载这些概念的代码路径。

What NOT to write:
- 修复方案（那是 Fix Shape）。
- 与本 bug 无关的领域背景。

How to write it:
- 用领域语言，不用实现语言。
- 一句话说清"正确状态应该是什么"，这是 Fix Shape 的依据。

Placeholder:
```
<!-- 必需：说清被违反的不变量（"系统本应 X，实际 Y"）。纯 typo/文案 bug 可缩到一行或删除整段。不写修法。 -->
<The invariant that should hold, and how the current state violates it.>
```

### ③ Fix Shape

What to write:
- 修正方向：把系统从"违反不变量"推回"满足不变量"的产品级决策。
- 触碰边界：动什么、不动什么。

What NOT to write:
- 具体文件/函数/表结构（那是 plan 阶段的事）。
- 顺手修掉的相邻 bug（那是独立 issue）。

How to write it:
- 保持最小：只为恢复不变量而改，不为"更优雅"而改。

Placeholder:
```
<!-- 修正方向 + 触碰边界（动什么、不动什么）。保持最小：只为恢复不变量而改。具体文件/函数留给 plan。 -->
<The correction direction and what is in/out of scope.>
```

### ④ Acceptance Criteria

What to write:
- 功能性：复现路径下行为变为正确，且为可观测条件（`- [ ]`）。
- 非功能性：指标达标（"- [ ] epics 列表 200 issue 首屏 <300ms"）。

What NOT to write:
- 实现层校验（"单测通过""迁移执行"）。

Placeholder:
```
<!-- 功能性=复现路径下行为变正确；非功能性=指标达标（给数字）。每条一个 [ ]，不写实现层校验。 -->
- [ ] <Functional: observable correct behavior; Non-functional: metric meets target>
- [ ] <...>
```

### ⑤ Non-Goals

同 Feature 的 Non-Goals 语义：相邻但不修的 bug、不在本次扩大的边界。Placeholder：
```
<!-- 相邻但不修的 bug、刻意不扩大的边界。 -->
- <Explicit out-of-scope item>
```

---

## Refactor

结构相同，但③④语义**反转**——这是 Refactor 的定义特征。**Behavior Contract 是心脏**：没有"什么不能变 + 安全网"，重构对 AI planner 来说就是赌博。

> 反模式提示：敏捷原教旨派认为"重构该随特性持续做、不该单开 ticket"。对人类团队成立，对 Mohist 不成立——AI 在 plan-build-check-integrate 循环里无法 opportunistic 顺手重构（一旦顺手改无关代码，check 阶段会爆）。所以 Mohist 需要有界、可验收的独立 Refactor 单元。代价由 Motivation（证明债真实存在）+ Non-Goals（抗镀金）对冲。

### ① Motivation

What to write:
- 这笔债的**真实成本**：它挡住了什么、它最近在哪儿引发了痛（"此文件 1191 行，加阶段要动 3 处无关代码，#77/#78/#252 三个 backlog 被它堵着"）。
- 为什么现在动（而不是继续拖）。

What NOT to write:
- "代码我不喜欢""不够优雅"——主观、无成本证据。
- 解决方案（那是 Change Scope）。

How to write it:
- 用**过去的痛**和**未来的阻塞**来证明债的真实性。

Placeholder:
```
<!-- 这笔债的真实成本：挡住了什么、最近在哪引发痛、为什么现在动。禁止"代码我不喜欢"这类主观动机。 -->
<What this debt costs: what it blocks, where it recently hurt, why now.>
```

### ② Change Scope

What to write:
- 重构范围：动哪些部分、用什么手法（拆分 / 提取 / 内联 / 引入间接层等，产品级描述）。

What NOT to write:
- 具体到文件/函数级步骤（那是 plan）。
- 范围外的"顺手清理"。

How to write it:
- 范围要**有界**——能在一句话内说清"这次重构做什么"。

Placeholder:
```
<!-- 重构范围 + 手法（拆分/提取/内联/引入间接层），产品级、有界。具体步骤留给 plan。范围外的顺手清理放 Non-Goals。 -->
<What gets restructured and how, at the product level.>
```

### ③ Behavior Contract（必需）

What to write:
- **必须不变的外部行为**：用户/API/其他模块可观测的契约，逐条列出。
- **安全网**：用什么证明行为没变——现有测试覆盖了哪些、需要补哪些 characterization test / golden master。

What NOT to write:
- "尽量保持兼容"——不可验收。
- 把行为变更当成重构的一部分（那是 Feature 或 Bug）。

How to write it:
- 假设 AI planner 会"顺手优化"行为——这份契约就是它的围栏。
- 没有安全网的契约 = 空话；要么补测试，要么把范围缩小到有测试保护的部分。

Placeholder:
```
<!-- 必需，Refactor 的核心：逐条列出"必须不变的外部行为"+ 安全网（现有/要补的 characterization test）。没有安全网=空话，要么补测试要么缩小范围。 -->
<Behaviors that must NOT change, and the safety net (existing/new tests) proving it.>
```

### ④ Done When

What to write:
- **结构指标改善**：可度量（"文件 <400 行/每个 partial"、"N+1 查询消除"、"该模块覆盖率 80%"、"圈复杂度 <X"）。
- 安全网全绿（行为不变）。

What NOT to write:
- "代码更清爽""更易维护"——不可度量。

How to write it:
- 至少一个**数字**；它和 Motivation 的痛应该一一对应（Motivation 说的痛，Done When 给出解除它的指标）。

Placeholder:
```
<!-- 至少一个可度量的结构指标（数字）+ 安全网全绿。禁止"代码更清爽"。指标要和 Motivation 的痛一一对应。 -->
- [ ] <Measurable structural improvement>
- [ ] <Safety net green: behavior unchanged>
```

### ⑤ Non-Goals（抗镀金）

What to write:
- 范围外的"顺手美化"、不该在本次引入的新抽象、不该重写的健康代码。

How to write it:
- Refactor 最大的风险是镀金（gold-plating）——Non-Goals 在这里比另外两类更重要。

Placeholder:
```
<!-- 抗镀金：范围外的顺手美化、不该引入的新抽象、不该重写的健康代码。 -->
- <Explicit out-of-scope polish or abstraction>
```

---

## 改进路线

### 资产与加载（已定）

- 三类模板是 `Issue/Services/IssueTemplates/templates/*.md` 资产文件（frontmatter + body）。
- 加载器仿 `SkillAssetService`：发现层只读 frontmatter，详情层读 body；csproj `<Content Include=".../templates/*.md" CopyToOutputDirectory="PreserveNewest" />` 随产物拷贝。
- `mohist/default` 被 `feature` 取代；硬编码的 `MohistDefaultIssueTemplate.cs` 删除，内置与自定义统一成"都是数据，只是来源不同（内置文件 vs DB 行）"。

### 改进路线

> 原则：**自动 issue 工作流只承载代码改动**；skill / 文档类非代码内容手动维护，不进 plan→build→check→integrate。

**Issue（自动工作流，仅 server 代码）—— 内置 issue 模板：文件资产 + 两段按需加载 + 简化 metadata**
- 写文件加载器（frontmatter-only 发现 / body-on-detail），复用现有 frontmatter 解析（`PromptFrontmatterParser`）。
- 重构 `IssueTemplateRegistry`：内置来自文件，删 `MohistDefaultIssueTemplate.cs`；`List()` 只返回 metadata，`Get()` 才加载 body；移除 template 的 `SuitableForMatcher.Matches()` 路径（选模板是 AI/人判断）。
- metadata 收敛为 name + description（`IssueTemplateInfo` / `IssueTemplateDetail` 字段裁剪）。
- `mohist/default` → `feature` 迁移。
- 验收：三类模板 `list`/`get` 端到端可用；发现层不解析 body；Web 选择器不破。

**手动完成（不进自动工作流）**
- `mohist-create-issue` skill 改用 `list`/`get` 选型——skill 是非代码内容，手动编写。

> 不在范围：Web 下拉透出 description（可选后续）；自定义模板的写入路径（目前无通路，单开）。
