# 工作流

## 核心理念

mohist 是一条从想法到代码的流水线，基于 DevOps pipeline 模型。

```
你的想法 ──▶ mohist ──▶ 合格的代码
```

## 两种交互模式

### Explore Mode（Pipeline 外）

用户与 mohist 自由对话，梳理需求、澄清模糊点、做取舍。产出清晰的 Issue 后进入 Pipeline。

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

## 三个 Stage

| Stage | 职责 | 谁在做 | Gate |
|-------|------|--------|------|
| PLAN | 基于明确需求设计方案、分解任务 | Agent | human（你确认方案） |
| BUILD | 执行任务、写代码、内循环 write→test→fix | Agent | none（自动进入 CHECK） |
| CHECK | 测试、代码审查、对比需求 | Agent | human（你确认结果） |

## 你需要做什么

```
1. Explore Mode：和 AI 对话，把需求聊清楚
       ↓
2. 创建 Issue（draft）
       ↓
3. 启动 Issue，进入 Pipeline
       ↓
4. PLAN gate：确认方案
       ↓
5. AI 自动 BUILD + CHECK（你可以去做别的事）
       ↓
6. CHECK gate：审查结果
       ↓
7. 如果 CHECK 有问题 → 自动回到 PLAN（循环）
```

## Stage Gate

Gate 是 Stage 的属性（`gate_after: none | human`），不是独立 Stage：

- **PLAN gate**：方案确认后，AI 开始写代码
- **BUILD gate**：无，完成后自动进入 CHECK
- **CHECK gate**：审查通过后完成，有问题回到 PLAN

## 随时可以介入

你不是旁观者。在任何阶段：

- 查看进度
- 追加指令
- 要求暂停
- 要求回退
