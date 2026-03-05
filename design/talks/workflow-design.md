# 工作流设计讨论

**状态**: 演进中  
**创建时间**: 2026-03-06  
**关联文档**: [workflow.md](../workflow.md)

---

## 1. 核心设计决策

### 1.1 Stage 命名约定

**问题**: 如何命名工作流阶段？

**方案对比**:

| 方案 | 示例 | 优点 | 缺点 |
|------|------|------|------|
| 状态描述 | exploration, design, implement | 直观 | 不明确驱动什么动作 |
| 动作驱动 | ready-for-design, ready-for-implement | 明确下一步 | 稍长 |

**决策**: 采用 `ready-for-*` 格式

**理由**:
- 明确驱动下一步动作
- 自动化系统可以根据标签触发相应操作
- 避免"当前是什么状态"的歧义，改为"下一步该做什么"

**应用**:
- `ready-for-refinement`: 触发 OpenClaw 与用户对话
- `ready-for-design`: 触发设计文档生成
- `ready-for-implement`: 触发 Ralph CLI 执行
- `ready-for-review`: 触发 Agent Review
- `ready-for-merge`: 触发 PR 合并

---

### 1.2 单 PR vs 多 PR 设计

**问题**: 设计文档和实现代码应该放在同一个 PR 还是分开？

**方案对比**:

| 方案 | 流程 | 优点 | 缺点 |
|------|------|------|------|
| **多 PR** | Design PR → 合并 → Implementation PR | 设计审查独立 | PR 数量多，历史分散 |
| **单 PR** | PR #1: design.md → 添加实现 commits | 历史连续，一站式审查 | PR 可能变大 |

**决策**: 单 PR 设计

**理由**:
1. **历史可追溯**: 所有演进过程在 PR 的 commits 中可见
2. **一站式审查**: 用户在同一个 PR 中审查设计和实现
3. **回退连续性**: re-evaluate 时不需要关闭 PR 重新创建
4. **简化流程**: 一个 Issue 对应一个 PR

**PR #1 演进示例**:
```
Commit 1: "design: add architecture and specs"
Commit 2: "feat: implement user authentication"
Commit 3: "feat: implement article CRUD"
Commit 4: "fix: address review feedback"
```

---

### 1.3 回退机制设计

**问题**: 如何处理用户对设计或实现不满意的情况？

**方案对比**:

| 方案 | 流程 | 优点 | 缺点 |
|------|------|------|------|
| **关闭重建** | 关闭 PR → 创建新 PR | 干净 | 历史断裂 |
| **同 PR 回退** | PR #1 保持打开 → 添加新 commits | 历史连续 | PR 可能混乱 |

**决策**: 同 PR 回退

**流程**:
1. 用户在 Issue 评论"重新设计"或"这不是想要的"
2. 标签变为 `re-evaluate`
3. 用户选择:
   - **A. 重新设计** → 回到 `ready-for-design`
     - PR #1 保持打开
     - 添加新 commit 更新设计
     - 废弃旧的实现 commits（git revert 或标记为 deprecated）
   - **B. 调整需求** → 回到 `ready-for-refinement`
     - 更新 Issue Body
     - 重新设计后再实现

**优势**:
- 完整历史在同一 PR 中可见
- 不需要重新创建 PR
- 用户可以对比新旧设计

---

## 2. 与原始 prd.md 对比

### prd.md 的设计

- **7 个阶段**: Exploration → Refinement → Design → Implementation → Review → Re-evaluation → Done
- **双向通信**: Issue Comments 作为命令总线
- **三层 Review**: User Review / Agent Review / Auto Verification
- **Re-evaluation**: 完整回退机制

### workflow.md 的简化

- **7 个标签**: ready-for-refinement → ready-for-design → ready-for-implement → in-progress → ready-for-review → ready-for-merge → done
- **单 PR 设计**: 设计和实现合并
- **回退保持连续**: 不关闭 PR

### 核心差异

| 特性 | prd.md | workflow.md |
|------|--------|-------------|
| Re-evaluation | 独立阶段 | 合并到工作流中 |
| PR 策略 | 可能多 PR | 单 PR |
| 状态表示 | 阶段 | GitHub Labels |

---

## 3. 待解决问题

### 3.1 失败处理策略

**问题**: 任务执行失败时如何处理？

**待澄清**:
- 自动重试次数？
- 超过重试次数后切换到 `blocked` 还是 `failed`？
- 人工如何介入？

**当前方案** (workflow.md):
- `status:blocked`: 需人工干预（如缺少 API Key）
- `status:failed`: 多次重试失败

---

### 3.2 暂停/恢复机制

**问题**: 如何支持用户中途暂停？

**当前方案** (workflow.md):
- **暂停**: 用户评论"暂停" → `status:paused` → 保存 checkpoint
- **恢复**: 用户评论"继续" → 从 checkpoint 恢复 → `in-progress`

**待实现**:
- checkpoint 保存位置（`.ralph-cache/`？）
- 恢复逻辑（如何从 checkpoint 恢复？）

---

### 3.3 质量门控检查项

**问题**: 质量门控应该检查什么？

**候选检查项**:
- [x] 类型检查 (`tsc --noEmit`)
- [x] Lint (`eslint`)
- [x] 单元测试 (`jest`)
- [ ] 代码覆盖率阈值？
- [ ] 自定义验证脚本？

**待决策**: 确定必须检查项和可选检查项

---

## 4. 演进历史

### 2026-03-06: 初始设计

**背景**:
- 从 prd.md 提取工作流设计
- 整合到 Ralph CLI 设计中

**关键决策**:
1. Stage 命名改为 `ready-for-*` 格式
2. 单 PR 跟踪设计和实现
3. 回退时保持 PR 打开，添加新 commits

**产出**:
- `design/workflow.md`: 工作流详细说明
- `design/talks/ralph-cli.md`: Ralph CLI 设计（包含工作流集成）

---

## 5. 参考资料

- [prd.md](../../openspec/changes/crawlph-skill/prd.md) - 原始 7 阶段设计
- [ralph-cli.md](./ralph-cli.md) - Ralph CLI 设计
- [workflow.md](../workflow.md) - 工作流详细说明

---

**标签**: workflow, design-decisions, ready-for-*
