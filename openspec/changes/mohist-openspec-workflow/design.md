## Context

### 当前状态

Mohist 当前实现了基于 workflow.yaml 的 Stage 驱动工作流：

```
draft → plan → build → check → done
```

- **Plan 阶段**: Agent 探索需求并生成临时计划
- **Build 阶段**: 一次性执行完整实现，粗粒度
- **Check 阶段**: 运行验证，可循环回 build

**问题**:
1. Plan 阶段的输出是临时的，不作为后续阶段的持久化上下文
2. Build 阶段无法分解为可追踪的子任务
3. 没有结构化的需求规格（Specs）用于验证
4. Agent 无法从之前的任务执行中学习并调整后续任务

### 参考模型: OpenSpec Ralph

OpenSpec Ralph 工作流的核心优势：

```
proposal → design → specs → prd.json → ralph-loop
```

- **结构化产物**: proposal/design/specs 作为持久化上下文
- **任务分解**: prd.json 将需求分解为可执行的 tasks
- **循环执行**: 逐个 task 执行，验证 AC，更新状态
- **学习传递**: progress.txt 记录学习，影响后续迭代

### 约束

1. **保持 workflow.yaml 为主**: 不改变现有的 stage 驱动模型
2. **向后兼容**: 现有 issues 继续工作
3. **渐进式**: 可选择性地为复杂 issues 使用新流程
4. **项目集成**: Specs 应该随代码版本化，便于 code review

## Goals / Non-Goals

**Goals:**

1. Plan 阶段生成结构化的 Change 产物（proposal/design/specs/prd.json）
2. Build 阶段支持 Ralph-style 任务循环执行
3. Task 执行时可访问完整上下文（proposal/design/spec/session-memories）
4. Agent 从任务执行中学习，调整后续任务指令
5. Specs 存储于项目目录，随代码版本化

**Non-Goals:**

- 不替换现有的 workflow.yaml 配置系统
- 不强制所有 issues 使用新流程（简单 issues 可继续使用原流程）
- 不实现多 Change 并行（M4 阶段考虑）
- 不与 OpenSpec CLI 直接集成（概念借用而非工具依赖）

## Decisions

### D1: Change 目录结构

**决策**: 使用 `.mohist-specs/changes/{change-name}/` 存放 Change 产物

**理由**:
- 约定目录名 `.mohist-specs/` 类似于 `.github/`，不显得杂乱
- 位于项目根目录，随 git 版本化
- Code review 时可看到完整设计上下文

**替代方案**: `.mohist/changes/`（隐藏目录）
- 放弃原因：不在 git 内，无法参与 code review

### D2: Specs 生成策略

**决策**: Agent 自动生成，Review 阶段人机结合

**流程**:
```
Plan 阶段: Agent 生成 proposal + design + specs
Review 阶段: Agent 自动审查 → 生成 prd.json → 人工审查
```

**理由**:
- Agent 探索代码库后可以生成合理的初始 specs
- 自动审查确保 proposal/design/specs 一致性
- 人工 review 确保 specs 质量

### D3: 无 progress.txt，使用 session-memories

**决策**: 不采用 OpenSpec 的 progress.txt，改用数据库存储

**存储**:
```
.mohist/issues/{issue-id}/session-memories/{task-id}.json
```

**内容**:
```json
{
  "task_id": "T-001",
  "insights": ["发现的约束/模式"],
  "adjustments": ["对后续任务的建议"],
  "success": true
}
```

**理由**:
- 结构化存储，便于查询和传递
- 不需要人工阅读 progress.txt
- Agent 可以直接读取之前的 memories 作为上下文

### D4: Workflow 阶段扩展

**决策**: 扩展 workflow.yaml，新增 review 和 verify 阶段

**新 workflow**:
```yaml
stages:
  - plan       # 生成 Change 产物
  - review     # Agent审查 + 人工审查
  - build      # Ralph-style 任务执行
  - verify     # 最终验收
```

**理由**:
- review 阶段专门用于验证 specs 和生成 prd.json
- verify 阶段作为最终 gate，确保所有 AC 满足

### D5: 任务执行上下文组装

**决策**: 每个 task 执行时动态组装完整上下文

**上下文组成**:
```
prompt = system_prompt
  + proposal.md (全局背景)
  + design.md (技术约束)
  + specs/{capability}/spec.md#REQ-XXX (当前需求)
  + session-memories/* (历史学习)
  + task.description + acceptanceCriteria
```

**理由**:
- Agent 可以看到完整的设计上下文
- 历史学习影响当前任务执行
- 细粒度的任务追踪

## Risks / Trade-offs

- [Risk] Agent 生成的 specs 质量不稳定 → **缓解**: Review 阶段强制人工审查，可以编辑修改
- [Risk] Context 过长导致 LLM 性能下降 → **缓解**: Session memories 只保留关键洞察，非完整日志
- [Risk] 与现有 workflow 冲突 → **缓解**: 可选功能，通过配置启用，保持向后兼容
- [Risk] Specs 文件过多导致仓库膨胀 → **缓解**: 已完成的 Change 归档到 `.mohist-specs/archive/`
- [Trade-off] Session memories 存储在文件系统 vs 数据库 → 选择文件系统便于查看和调试，但查询效率较低

## Migration Plan

1. **Phase 1**: 实现核心工具（explore_and_generate_specs, review_specs, generate_prd, execute_task, store_learning）
2. **Phase 2**: 扩展 workflow-loader 支持新阶段（review, verify）
3. **Phase 3**: 更新 main-agent 支持 Ralph-style 任务循环
4. **Phase 4**: 添加 CLI 命令 `mo propose` 和配置项启用新流程
5. **Phase 5**: 文档和示例

**回滚策略**: 新流程是可选的，现有 workflow 继续工作。如发现问题，可禁用新功能。

## Open Questions

1. Change 的命名策略：基于 issue 自动命名（如 `issue-42-auth`）还是允许用户指定？
2. Specs 生成时，如何平衡详细程度和 token 限制？
3. Session memories 的清理策略：Change 完成后保留多久？
4. 是否需要支持从现有的 plan 输出自动生成 Change 产物？
