## Context

当前 Mohist 的 workflow 是简单的线性状态机（Draft → Plan → Build → Check → Done），Agent 在每个阶段执行一次任务后就推进到下一阶段。这种模式的局限在于：

1. 没有自动化审查机制，质量依赖单次 Agent 执行
2. 阶段间没有迭代优化，发现问题只能人工介入
3. 用户需要在每个阶段手动检查产出物质量

新的 workflow 需要支持"内循环"模式：在 Plan 和 Review 阶段自动进行多维度审查，发现问题自动修复迭代，直到质量达标才推进到下一阶段。

## Goals / Non-Goals

**Goals:**
- 实现 Plan 阶段的自动化审查和迭代优化（完整性、一致性、可行性、风险）
- 实现 Review 阶段的代码多维度审查（正确性、复杂性、测试覆盖、安全性）
- 支持灵活的配置：审查维度、通过标准、最大迭代次数
- 建立统一的变更产出物管理体系（存储在 `.mohist/changes/`）
- 设计对话式用户审查交互界面

**Non-Goals:**
- 不实现通用的 workflow 引擎（非目标）
- 不支持用户自定义 workflow 阶段（现阶段只支持固定 workflow）
- 不实现复杂的并行审查调度（初期串行即可）

## Decisions

**Decision 1: Workflow 阶段模型**

采用 Explore → Plan (内循环) → Build → Review (内循环) → Done 模型。

- **选择理由**: 与用户需求完全匹配，探索→设计→实现的流程符合人类认知习惯
- **替代方案**: 保持现有的线性模型，添加审查回调 → 拒绝，因为无法很好地支持迭代优化

**Decision 2: 审查通过标准**

由 Agent 灵活判断，而非硬编码规则。

- **选择理由**: 避免过早的规则设计，让 Agent 根据上下文判断
- **实现方式**: 每个 Review Agent 返回 structured output，包含 `passed` (boolean) 和 `reasoning` (string)
- **聚合逻辑**: Workflow Controller 综合所有 Review Agent 的结果，决定是继续、修复还是暂停

**Decision 3: 内循环控制器的职责边界**

Loop Controller 只负责协调，不负责具体审查逻辑。

- **选择理由**: 单一职责，便于测试和扩展
- **职责划分**:
  - Loop Controller: 管理迭代次数、决定下一步动作（继续/修复/暂停）
  - Review Agents: 执行具体审查，返回结构化结果
  - Fix Agents: 根据审查意见执行修复

**Decision 4: 变更产出物存储**

存储在 `.mohist/changes/{issue-number}-{slug}/` 目录下，由 Git 管理。

- **选择理由**: 
  - 与代码仓库一起版本控制，便于追溯
  - 不污染项目根目录
  - 符合 OpenSpec 规范
- **目录结构**:
  ```
  .mohist/changes/42-user-auth/
  ├── proposal.md
  ├── design.md
  ├── specs/
  │   ├── auth-flow.md
  │   └── session-mgmt.md
  └── prd.json
  ```

**Decision 5: Build 阶段的失败处理**

Task 失败时自动重试 + Fix，仍失败则升级。

- **选择理由**: 平衡自动化和可靠性
- **策略**: 
  - 最多 3 次自动重试
  - 失败后尝试自动修复
  - 修复失败则暂停并通知用户

**Decision 6: 用户审查交互方式**

采用对话式（Chat-style），而非代码审查界面。

- **选择理由**: 
  - 实现简单，现阶段足够
  - 与 Explore 阶段的交互方式一致
  - 可以通过迭代增强（后续可添加 diff 查看）

## Risks / Trade-offs

**Risk 1: Agent 审查质量不稳定**

→ **Mitigation**: 
- 多维度审查，不依赖单一 Agent 判断
- 保留人工审批作为最终关卡
- 收集审查质量数据，后续迭代优化

**Risk 2: 内循环无限迭代**

→ **Mitigation**: 
- 设置最大迭代次数（Plan 阶段最多 5 轮，Review 阶段最多 3 轮）
- 达到上限后强制暂停，交给用户处理

**Risk 3: 产出物管理增加 Git 负担**

→ **Mitigation**:
- 产出物主要是 markdown/json，不会太大
- 可以在 `.gitattributes` 中标记为 linguist-generated，减少 review 噪音
- 提供清理命令归档旧变更

**Risk 4: 与现有代码的兼容性问题**

→ **Mitigation**:
- 保留现有的 Stage 枚举作为兼容层
- 新增 Workflow Controller 层，逐步替换旧逻辑
- 使用特性开关控制新旧 workflow 切换（如果需要）

## Migration Plan

1. **Phase 1**: 实现新的 Stage 枚举和 Workflow Controller 框架
2. **Phase 2**: 实现 Plan 阶段的内循环和审查机制
3. **Phase 3**: 实现 Build 阶段的顺序执行
4. **Phase 4**: 实现 Review 阶段的内循环和代码审查
5. **Phase 5**: 集成测试和文档更新

**Rollback**: 保留旧代码路径，通过配置切换回旧 workflow。

## Open Questions

1. Review Agent 的具体数量和维度是否需要可配置？（建议初期固定，后续根据使用数据调整）
2. 是否需要支持 Review 阶段的并行执行？（建议初期串行，简化实现）
3. Fix Agent 是一次性修复所有问题，还是逐个修复？（建议一次性，减少迭代次数）
