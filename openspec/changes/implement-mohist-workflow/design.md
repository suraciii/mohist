## Context

当前 Mohist 的 workflow 是简单的线性状态机（Draft → Plan → Build → Check → Done），Agent 在每个阶段执行一次任务后就推进到下一阶段。这种模式的局限在于：

1. 没有自动化审查机制，质量依赖单次 Agent 执行
2. 阶段间没有迭代优化，发现问题只能人工介入
3. 用户需要在每个阶段手动检查产出物质量

新的 workflow 简化为 3 个核心 Agent，通过 Prompt 自定义行为，Agent 自主决定是否需要迭代优化。

## Goals / Non-Goals

**Goals:**
- 实现 Plan 阶段的设计和审查（Planner Agent，Prompt 自定义审查标准）
- 实现 Build 阶段的顺序任务执行（Coder Agent）
- 实现 Review 阶段的代码审查（Reviewer Agent，Prompt 自定义审查维度）
- 建立统一的变更产出物管理体系
- 设计对话式用户审查交互界面

**Non-Goals:**
- 不实现复杂的内循环编排（LoopController、MultiAgentReview）
- 不硬编码具体的审查维度和规则
- 不支持用户自定义 workflow 阶段
- 不实现通用的 workflow 引擎

## Decisions

### Decision 1: 简化的 Agent 模型

采用 3 个核心 Agent：Planner、Coder、Reviewer。具体的审查维度和行为通过 Prompt 自定义。

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                    简化后的 Mohist Workflow                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│    用户         Planner                      Coder           Reviewer      │
│   介入│         Agent                        Agent           Agent         │
│      ▼          │                            │               │            │
│  ┌──────────┐  ┌──────────┐   ┌──────────┐  ┌──────────┐   ┌──────────┐   │
│  │ Explore  │──│   Plan   │──▶│  Build   │──│  Review  │──▶│   Done   │   │
│  │  (探索)  │  │  (设计)  │   │  (构建)  │  │  (审查)  │   │ (完成)   │   │
│  └──────────┘  └────┬─────┘   └──────────┘  └────┬─────┘   └──────────┘   │
│                     │                            │                         │
│                用户审批点                    用户审批点                     │
│                                                                             │
│  Prompt 定义：                                                               │
│  - Planner: 设计规范、审查标准                                               │
│  - Reviewer: 审查维度（正确性、复杂度、安全等）                              │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Planner Agent**: 生成设计方案，自我审查，Prompt 定义审查标准
- **Coder Agent**: 执行具体任务，Prompt 定义编码规范
- **Reviewer Agent**: 审查代码质量，Prompt 定义审查维度

### Decision 2: Agent 自主迭代

**原方案**: LoopController 强制管理迭代（最多 5 轮）
**新方案**: Agent 自主决定是否需要迭代

**理由**:
- 简化架构，移除 LoopController 编排层
- Agent 可以根据上下文智能判断是否需要重新设计/修复
- 用户始终有最终审批权

**实现**:
```
Plan 阶段:
1. Planner Agent 生成设计方案
2. Planner Agent 自我审查（根据 Prompt 中的审查标准）
3. IF 发现问题 → 自我修正并重新生成
4. IF 通过 → 提交给用户审批
5. 用户审批通过 → 进入 Build 阶段

Review 阶段:
1. Reviewer Agent 审查代码
2. IF 发现问题 → 可以建议修复或交给用户决定
3. IF 通过 → 提交给用户审批
4. 用户审批通过 → 进入 Done 阶段
```

### Decision 3: 移除 MultiAgentReview 编排层

**原方案**: 4 个专门的 Review Agents + MultiAgentReview 编排器
**新方案**: 1 个 Reviewer Agent，具体的审查维度在 Prompt 中定义

**Prompt 示例**:
```
You are a Reviewer Agent. Review the code according to these dimensions:
1. Correctness - check for logic errors, type safety, lint issues
2. Complexity - check cyclomatic complexity, function length
3. Test Coverage - verify tests exist and pass
4. Security - check for common vulnerabilities

For each dimension, provide:
- passed (boolean)
- reasoning (string)
- issues (array) if any

Overall: return passed only if all dimensions pass.
```

### Decision 4: Planner Agent 职责

Planner Agent 负责：
1. 探索 codebase，理解现有架构
2. 生成设计方案（proposal.md, design.md, specs/）
3. 生成 prd.json（任务规划）
4. 自我审查设计质量
5. 根据审查反馈自我修正

**Prompt 示例**:
```
You are a Planner Agent. Create a design for this issue:
- Issue: {title}
- Description: {body}

Steps:
1. Explore the codebase to understand existing patterns
2. Create design documents in .mohist/changes/{number}-{slug}/
3. Self-review your design for:
   - Completeness: all requirements covered?
   - Consistency: aligns with existing patterns?
   - Feasibility: can be implemented?
   - Risks: potential issues identified?
4. If issues found, fix them
5. Generate prd.json with tasks
```

### Decision 5: Coder Agent 职责

Coder Agent 通过 `spawn_coder` 调用，负责：
1. 读取 prd.json 中的 task
2. 理解 task 要求
3. 实现代码
4. 运行测试/验证

Build 阶段顺序执行：
```
FOR each task in prd.json.tasks:
  1. Call spawn_coder with task details
  2. IF success → mark task as done
  3. IF failure:
     - Retry up to 3 times
     - IF still failing → pause and ask user
```

### Decision 6: Check 阶段并入 Review

Check 阶段的功能（测试执行、lint、typecheck）作为 Reviewer Agent 的审查维度之一，通过 Prompt 定义。

### Decision 7: 变更产出物存储

存储在 `.mohist/changes/{issue-number}-{slug}/` 目录下，由 Git 管理。

目录结构：
```
.mohist/changes/42-user-auth/
├── proposal.md
├── design.md
├── specs/
│   ├── auth-flow.md
│   └── session-mgmt.md
└── prd.json
```

## Risks / Trade-offs

### Risk 1: Agent 自我审查质量不稳定

→ **Mitigation**: 
- 详细的 Prompt 模板，包含审查清单
- 保留人工审批作为最终关卡
- Prompt 迭代优化

### Risk 2: Agent 可能无限迭代

→ **Mitigation**: 
- 在 Prompt 中设置最大迭代次数建议
- 用户可以随时介入
- 长时间运行检测和超时

### Risk 3: Prompt 复杂度

→ **Mitigation**: 
- 提供默认的 Prompt 模板
- Prompt 可配置但非必须
- 良好的默认值覆盖 80% 场景

### Risk 4: 与现有代码的兼容性

→ **Mitigation**: 
- 保留现有的 Stage 枚举作为兼容层
- 新增 Workflow Controller 层，逐步替换旧逻辑
- 使用特性开关控制新旧 workflow 切换

## Migration Plan

### Phase 1: 基础框架
- 更新 Stage 枚举
- 创建 WorkflowController
- 实现 ChangeArtifactsManager

### Phase 2: Planner Agent
- 实现 Planner Agent 基础框架
- 提供默认 Prompt 模板
- 集成到 Plan 阶段

### Phase 3: Build 阶段
- 实现顺序 task 执行
- 集成 spawn_coder
- 实现失败重试

### Phase 4: Reviewer Agent
- 实现 Reviewer Agent 基础框架
- 提供默认 Prompt 模板
- 集成到 Review 阶段

### Phase 5: 集成和测试
- 集成到 Main Agent
- 端到端测试
- 文档更新

**Rollback**: 保留旧代码路径，通过配置切换回旧 workflow。

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Mohist Workflow 架构                                 │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │                        Main Agent                                   │   │
│  │  ┌───────────────────────────────────────────────────────────────┐  │   │
│  │  │ Tool Registry                                                   │  │   │
│  │  │  • spawn_coder    • advance_stage    • add_comment             │  │   │
│  │  │  • ask_user       • read_workflow    • get_issue               │  │   │
│  │  │  • read_prd       • update_task_status • store_learning        │  │   │
│  │  └───────────────────────────────────────────────────────────────┘  │   │
│  │                                                                     │   │
│  │  ┌───────────────────────────────────────────────────────────────┐  │   │
│  │  │ Session Manager                                                 │  │   │
│  │  │  • 管理 Agent 会话状态                                          │  │   │
│  │  │  • 支持断点恢复                                                 │  │   │
│  │  └───────────────────────────────────────────────────────────────┘  │   │
│  │                              │                                      │   │
│  └──────────────────────────────┼──────────────────────────────────────┘   │
│                                 │                                           │
│                                 ▼                                           │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │                     WorkflowController                              │   │
│  │                                                                     │   │
│  │  executeStage(issue, stage):                                        │   │
│  │    ├─ Plan ──────▶ Planner Agent (Prompt 定义审查标准)              │   │
│  │    ├─ Build ─────▶ Sequential Task Executor + Coder Agent           │   │
│  │    └─ Review ────▶ Reviewer Agent (Prompt 定义审查维度)             │   │
│  │                                                                     │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐   │
│  │                    Storage Layer                                    │   │
│  │  ┌─────────────┐  ┌──────────────────┐  ┌──────────────────────┐   │   │
│  │  │ SQLite      │  │ .mohist/changes/ │  │ Git Repository       │   │   │
│  │  │ (issues)    │  │ (artifacts)      │  │ (version control)    │   │   │
│  │  └─────────────┘  └──────────────────┘  └──────────────────────┘   │   │
│  └─────────────────────────────────────────────────────────────────────┘   │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Agent Prompt Templates

### Planner Agent Default Prompt

```yaml
role: planner
name: Planner Agent
description: Creates design artifacts and self-reviews them

steps:
  - Explore codebase to understand existing patterns
  - Read issue details and requirements
  - Create design documents:
      - proposal.md: Problem and solution overview
      - design.md: Technical design with decisions
      - specs/: Capability-based specifications
      - prd.json: Task breakdown
  - Self-review design:
      completeness: Are all requirements covered?
      consistency: Does it align with existing patterns?
      feasibility: Can it be implemented?
      risks: What could go wrong?
  - If issues found, fix and re-review
  - Submit for user approval

output_format:
  artifacts:
    - proposal.md
    - design.md
    - specs/*.md
    - prd.json
  review_result:
    passed: boolean
    issues: array
```

### Reviewer Agent Default Prompt

```yaml
role: reviewer
name: Reviewer Agent
description: Reviews code quality and provides feedback

dimensions:
  correctness:
    - Logic errors
    - Type safety
    - Lint violations
  complexity:
    - Function length
    - Cyclomatic complexity
    - Code duplication
  test_coverage:
    - Tests exist
    - Tests pass
    - Coverage adequate
  security:
    - Common vulnerabilities
    - Input validation
    - Injection risks

steps:
  - Review all changed files
  - For each dimension, provide:
      passed: boolean
      reasoning: string
      issues: array
  - Overall: pass only if all dimensions pass
  - If failed, suggest specific fixes

output_format:
  passed: boolean
  dimensions: array
  overall_reasoning: string
  fix_suggestions: array
```

## File Structure

```
packages/cli/src/
├── types/index.ts              # Stage enum 更新
├── workflow/
│   ├── index.ts                # WorkflowController 导出
│   └── workflow-controller.ts  # 阶段管理
├── agents/
│   ├── main-agent.ts           # 更新：调用 WorkflowController
│   ├── planner-agent.ts        # Planner Agent 实现
│   └── reviewer-agent.ts       # Reviewer Agent 实现
│   └── prompts/                # Prompt 模板
│       ├── planner-default.yaml
│       └── reviewer-default.yaml
├── artifacts/
│   └── change-artifacts-manager.ts
└── tools/
    └── spawn-coder.ts          # Coder Agent 调用
```
