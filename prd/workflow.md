# 工作流

## 核心理念

mohist 是一个从想法到代码的可编排 workflow，基于 DevOps workflow 模型。

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
| tasks.json | Plan | 执行 | 有序 task 列表, type/mode(AFK\|HITL)/output/dependsOn |
| self-review.md | Plan | 自审 | Agent 自我审查 plan 产物（最多 3 次迭代） |
| review.md | Check | 审查 | AI 对实现代码的审查报告 |

产物存放在代码库 `openspec/changes/{slug}/` 下，纳入版本控制。

## 两种交互模式

### Explore Mode（独立能力，不参与 stage 流转）

AI 通过结构化面试从产品/用户视角梳理需求。可以随时发起：
- 从自由对话创建 Issue + 产出 proposal.md
- 在已有 Issue 下补充面试、更新 proposal.md

Stage 状态机：Draft → Plan → Build → Check → Integrate → Done（Explore 不在其中）

### Workflow Mode（PLAN → BUILD → CHECK → INTEGRATE 流程）

```
┌──────────────────────────────────────────────────────────────────────────┐
│                                                                          │
│   ┌──────┐      ┌──────┐      ┌──────┐      ┌──────────┐      ┌─────┐  │
│   │ Plan │ ───▶ │ Build│ ───▶ │ Check│ ──▶ │Integrate │ ──▶ │Done │  │
│   └──────┘      └──────┘      └──────┘      └──────────┘      └─────┘  │
│       ▲                          │                │                      │
│       │                          ▼                ▼                      │
│       │                       Build            Build                    │
│       │                    (驳回/审查失败)   (集成失败)                  │
│       │                                                                  │
│       └───────── 健康检查失败回到 Plan（后备策略） ──────────────────     │
│                                                                          │
│     ⏸ plan gate            (自动推进)        ⏸ check gate              │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
```

## 五个 Stage

| Stage | 职责 | 产出 | Gate |
|-------|------|------|------|
| Explore | 从产品/用户视角结构化面试，梳理需求 | proposal.md | — (Workflow 外) |
| Plan | 基于提案做技术设计、拆分任务 | specs/ + design.md + tasks.json + self-review.md | 用户审批 + 健康检查 |
| Build | 逐个执行任务，写代码，内循环 write→test→fix | 代码变更 | 健康检查 + 全任务完成 |
| Check | AI 代码审查 + merge-ready 检查 | review.md | 用户审批 + merge-ready |
| Integrate | 规格同步 + 归档 + 合并 + 集成后健康检查 | 合并到主干的代码 | 集成后门控 (build+test) |

## 健康检查

默认 workflow 在关键阶段完成后运行通用 `core/script` 检查。命令直接写在 workflow 定义中，项目可以按自己的技术栈替换：

| 阶段 | 默认命令 | 失败处理 |
|------|---------|-------------|
| Plan | `git diff --check` | 注入修复 task 后重试 |
| Build | `git diff --check` | 注入修复 task 后重试 |
| Check | `git diff --check` | 通过后才允许审批 |
| Integrate | `git diff --check` | 失败后 Issue 回到可处理状态 |

此外，Plan 阶段产物缺失和 Check 阶段审查发现问题也会触发 AI 自动修复。

## 你需要做什么

```
1. Explore Mode：AI 面试你，梳理需求，产出 proposal.md
       ↓
2. 进入 Workflow
       ↓
3. Plan gate：确认技术方案和任务拆分
       ↓
4. AI 自动 Build（你可以去做别的事）
       ↓
5. Check gate：审查代码和合并状态
       ↓
6. AI 自动 Integrate：同步规格 + 归档 + 合并
       ↓
7. 完成
```

## AFK vs HITL

每个 task 标记执行模式：

- **AFK** (Away From Keyboard): AI 独立完成，不需要人类介入
- **HITL** (Human-In-The-Loop): 执行到某处需要人类做决策

HITL task 在决策点暂停等人类，AFK task 自动流转。

## 断点续传

Workflow 中断后可以从持久化状态恢复：

- 每个 Stage 的 tasks → checks → auto-fix 进度被持久化
- 服务重启后，Issue 从最后一个检查点恢复执行
- Plan 和 Build 阶段各自维护独立的检查点

## Issue 的角色

Issue 是追踪载体，不承载设计内容：

```
Issue #42: "Add logs page"
  ├── Stage: build
  ├── Status: active
  ├── Change dir: openspec/changes/42-add-logs-page/
  │     ├── proposal.md      ← Explore 产出
  │     ├── specs/           ← Plan 产出
  │     ├── design.md        ← Plan 产出
  │     ├── tasks.json       ← Plan 产出
  │     ├── self-review.md   ← Plan 产出（自审）
  │     ├── review.md        ← Check 产出
  │     └── (archive)        ← Integrate 后移入 archive/
  └── Comments: [...]
```

## Stage Gate

Gate 是 Stage 的属性，不是独立 Stage：

- **Plan gate**: 用户确认技术方案和任务拆分后，AI 开始写代码
- **Build gate**: 健康检查（build 通过）后自动进入 Check
- **Check gate**: 用户确认审查结果后，AI 自动集成
- **Integrate gate**: 集成后健康检查（build+test 通过），完成后标记 Done

## 随时可以介入

你不是旁观者。在任何阶段：

- 查看进度（CLI / Web UI）
- 追加指令
- 要求暂停
- 要求回退
