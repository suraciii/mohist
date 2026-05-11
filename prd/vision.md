# 产品愿景

## 一句话描述

mohist 是一条从想法到代码的流水线。

## 解决什么问题

```
传统方式:
你: "帮我加个搜索功能"
AI: (写代码...)
你: "不对，我要的是..."
AI: (改代码...)
你: "还是不对，需求是这样的..."
AI: (改需求...)
(循环往复，效率低)

mohist:
你: "我想加个搜索"
AI: (面试你：谁用？什么场景？失败怎么办？边界在哪？)
你: 回答问题，理清需求
AI: 产出 proposal → 设计方案 → 拆任务 → 实现 → 审查 → 集成
你: 在关键决策点确认
(需求清晰，一次到位)
```

## 核心价值

- **需求先行** — AI 面试你，把模糊想法变成清晰 proposal
- **产物可审** — 每步都有文件产出，可 diff、可 review、可追溯
- **循环反馈** — PLAN → BUILD → CHECK 循环，CHECK 发现问题自动回到 BUILD
- **健康门控** — 每阶段自动运行编译/测试，失败自动修复
- **自动集成** — 审查通过后自动同步规格、归档、合并到主干
- **随时介入** — gate 点让你确认，你也可以随时追加指令

## Pipeline 模型

```
Explore Mode (Pipeline 外)              Pipeline Mode
┌────────────────────────────┐          ┌──────────────────────────────────────────────────┐
│ AI 面试人类，梳理需求       │          │                                                  │
│ 产出 proposal.md           │──▶ Plan ──▶│ PLAN ──▶ BUILD ──▶ CHECK ──▶ INTEGRATE ──▶ Done│
│ (产品/用户视角)             │          │   ▲                            │        │        │
└────────────────────────────┘          │   └────── 有代码问题 ────────────┘        │        │
                                        │   └────── 集成失败 ───────────────────────┘        │
                                        │                                                  │
                                        │   ⏸ plan gate           ⏸ check gate             │
                                        └──────────────────────────────────────────────────┘

产物:
openspec/changes/{slug}/
  ├── proposal.md        ← Explore
  ├── specs/             ← Plan
  ├── design.md          ← Plan
  ├── tasks.json         ← Plan
  ├── self-review.md     ← Plan (Agent 自审)
  ├── review.md          ← Check (AI 审查)
  └── (archive)          ← Integrate (归档)
```

## 边界

### 现在做的

```
想法 ──▶ Explore ──▶ Plan ──▶ Build ──▶ Check ──▶ Integrate ──▶ Done
                  └── proposal  └─ specs    └─ 代码    └─ 审查报告  └─ 合并+归档
                                 └─ design               └─ merge-check
                                 └─ tasks                └─ approval
                                 └─ self-review
```

### 暂不做的

- 部署到生产
- 监控和告警
- 多人协作

### 未来可能做的

- 从想法到上线
- 团队协作
- 项目管理