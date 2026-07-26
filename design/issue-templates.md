# Issue Templates

issue template 定义"一个 issue 的 body 长什么样"，与 Issue 选择的 WorkflowProfile
（"怎么执行"）正交——前者是写什么，后者是怎么跑。per-section 写作指令（写什么 /
禁什么 / 可选段删除条件）的内容真源是资产文件
`packages/server/src/Mohist.Server/Issue/Services/IssueTemplates/templates/{feature,bug,refactor}.md`；
本文只讲设计依据，不复制其内容。

## Metadata：只有 name 与 description

每个模板的 frontmatter 只有两字段：

| 字段 | 作用 |
|---|---|
| `name` | 展示名（如 Feature） |
| `description` | 一句话描述——**模板选择的唯一依据** |

- **没有 suitableFor / defaults / 显式 id**。文件名即模板 id（`feature.md` → `feature`）。
- **选择由 AI 或人读 description 判断，不是程序匹配**。所以三类模板的 description 必须互相区分清楚，核心是"外部行为变了吗"判据。
- workflow / risk 不进模板 metadata——它们是 issue 自己的 frontmatter（由 create-issue skill 另行推荐）。

## 使用场景：两段、按需、判断在 agent 侧

模板服务两个消费者——Web 人流与 AI 流（`mohist-create-issue` skill），走同一种三段式：

| 步骤 | 人（Web 创建对话框） | AI（mohist-create-issue skill） |
|---|---|---|
| ① 拉目录 | `GET /issue-templates`（只 metadata） | `mo issue template list`（只 metadata） |
| ② 判断选哪个 | 人看下拉框 | AI 读 description 判断（**非程序匹配**） |
| ③ 拿完整 | `GET /issue-templates/{id}` → body | `mo issue template view <id>` → body，按 body 内注释指令填充各 section |

核心：**② 发生在 agent/人脑里，喂给它的是 metadata；body 只在选定后才加载。** 因此加载分两段，发现层绝不读 body（仿 skill 发现机制）：

| 阶段 | 触发 | 读取 | 产出 |
|---|---|---|---|
| 发现 | list / 选模板 | **只 frontmatter** | name + description |
| 详情 | get / compose body | frontmatter + **完整 raw body** | body 字符串（原样，含 HTML 注释指令） |

## Body 的处理：原样存取，不解析

server 把 body 当**不透明原始字符串**——不解析 section、不提取 guidance、不 strip HTML 注释。这仿 GitHub classic markdown issue template（整个文件正文原样灌进 issue body，含 `<!-- comments -->`）。

- Web 直接用 body 预填编辑框，CLI 原样展示 body，都不 strip 注释——注释在渲染 markdown 时隐藏，对人和 AI 都不构成干扰。
- per-section 写作指令作为 HTML 注释写在 body 里，随 body 原样到达消费者，是给填写者（人或 AI）的内联指引。

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

因此三类模板共享一条铁律：**每一段都必须是 planner-actionable 的——可复现、可观测、可量化。** 这条铁律是通用原则，由 create-issue skill 讲一次；具体的禁止项（如 Bug 症状段禁"有点慢"、Refactor 完成段禁"代码更清爽"）作为 per-section 指令写在各模板的 HTML 注释里。

## 业界实践映射

| 模板 | 借力的实践 |
|---|---|
| Feature | Agile User Story（Cohn）+ **INVEST** 质量闸 + **Dual-Track Agile / Opportunity-Solution Tree**（Torres，发现轨与交付轨分离）+ 可选 **BDD/Gherkin** 验收。 |
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

**对称性的价值**：人和 AI 都只用记一种结构；模板选择只决定"中段讲什么"。

> Severity / Priority / Risk 不在 body 里重复——它们是 issue 的 frontmatter 字段。body 只放 planner 需要的语义。

## 差距脚注

正文是 spec，以下是现状差距，收敛后删：

- custom template CRUD 未建：`ProjectIssueTemplates` 表只有读侧、无写入通路；建 CRUD 时把存储 JSON 从 legacy `{sections:[...]}` 统一为 `{body:"..."}` 并删兼容层。
- Web 下拉尚未透出 description。
