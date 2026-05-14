# Issue Templates

Issue body 是 Mohist Plan 阶段的输入，不是完整 PRD、探索记录或技术设计文档。

原则：

- 先写目标产品形态，再保留少量关键领域模型。
- 用户故事只保留压缩后的用户目标；不要写长篇模板化故事。
- 不预设文件、函数、数据库表或具体实现任务；这些属于 Plan 阶段。
- 不把探索过程原样放进正文，只沉淀结论、边界和验收。

Labels: bug, feature, improvement

## Template: product

```markdown
## Problem

[用户可见的问题或机会。说明用户卡在哪里、无法做什么决定、或当前体验为什么不够。]

## User Goal

[可选。用 1-4 条短句描述用户目标；如果 Problem 已经足够清楚，可以省略。]

- [用户想完成的目标 1]
- [用户想完成的目标 2]

## Product Shape

### 1. [目标能力 / 产品形态]

[用户最终会看到或使用什么。描述行为和体验，不写实现方案。]

设计约束：

- [必须成立的产品边界或兼容性要求]
- [不能混淆的用户语义]

### 2. [目标能力 / 产品形态]

[如有多个产品点，继续按形态拆分。]

## Key Domain Model

### [关键概念]

[只写对理解需求必要的领域定义和不变量。]

### [关键概念]

[说明它包含什么、不包含什么，以及不能与哪个概念混用。]

## Acceptance Criteria

- [ ] [可验证的产品行为]
- [ ] [关键边界条件]
- [ ] [默认/既有行为保持兼容]

## Non-Goals

- [明确不做的范围]
- [容易被误扩展但本 issue 不覆盖的范围]
```

Labels: refactor

## Template: refactor

```markdown
## Problem

[当前设计或代码结构带来的维护成本、修改放大、认知负担或回归风险。]

## Refactor Shape

[重构后的目标结构或边界。描述模块职责和行为不变量，不写逐文件任务清单。]

设计约束：

- [对外行为保持不变]
- [要保护的 API / CLI / UI contract]
- [不应顺手改动的业务语义]

## Key Domain Model

### [核心概念 / 边界]

[本次重构要澄清或保护的概念。]

## Acceptance Criteria

- [ ] 重构前后用户可见行为保持一致。
- [ ] 相关测试通过，且覆盖关键行为不变量。
- [ ] 模块边界比当前更清晰，后续修改不需要进入无关区域。

## Non-Goals

- [不改变的业务行为]
- [不顺手实现的新功能]
```

Labels: design

## Template: design

```markdown
## Problem

[为什么需要设计。聚焦用户场景、产品决策或架构边界，而不是实现细节。]

## User Goal

[可选。压缩后的用户目标或决策问题。]

## Product Shape

[目标产品形态或候选方向。设计 issue 可以保留少量开放问题，但应明确要产出什么决策。]

## Key Domain Model

### [关键概念]

[本次设计必须澄清的概念、边界和不变量。]

## Open Questions

- [必须在 Plan/Design 中回答的问题]

## Acceptance Criteria

- [ ] 设计方案覆盖关键用户场景。
- [ ] 明确产品边界、领域概念和非目标。
- [ ] 技术可行性和复杂度可评估。

## Non-Goals

- [本次设计不解决的问题]
```

Labels: docs

## Template: docs

```markdown
## Problem

[现有文档让用户误解、缺失或无法完成什么任务。]

## Product Shape

[目标文档形态：新增/改写哪些用户可见内容，用户读完能做什么。]

## Acceptance Criteria

- [ ] 文档内容准确、完整。
- [ ] 文档与当前产品行为一致。
- [ ] 文档格式和风格与项目一致。

## Non-Goals

- [不涉及的产品或代码变更]
```

Labels: ui-feature, ui-improvement

## Template: ui

```markdown
## Problem

[用户在当前界面中看不到什么、误解什么、无法做什么决定。]

## User Goal

[可选。压缩后的用户目标。]

## Product Shape

[目标界面形态和关键交互。必要时加入简短 ASCII 原型。]

```text
+------------------------------------------+
| [关键布局或状态，不需要完整视觉稿]          |
+------------------------------------------+
```

设计约束：

- [关键状态：loading / empty / error / disabled 等]
- [响应式或可访问性边界]

## Key Domain Model

### [界面中必须表达清楚的概念]

[例如状态标签、用户动作、系统事实之间的区别。]

## Acceptance Criteria

- [ ] 界面表达目标产品形态。
- [ ] 关键交互状态有明确反馈。
- [ ] 桌面和移动端行为符合预期。

## Non-Goals

- [不改动的流程、页面或视觉系统]
```
