# 工作流

## 核心理念

mohist 是一条从想法到代码的流水线，基于 DevOps pipeline 模型。

```
你的想法 ──▶ mohist ──▶ 合格的代码
```

## 核心原则

> AI 产出，人类审查，然后才落地。

mohist 加速产出，审查永远是人类的事。每一步都有可审查的产物，每个产物都能追溯到上游决策。

来源: [Mario Barbero - My AI-Assisted Workflow](https://www.maiobarbero.dev/articles/ai-assisted-workflow/)

## 产物体系

基于 OpenSpec 的 proposal → specs + design → tasks 模型：

```
                    依赖图

                  proposal (root)
                 ┌────┴────┐
                 ▼         ▼
              specs     design
                 └────┬────┘
                      ▼
                    tasks
```

| 产物 | 阶段 | 视角 | 内容 |
|------|------|------|------|
| proposal.md | Explore | 产品/用户 | Intent + Scope + Approach + User Stories + Out of Scope + Open Questions |
| specs/ | Plan | 规格 | Delta specs (ADDED/MODIFIED/REMOVED), GIVEN/WHEN/THEN |
| design.md | Plan | 技术 | Technical Approach + Architecture Decisions + Module Design + Data Flow |
| tasks.json | Plan | 执行 | 有序 task 列表, type/mode(AFK|HITL)/output/dependsOn |

产物存放在代码库 `openspec/changes/{slug}/` 下，纳入版本控制。

## 两种交互模式

### Explore Mode（独立能力，不参与 stage 流转）

AI 通过结构化面试从产品/用户视角梳理需求。可以随时发起：
- 从自由对话创建 Issue + 产出 proposal.md
- 在已有 Issue 下补充面试、更新 proposal.md

Stage 状态机：draft → plan → build → check → done（Explore 不在其中）

### Pipeline Mode（PLAN → BUILD → CHECK 循环）

```
┌──────────────────────────────────────────────────────────────────┐
│                                                                  │
│   ┌──────┐      ┌──────┐      ┌──────┐      ┌──────┐           │
│   │ Plan │ ───▶ │ Build│ ───▶ │ Check│ ──▶ │ Done │           │
│   └──────┘      └──────┘      └──────┘      └──────┘           │
│       ▲                          │                                │
│       └────── 有问题 ─────────────┘                                │
│                                                                  │
│     ⏸ 你确认               (自动推进)        ⏸ 你确认            │
│                                                                  │
└──────────────────────────────────────────────────────────────────┘
```

## 四个 Stage

| Stage | 职责 | 产出 | Gate |
|-------|------|------|------|
| Explore | 从产品/用户视角结构化面试，梳理需求 | proposal.md | — (Pipeline 外) |
| Plan | 基于提案做技术设计、拆分任务 | specs/ + design.md + tasks.json | human（你确认方案和任务拆分） |
| Build | 逐个执行任务，写代码，内循环 write→test→fix | 代码变更 | none（自动进入 CHECK） |
| Check | 6-pass 代码审查 + 跨切面审计 | 审查报告 | human（你确认结果） |

## 你需要做什么

```
1. Explore Mode：AI 面试你，梳理需求，产出 proposal.md
       ↓
2. 进入 Pipeline
       ↓
3. Plan gate：确认技术方案和任务拆分
       ↓
4. AI 自动 Build + Check（你可以去做别的事）
       ↓
5. Check gate：审查结果
       ↓
6. 如果 Check 有问题 → 自动回到 Plan（循环）
```

## AFK vs HITL

每个 task 标记执行模式：

- **AFK** (Away From Keyboard): AI 独立完成，不需要人类介入
- **HITL** (Human-In-The-Loop): 执行到某处需要人类做决策

HITL task 在决策点暂停等人类，AFK task 自动流转。

## Issue 的角色

Issue 是追踪载体，不承载设计内容：

```
Issue #42: "Add logs page"
  ├── Stage: plan
  ├── Status: active
  ├── Change dir: openspec/changes/42-add-logs-page/
  │     ├── proposal.md    ← Explore 产出
  │     ├── specs/         ← Plan 产出
  │     ├── design.md      ← Plan 产出
  │     └── tasks.json     ← Plan 产出
  └── Comments: [...]
```

## Stage Gate

Gate 是 Stage 的属性（`gate_after: none | human`），不是独立 Stage：

- **Plan gate**: 确认技术方案和任务拆分后，AI 开始写代码
- **Build gate**: 无，完成后自动进入 Check
- **Check gate**: 审查通过后完成，有问题回到 Plan

## 随时可以介入

你不是旁观者。在任何阶段：

- 查看进度
- 追加指令
- 要求暂停
- 要求回退
