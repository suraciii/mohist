# 工作流对齐：Mario Barbero 的 AI-Assisted Workflow

**日期**: 2026-04-15
**来源**: [My AI-Assisted workflow](https://www.maiobarbero.dev/articles/ai-assisted-workflow/)
**背景**: 探讨 mohist 工作流如何吸收 Mario Barbero 提出的 7 步 AI 辅助开发流程

---

## Mario 的 7 步流程

```
① Free-form Plan
    ↓ (人类写)
② PRD via write-a-prd
    ↓ (AI 产出，结构化面试)
③ Issues via prd-to-issues
    ↓ (AI 产出，vertical slices, AFK/HITL)
④ Tasks via issues-to-tasks
    ↓ (AI 产出，每个 task = 一次 AI 会话)
⑤ Handoff to Code
    ↓ (AI 执行，每 task 独立会话)
⑥ Code Review (6-pass)
    ↓ (AI 执行，结构化审查)
⑦ Final Audit
    ↓ (AI 执行，跨切面系统性检查)
```

### 核心原则

1. **"AI produces, you review, then it gets created"** — 每一步都是 AI 产出、人类审查
2. **结构化面试** — PRD 阶段 AI 通过面试逼人类想清楚模糊点
3. **Vertical Slices** — 每个 Issue 必须端到端切透所有层，不是水平切片
4. **AFK vs HITL** — 区分"AI 可以独立完成"和"需要人类决策"
5. **一次会话一个 Task** — 每个 task 必须在单次 AI 会话内完成，用干净上下文
6. **Task-as-Prompt** — Task 描述写给 AI 看的指令，不是写给人的备忘录
7. **6-pass 代码审查** — 包括专门的操作顺序检查
8. **跨切面审计** — 全局一致性检查，不仅是单文件 bug

---

## 当前 mohist 流程

```
Explore ──▶ Plan ──▶ Build ──▶ Review ──▶ Done
              ⏸                  ⏸
           人工审批            人工审批
```

### 流程特征

- 3 阶段线性 + 循环（CHECK→PLAN）
- 2 个人工审批门（Plan gate, Review gate）
- Plan 阶段：AI 探索代码库 → AI 生成方案 → AI 自审查 3 轮 → 人工审批
- Build 阶段：RalphExecutor 从 prd.json 读任务 → 逐个执行（ACP session）
- Review 阶段：AI 审查（4 维度：correctness, complexity, test_coverage, security）

---

## 核心差异分析

### 差异 1: 质量引擎

| | Mario | Mohist |
|---|---|---|
| Plan 质量 | AI 面试人类，逼人类想清楚 | AI 自审查（AI 审查自己的输出） |
| 哲学 | "被迫回答自己的问题，暴露含糊其辞" | "AI 能发现自己遗漏的边界情况" |

### 差异 2: 信息压缩粒度

| | Mario | Mohist |
|---|---|---|
| 步骤 | 7 步，每步有验证点 | 3 阶段，2 个验证点 |
| Plan 内部 | PRD → Issue拆分 → Task拆分，三步分开验证 | 一步全出（proposal + design + specs + prd.json） |
| 信息传递 | 逐步压缩，每步人工验证后传递 | 一次性产出，全靠 AI 质量 |

### 差异 3: Task 执行模型

| | Mario | Mohist |
|---|---|---|
| 约束 | 一个 task = 一次 AI 会话，必须能完成 | 无此约束 |
| 上下文 | 每任务干净上下文，无漂移 | 共享上下文（可能漂移） |
| Task 描述 | 写给 AI 的指令（文件、模式、完成定义） | 模板拼接（context-assembler） |

### 差异 4: 审查模型

| | Mario | Mohist |
|---|---|---|
| 范围 | 6 pass + 跨切面审计 | 4 维度审查 |
| 操作顺序 | 专门检查 pass | 未覆盖 |
| 全局一致性 | 独立的 final audit 步骤 | 未覆盖 |

---

## 对齐方向（讨论中）

### 流程映射

Mario 的 7 步映射到 mohist 的 3 阶段：

```
Mario                         Mohist (当前)         Mohist (对齐后?)
────────────────────────      ──────────────        ─────────────────
① Free-form Plan        ──▶   Explore Mode     ──▶  Explore Mode (保持)
② PRD (structured       ──▶   Plan 阶段        ──▶  Plan 阶段 (增强: 面试)
   interview)
③ Issues (vertical      ──▶   Plan 阶段        ──▶  Plan 阶段 (增强: Issue拆分审查)
   slices)
④ Tasks (one per        ──▶   Plan 阶段        ──▶  Plan 阶段 (增强: Task拆分审查)
   session)
⑤ Implement             ──▶   Build 阶段       ──▶  Build 阶段 (增强: 上下文隔离)
⑥ Code Review (6-pass)  ──▶   Review 阶段      ──▶  Review 阶段 (增强: 多pass审查)
⑦ Final Audit           ──▶   (缺失)           ──▶  Review 阶段 (增强: 跨切面审计)
```

---

## Mario 的 5 个 Skill 实现细节

来源: https://github.com/maiobarbero/my-ai-workflow

### Skill 1: write-a-prd

**流程**: 收集计划 → 探索代码库 → 面试用户 → 设计模块 → 写 PRD

关键设计:
- **先探索代码库，再面试用户** — 先验证用户的假设，再追问
- **面试原则**: "Walk down each branch of the design tree, resolving dependencies one by one. Do not move to the next branch until the current one is resolved."
- **必须覆盖**: 每个参与者、每个失败模式、每个边界情况、每个集成点、每个难逆转的决策
- **模块设计**: 寻找"深模块"（封装复杂性 behind 简单接口），与用户确认每个模块
- **PRD 模板**: Problem Statement → Solution → User Stories → Implementation Decisions → Module Design → Testing Decisions → Out of Scope → Open Questions

### Skill 2: prd-to-issues

**流程**: 定位 PRD → 探索代码库 → 拆垂直切片 → 与用户确认 → 写 Issue 文件

关键设计:
- **垂直切片规则**:
  - 每个切片穿透所有层（schema → logic → API → UI → tests）
  - 完成的切片可 demo 或独立验证
  - 宁多薄切片，不搞厚切片
  - 无法独立验证的切片 = 太粗
- **AFK vs HITL**: 每个 issue 标记
- **Quiz 用户**: 问粒度、依赖关系、是否要合并/拆分、HITL 标记是否正确
- **粗度信号**: 超过 2-3 个 user story 或半天工作量 → 可能太粗，要拆
- **Issue 模板**: Type(HITL/AFK) + Blocked by + What to build + How to verify + Acceptance criteria(Given/When/Then) + User stories addressed

### Skill 3: issues-to-tasks

**流程**: 定位 issue → 探索代码库 → 起草任务列表 → 与用户确认 → 写任务文件

关键设计:
- **核心约束**: 每个 task 必须在一次 AI session 内完成
- **Task 类型**: WRITE / TEST / MIGRATE / CONFIG / REVIEW
- **排序原则**: Schema → Logic → API → UI，tests 交错穿插（不是最后才测）
- **Quiz 用户**: 顺序对吗？有 task 太大吗？有 task 太小要合并吗？REVIEW 标记对吗？
- **Task 模板**: Title + Type + Output(完成时存在什么) + Depends on
- **"Do NOT modify the parent issue or the parent PRD"** — 任务不修改上游产物

### Skill 4: code-review

**6 个审查 pass**:
1. **Logic errors** — 死循环、off-by-one、null 解引用、死代码、布尔逻辑错误、竞态条件
2. **Operation ordering** — 副作用在 guard 之前、mutation 在验证之前、资源未释放、audit log 位置错误
3. **Bad practices** — 未验证输入、过宽异常处理、缺少 I/O 错误处理、不安全类型转换
4. **Security** — SQL 注入、硬编码密钥、敏感数据暴露
5. **Magic strings/values** — 内联字面量、重复字面量、应提取为常量/枚举
6. **Pattern improvements** — 硬编码依赖 → DI、条件链 → 策略模式、过程代码 → 命名抽象

**输出格式**: Location + Severity + Pass + Description + Fix，按 severity 分组

### Skill 5: final-audit

**流程**: 定位 feature → 确认范围 → 读所有文件建心智模型 → 审计 → 优先级排序 → 出报告

关键设计:
- **"Do not audit yet. Build a complete mental model first."** — 先读完所有代码再审计
- **4 类审计**:
  - Consistency — 命名、错误处理、相似操作的一致性
  - Security — 输入验证、认证授权、敏感数据、注入风险
  - Logic — 竞态、失败模式、边界条件、与验收标准的匹配
  - Best practices — 重复逻辑、深模块vs浅模块、职责过多、测试是否脆弱
- **4 级 severity**: Critical / High / Medium / Low
- **不自动修改** — "Ask the user which findings they want to act on"

---

## 固化状态

所有决策已固化到:
- `prd/workflow.md` — 产物体系、Stage 职责、AFK/HITL
- `prd/vision.md` — Pipeline 模型图
- `prd/user-interaction.md` — Explore 面试、Gate 交互
- `prd/diagrams/workflow-overview.md` — 产物流转图
- `design/explore.md` — 结构化面试设计 (新文件)
- `design/plan.md` — specs + design + tasks.json
- `design/build.md` — AFK/HITL 模式
- `design/check.md` — 6-pass review + final audit

---

## 开放问题

1. ~~**Explore 面试的具体交互模式**~~ — ✅ 已解决：同步阻塞式。Explore 本身就是人类参与阶段（Pipeline 外），不存在"被拴住"的张力。用现有 ask_user 机制，行为从"偶尔问小问题"变为"系统化面试"（决策 5）
2. ~~**Build 任务隔离**~~ — ✅ 已解决：当前实现天然满足。每个 task 调用 runAcpSession() 创建独立 oneshot session，context-assembler 为每个 task 拼接自包含 prompt。与 Mario 的"Fresh context per task"一致（决策 6）
3. ~~**Review 多层次**~~ — ✅ 已解决：两层审查已固化到 design/check.md。Layer 1 = 6-pass per-task review，Layer 2 = whole-feature 跨切面审计（决策 7）
4. ~~**Plan 内部是否需要子 gate**~~ — ✅ 已解决：单 gate。Plan 是一个 stage，产出 specs + design + tasks.json 后一个 gate 让用户确认全部。不拆子 gate。自审查在内部保证拆分质量（决策 8）

---

## 决策记录

### 决策 1: 各 Stage 的职责边界

```
Explore ──▶ Plan ──▶ Build ──▶ Review ──▶ Done
  │           │        │         │
  │           │        │         │
  需求探索    设计+任务  实现      审查
  产品/用户   技术方案   写代码    验证
  视角        + 任务规划  + 测试   + 审计
```

- **Explore**: 从产品/用户视角出发，结构化面试梳理需求，产出 proposal.md
- **Plan**: 基于proposal做技术设计(specs + design)和任务规划(tasks.json)
- **Build**: 执行任务
- **Review**: 审查验证

### 决策 2: 采纳 AFK/HITL 概念

Mario 提出的 AFK/HITL 分类非常有价值，要纳入 mohist 的任务模型：

- **AFK** (Away From Keyboard): AI 可以独立完成，不需要人类介入，可以自动合并
- **HITL** (Human-In-The-Loop): 执行过程中某处需要人类做决策

这个分类影响：
- Task 描述中需要标记 AFK 或 HITL
- Build 阶段执行时，HITL task 需要在特定点暂停等人类决策
- 用户可以优先关注 HITL task，AFK task 自动流转

### 决策 3: 产物体系对齐 OpenSpec

**核心映射: proposal → specs + design → tasks**

```
OpenSpec concept    Mario equivalent        mohist Stage
─────────────────   ──────────────────      ────────────

proposal            ① Free-form Plan        Explore
                    ② PRD (面试)
                    (Intent/Scope/Approach   (结构化面试,
                     User Stories/Scope)      产品视角梳理)

specs + design      ② PRD (Impl Decisions   Plan
                    / Module Design)          (技术设计)

tasks               ③ Issues                Plan
                    ④ Tasks
                    (AFK/HITL, Type,          (任务拆分 +
                     Output, Depends on)       AFK/HITL标记)
```

**各产物职责:**

| 产物 | 阶段 | 视角 | 内容 |
|------|------|------|------|
| proposal.md | Explore | 产品/用户 | Intent + Scope + Approach + User Stories + Out of Scope + Open Questions |
| specs/ | Plan | 规格 | Delta specs (ADDED/MODIFIED/REMOVED), GIVEN/WHEN/THEN |
| design.md | Plan | 技术 | Technical Approach + Architecture Decisions + Module Design + Data Flow |
| tasks.json | Plan | 执行 | 有序 task 列表, type/mode(AFK\|HITL)/output/dependsOn |

**Issue 是追踪载体，不承载内容:**

```
Issue #42: "Add logs page"
  ├── Stage: plan
  ├── Status: active
  ├── Change dir: openspec/changes/42-add-logs-page/   ← 代码库目录下
  │     ├── proposal.md    ← Explore 产出
  │     ├── specs/         ← Plan 产出
  │     ├── design.md      ← Plan 产出
  │     └── tasks.json     ← Plan 产出
  └── Comments: [...]
```

**关键设计决策:**
- 变更产物存放在代码库目录 `openspec/changes/` 下，纳入版本控制，可 review
- 不再使用 `.mohist/changes/`（藏在 home 目录，不可见）
- proposal.md 归 Explore（需求/产品视角），design.md + specs/ + tasks.json 归 Plan（技术视角）
- Issue 只追踪状态流转，不承载设计内容

### 决策 4: 任务清单命名 — tasks.json 而非 prd.json

在 Mario 的工作流中，流程是 PRD → Issues → Tasks，三者是不同的产物：

```
PRD (产品需求文档)          ← 需求层面：问题、用户故事、范围
  ↓
Issues (垂直切片)           ← 交付层面：端到端可验证的切片
  ↓
Tasks (执行任务)            ← 执行层面：一次 AI 会话的工作单元
```

当前 mohist 用 `prd.json` 存放任务清单，但 `prd` 这个名字暗示的是产品需求文档，不是执行任务。

**决策**: 将任务清单文件从 `prd.json` 改名为 `tasks.json`，使命名与职责对齐。

影响范围：
- `openspec/changes/{slug}/prd.json` → `tasks.json`
- `context-assembler.ts` 中读取 prd.json 的逻辑
- `ralph-executor.ts` 中读取任务列表的逻辑
- `main-agent.ts` 中 `read_prd` 工具 → `read_tasks`
- 相关的 types 定义 (`PrdTask` → `Task`)

### 决策 5: Explore 面试交互模式 — 同步阻塞

Explore 在 Pipeline 之外，本身就是人类参与阶段。面试是同步阻塞式的：用户发起 session，AI 问，用户答，多轮，直到产出 proposal.md。不需要新的交互机制，用现有 ask_user 工具即可。

### 决策 6: Build 任务上下文隔离 — 当前实现已满足

每个 task 调用 runAcpSession() 创建独立 ACP oneshot session，context-assembler 为每个 task 拼接自包含 prompt（proposal + design + specs + task 描述）。与 Mario 的"Fresh context per task"原则一致。

### 决策 7: Review 两层审查

已固化到 design/check.md。Layer 1 = 6-pass per-task/per-diff 代码审查（逻辑错误→操作顺序→坏实践→安全→魔法值→模式改进），Layer 2 = whole-feature 跨切面审计（一致性/安全/逻辑/最佳实践）。关键原则：先读完所有代码建心智模型，再审计。

### 决策 8: Plan 单 gate，不拆子 gate

Plan 是一个 stage，产出 specs + design + tasks.json 后一个 gate 让用户确认全部。自审查在内部保证拆分质量（验证 task 粒度、依赖关系、AFK/HITL 标记）。不拆为两个 gate。

### 决策 9: Issue 先创建，changes 目录跟 Issue 走

流程：用户说"我想加搜索" → 创建 Issue #42 → Explore session 绑定 Issue #42 → 产出 openspec/changes/42-add-search/proposal.md → Issue stage 进入 plan。Issue 是追踪载体，changes 目录用 issue number + slug 命名。

### 决策 10: Explore 是独立能力，不是 stage

Explore 不参与 stage 状态机（draft → plan → build → check → done）。它是可以随时发起的能力：
- 入口 1: 从 Explore 创建 Issue（用户自由对话 → 需求收敛 → create_issue）
- 入口 2: 在已有 Issue 下 Explore（mo explore 42 → 补充面试 → 更新 proposal.md）

可以在任何 stage 发起 explore session 来补充/修改 proposal。

### 决策 11: 自审查是人类审查的预演

AI self-review 不是可选步骤，而是必要的预处理。AI 按照人类审查的同样标准先审一遍（方案完整性 + 拆分质量），把明显问题修掉，降低人类在 gate 处的审查成本。人类拿到的是已经过一轮筛选的产物。

### 决策 12: Gate 模型 — 展示报告，等用户批准或反馈

Gate 不需要复杂的多轮对话。AI 产出方案 + 自审查报告，展示给用户，等待用户的批准或反馈。

```
AI 产出 specs + design + tasks.json → AI 自审查 → 出报告 → 展示给用户
  → 用户批准 → 进入 Build
  → 用户给反馈 → AI 修改 → 重新自审查 → 再展示
```

### 决策 13: Task 执行机制 + tasks.json 结构化读取

"一次会话一个 task"不是约束而是机制——每个 ACP session 通过指令指定执行哪个 task，天然就是一对一。

tasks.json 是结构化文件，mohist agent 通过 tool 来获取当前应该实施的任务（按 order、依赖关系、状态过滤）。不需要 coder agent 自己解析 tasks.json。

### 决策 14: Check 阶段的核心是对抗性审查

Check 的重点不是多个角色分工，而是对抗性视角——专门去检验前面 agent 的产出。审查者是"对手"，不是"同事"。审查维度（6-pass + 跨切面审计）是审查的侧重点，不是角色分工。M1/M2 单 agent 执行全部审查维度即可。

---

## 工程影响范围

| 组件 | 变更 | 大小 |
|------|------|------|
| ExploreAgent | system prompt 重写（结构化面试），新增 write_file 工具，产出 proposal.md 到 openspec/changes/ | 大 |
| PlannerAgent | 从一步全出 JSON 改为读 proposal → 产出 specs + design + tasks.json，自审查改为方案完整性+拆分质量 | 大 |
| ReviewerAgent | 从 4 维度改为 6-pass + 跨切面审计 | 中 |
| WorkflowController | 产物路径从 .mohist/changes/ 改为 openspec/changes/ | 中 |
| ChangeArtifactsManager | 适配新目录结构和 tasks.json 格式 | 中 |
| context-assembler | 读取 tasks.json（非 prd.json），新增 type/mode/output 字段拼装 | 中 |
| ralph-executor | 读取 tasks.json，通过 tool 获取 next task | 中 |
| main-agent | read_prd → read_tasks，gate 行为改为展示报告+等反馈 | 小 |
| types | PrdTask → Task，新增 type/mode/output 字段 | 小 |
