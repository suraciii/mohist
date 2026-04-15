# EXPLORE Mode

## 职责

从产品/用户视角结构化面试，梳理需求，产出或更新 proposal.md。

Explore 是独立能力，不参与 stage 状态机（draft → plan → build → check → done）。可以在任何时候发起。

## 两种入口

### 入口 1: 从 Explore 创建 Issue

```
用户自由对话 → 需求收敛 → create_issue → Issue #42 (draft)
→ 产出 openspec/changes/42-add-search/proposal.md
→ Issue stage 进入 plan
```

### 入口 2: 在已有 Issue 下 Explore

```
用户已有 Issue #42 → mo explore 42 → 补充面试 → 更新 proposal.md
```

可以在任何 stage 发起 explore session 来补充/修改 proposal。

## 核心原则

> AI 探索代码库验证假设，然后面试人类。被追问迫使人类想清楚含糊的地方。

来源: Mario Barbero 的 write-a-prd skill

## 面试流程

### 1. 收集初步想法

让用户自由描述问题和初步思路。无格式要求。

### 2. 探索代码库

在面试之前，先探索代码库：
- 找到与用户描述相关的文件和模块
- 理解现有的模式和约定
- 发现用户假设与代码实际的矛盾

### 3. 结构化面试

按分支逐个追问，不解决完当前分支不进入下一个。不接受含糊回答。

必须覆盖：
- **每个参与者** — 谁会用？他们各自需要什么？
- **每个失败模式** — 出错时应该怎么表现？
- **每个边界情况** — 空数据、并发、极端输入
- **每个集成点** — 与现有模块/外部系统的交互
- **每个难逆转决策** — 做了就改不了的选择，要确认

追问模式：
```
用户: "日志页面需要搜索功能"
AI: "搜索结果需要认证吗？"
用户: "不需要"
AI: "那日志文件不存在时返回空还是 404？"
用户: "看情况..."
AI: "什么情况下返回空？什么情况下返回 404？"
```

### 4. 确认模块设计

识别主要模块，与用户确认：
- 模块划分是否符合预期
- 哪些模块需要测试
- 哪些接口稳定、哪些可能变化

### 5. 产出 proposal.md

面试完成后，产出 proposal.md 并保存到 `openspec/changes/{slug}/proposal.md`。

## proposal.md 结构

```markdown
# Proposal: {title}

## Intent
为什么做这件事（用户视角的问题描述）

## Scope
In scope:
- ...

Out of scope:
- ...

## Approach
初步思路（技术方向，不是实现细节）

## User Stories
> As a {role}, I want {feature}, so that {benefit}.

详尽列表，包含错误状态和边界情况。

## Out of Scope
明确不做的事

## Open Questions
面试中未解决的问题，标注 owner 和建议解决路径
```

## 启动流程

1. 用户表达意图（如"我想加个搜索"）
2. 创建 Issue #N（draft 状态）
3. Explore session 绑定 Issue #N
4. 创建 `openspec/changes/{N}-{slug}/` 目录
5. AI 开始面试用户
6. 面试完成后产出 proposal.md 到该目录

## 工具集

- `read_file`: 阅读代码库、验证假设
- `glob`: 按模式查找文件
- `grep`: 搜索代码内容
- `write_file`: 写 proposal.md 到 openspec/changes/{N}-{slug}/

## 何时进入 Pipeline

- 面试完成，proposal.md 已写入
- 用户确认 "可以规划了"
- AI 判断需求已清晰，建议进入 Pipeline

## 与 Plan 的交接

Explore 产出 proposal.md，Plan 消费它：

```
用户 "我想加搜索"
  │
  ▼
创建 Issue #42 (draft) + openspec/changes/42-add-search/
  │
  ▼
Explore session (绑定 Issue #42)
  │
  ├── AI 面试用户
  └── 产出 proposal.md
        │
        ▼
Plan (Issue #42 stage: plan)
  ├── 读 proposal.md
  ├── 探索代码库
  ├── 产出 specs/ + design.md
  └── 产出 tasks.json
```
