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
2. Plan 阶段内完成自我审查（最多 3 次迭代）
3. Build 阶段支持 Ralph-style 任务循环执行
4. Task 执行时可访问完整上下文（proposal/design/spec/session-memories）
5. Build 失败时由 Mohist Agent 记忆失败原因并传递给重试
6. Specs 存储于项目目录，随代码版本化

**Non-Goals:**

- 不替换现有的 workflow.yaml 配置系统
- 不强制所有 issues 使用新流程（简单 issues 可继续使用原流程）
- 不实现多 Change 并行（M4 阶段考虑）
- 不与 OpenSpec CLI 直接集成（概念借用而非工具依赖）

## Decisions

### D1: Change 目录结构

**决策**: 使用 `.mohist-specs/changes/{issue-number}-{slug}/` 存放 Change 产物

**命名规则**:
- 基础名：`{issue.number}-{slug}`
- slug 生成：issue title 的 kebab-case 简化，最长 50 字符
- 冲突处理：如果目录已存在，自动添加 `-v2`, `-v3` 后缀

**示例**:
- Issue #42 "Add user authentication system" → `42-add-user-authentication/`
- 再次执行 → `42-add-user-authentication-v2/`

**理由**:
- 约定目录名 `.mohist-specs/` 类似于 `.github/`，不显得杂乱
- 位于项目根目录，随 git 版本化
- Code review 时可看到完整设计上下文

**替代方案**: `.mohist/changes/`（隐藏目录）
- 放弃原因：不在 git 内，无法参与 code review

### D2: Specs 生成策略（已更新）

**决策**: Plan 阶段完成所有生成和自我审查，Review 阶段纯人工审查

**流程**:
```
Plan 阶段: 
  1. Agent 生成 proposal + design + specs
  2. 自我审查（最多 3 次迭代）
     - 检查 specs 完整性
     - 验证设计可行性
     - 补充遗漏的 AC
  3. Agent 判断审查通过 → 生成 prd.json
  4. 达到上限仍未通过 → stage 失败，召唤人工

Review 阶段: 纯人工审查（approval gate）
  - 用户审查 Change 产物
  - 可以直接编辑修改
  - 满意后 approve，进入 build
```

**理由**:
- Agent 探索代码库后可以生成合理的初始 specs
- 自我审查在 plan 阶段内完成，减少 stage 切换复杂度
- 人工 review 确保 specs 质量

### D3: Session-Memories 使用文件系统（已更新）

**决策**: 使用文件系统存储，位于 Change 目录内

**存储位置**:
```
.mohist-specs/changes/{change-name}/session-memories/{task-id}.json
```

**内容**:
```json
{
  "task_id": "T-001",
  "timestamp": "2024-01-15T10:30:00Z",
  "insights": ["发现的约束/模式"],
  "adjustments": ["对后续任务的建议"],
  "success": true,
  "execution_summary": "实现了登录 UI，包含邮箱验证"
}
```

**理由**:
- Agent 顺序读取方便，满足当前场景
- 与 Change 产物一起版本化，便于调试
- 无需新增数据库表，简化实现
- 查询效率低但不影响当前场景（task 数量少）

**未来**: M4 阶段如需要跨 Change 学习，可迁移到数据库

### D4: Workflow 阶段扩展（已更新）

**决策**: 扩展 workflow.yaml，使用 4 stages：plan → review → build → check

**新 workflow**:
```yaml
stages:
  - plan       # 生成 Change 产物 + 自我审查
  - review     # 人工审查（approval gate）
  - build      # Ralph-style 任务执行
  - check      # 自动测试 + 人工验收 + 归档（approval gate）
```

**各阶段职责**:
- **plan**: 生成 proposal/design/specs，自我审查（3 次迭代），生成 prd.json
- **review**: 人工审查 Change 产物，可以编辑，满意后 approve
- **build**: Main-agent 驱动 Ralph 循环，逐个执行 task，失败时记忆并传递
- **check**: Agent 自动运行测试，然后人工验收，最后归档 Change

**理由**:
- review 阶段作为人工审查 gate，确保 specs 质量
- check 阶段合并自动测试和最终验收，简化流程

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

**重试时的附加内容**:
```
[之前尝试失败]
失败原因：{failure_reason}

[调整建议]
{adjustments}

[任务]
{task_description}
```

**理由**:
- Agent 可以看到完整的设计上下文
- 历史学习影响当前任务执行
- 失败原因传递给重试，提高成功率

### D6: 自我审查迭代控制（新增）

**决策**: Agent 自主判断是否通过，达到上限视为 stage 失败

**流程**:
1. 每次迭代 Agent 执行 validate + fix
2. 迭代结束时 Agent 判断"是否通过"
3. 通过标准：所有检查项满足，或无明显改进空间
4. 达到 maxIterations（3 次）仍未通过 → stage 标记失败
5. 失败 → 暂停，用户选择：
   - 手动修复后 `mo issue resume --skip-to-review`
   - 删除后重新 `mo propose 42`（创建新版本）

**理由**:
- Agent 有全局上下文，能更好判断是否收敛
- 硬性上限防止无限循环
- 失败时人工介入，不自动降级

### D7: Build 失败恢复机制（新增）

**决策**: 从失败 task 继续，记录 task-status.json

**失败类型与处理**:

| 失败类型 | 检测方式 | 重试 | 恢复方式 |
|---------|---------|------|---------|
| AC 不满足 | main-agent 验证 | ✅ 重试 2 次 | 自动重试，仍失败则 ask_user |
| 环境错误 | 错误信息匹配 | ✅ 重试 1 次 | 重试后仍失败则 ask_user |
| 代码依赖 | coder 明确说"无法完成" | ❌ | 直接 ask_user |
| 超时 | 超时检测 | ❌ | ask_user "是否拆分 task" |

**状态记录**:
```json
// task-status.json
{
  "current_task_index": 3,
  "tasks": [
    {"id": "T-001", "status": "completed", "attempts": 1},
    {"id": "T-002", "status": "completed", "attempts": 1},
    {"id": "T-003", "status": "failed", "attempts": 3, "error": "..."}
  ]
}
```

**恢复流程**:
1. 用户介入后调用 `mo issue resume`
2. main-agent 读取 task-status.json
3. 从 `current_task_index` 继续执行
4. 保留之前 task 的代码修改和 learnings

### D8: Change 命名策略（新增）

**决策**: 自动命名，冲突时创建新版本

**规则**:
- 基础名：`{issue.number}-{slug}`
- slug：issue title 的 kebab-case，最长 50 字符
- 冲突：目录已存在时，自动添加 `-v2`, `-v3`

**CLI 行为**:
- `mo propose 42`：创建新版本（如果有现有 Change）
- `mo propose 42 --force`：删除现有，创建新的（危险操作，需确认）

**理由**:
- 不可变原则：Change 一旦创建就不修改，新版本是新的 Change
- 安全：不会意外丢失用户的工作
- 可归档：旧版本可以移到 archive/

## Risks / Trade-offs

- [Risk] Agent 生成的 specs 质量不稳定 → **缓解**: Review 阶段强制人工审查，可以编辑修改
- [Risk] Context 过长导致 LLM 性能下降 → **缓解**: Session memories 只保留关键洞察，非完整日志
- [Risk] 与现有 workflow 冲突 → **缓解**: 可选功能，通过文件存在性自动检测，保持向后兼容
- [Risk] Specs 文件过多导致仓库膨胀 → **缓解**: 已完成的 Change 归档到 `.mohist-specs/archive/`
- [Trade-off] Session memories 存储在文件系统 vs 数据库 → 选择文件系统便于查看和调试，但查询效率较低

## Migration Plan

1. **Phase 1**: 实现核心工具（`read_prd`, `read_spec`, `store_learning`, `load_learnings`, `update_task_status`, `get_task_status`）
2. **Phase 2**: 更新 main-agent 支持 Ralph-style 任务循环、自我审查、失败恢复
3. **Phase 3**: 扩展 workflow-loader 支持自动检测（通过 prd.json 存在性）
4. **Phase 4**: 添加 CLI 命令 `mo propose` 和 `mo issue resume --skip-to-review`
5. **Phase 5**: 文档和示例

**回滚策略**: 新流程是可选的，通过文件存在性自动检测。如发现问题，可删除 `.mohist-specs/` 目录回退到传统流程。

## Open Questions（已更新）

1. ~~Change 的命名策略~~ → **已决定**: 自动命名 `{issue-number}-{slug}`，冲突加 `-v2`
2. Specs 生成时，如何平衡详细程度和 token 限制？
3. ~~Session memories 的清理策略~~ → **已决定**: 永久保留，随 Change 归档
4. ~~是否需要支持从现有的 plan 输出自动生成 Change 产物？~~ → **已决定**: 不需要迁移
