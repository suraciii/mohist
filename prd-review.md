# PRD 修改建议

基于架构探索和用户决策，对 `prd.md` 的修改建议。

---

## 关键决策总结

### 架构设计

1. **OpenSpec 角色**: 设计和实现阶段的核心工具，但可选（要求产出 specs）
2. **调用方式**: OpenClaw 通过 ACP runtime 调用 OpenCode
3. **Ralph Loop 位置**: OpenClaw 层（orchestrator）
4. **gh-issues**: 创建新的 skill（不扩展 gh-issues）
5. **Labels**: 使用简化版（PRD 当前）

### 工作流设计

6. **Specs 存储**: 每个 Issue 一个 spec 文件（`specs/issue-{N}.md`）
7. **OpenSpec 触发**: OpenClaw 智能触发
8. **Ralph Loop 实现**: 待研究（使用 Ralph Coding 理念）
9. **Issue Body**: 初始需求 → 完整需求 → 设计准备
10. **Design PR**: 仅 specs 文件
11. **Review Flow**: Agent Review → User Review
12. **并发处理**: 并行处理多个 Issues

### 实现细节

13. **Ready 标准**: 对话触发（用户说"可以设计了"）
14. **失败处理**: 使用 Ralph Coding 理念（自动重试直到成功）
15. **状态持久化**: 文件存储（`/data/.clawdbot/`）
16. **进度报告**: Gateway 消息（通过 OpenClaw 发送给用户）
17. **Labels 体系**: 简化版（PRD 当前）
18. **Skill 名称**: 待定

---

## PRD 主要问题

### 1. Issue Body 内容和演进规则不明确

**PRD 现状**:
- 没有明确说明初始需求和完整需求的区别
- 没有说明 Issue Body 在不同阶段的演进规则

**用户决策**:
- 初始需求（不能启动设计和开发）
- 完整需求（可以准备设计）
- 对话触发进入 Design 阶段

**修改建议**:
```markdown
### 1.4 Issue Body 演进规则

#### Exploration（探索）
- **Issue Body 内容**: 简短的需求描述（1-2 句话）
- **示例**: "文章管理需要支持多规格"
- **状态**: 初始需求，不能启动设计和开发

#### Refinement（提炼）
- **Issue Body 内容**: 完整的需求描述 + 任务清单（Markdown checkboxes）
- **示例**:
  ```markdown
  ## 需求描述
  文章管理需要支持多规格（SKU/SPU），包括：
  - 支持创建多规格商品
  - 支持批量导入规格
  - 支持规格库存管理
  
  ## 任务清单
  - [ ] 设计 SKU/SPU 数据模型
  - [ ] 实现规格创建 API
  - [ ] 实现批量导入功能
  - [ ] 实现库存管理功能
  ```
- **状态**: 完整需求，可以准备设计
- **触发**: 用户说"可以设计了"或 OpenClaw 智能判断需求完整

#### Design（设计）
- **Issue Body**: 保持不变
- **新增**: specs/issue-{N}.md（设计文档）
- **Design PR**: 仅包含 specs/issue-{N}.md

#### Implementation（实现）
- **Issue Body**: 更新任务清单进度（checkboxes）
- **示例**:
  ```markdown
  ## 任务清单
  - [x] 设计 SKU/SPU 数据模型
  - [x] 实现规格创建 API
  - [ ] 实现批量导入功能（进行中）
  - [ ] 实现库存管理功能
  ```
```

### 2. Ralph Loop 实现方案缺失

**PRD 现状**:
- 提到 Ralph Loop 但只有伪代码示例
- 没有详细的实现方案

**用户决策**:
- 使用 Ralph Coding 理念
- 在 OpenClaw 层实现（orchestrator）
- 自动重试直到成功

**修改建议**:
```markdown
### 2.4 Ralph Loop 实现方案

#### Ralph Coding 理念

**核心原则**:
1. 无限循环直到任务完成
2. 每次迭代都是全新上下文（clean context）
3. 外部文件保存进度（`/data/.clawdbot/ralph-progress-{issue}.json`）
4. 测试/编译失败 → 自动修复 → 继续循环

**伪代码**:
```python
def ralph_loop(issue_number):
    while True:
        # 读取进度文件
        progress = read_progress_file(issue_number)
        
        # 调用 OpenCode（全新上下文）
        result = opencode_acp(
            task=f"Continue implementing issue #{issue_number}",
            context_file=f"/data/.clawdbot/ralph-context-{issue_number}.md"
        )
        
        # 分析结果
        if result.status == 'completed':
            break
        elif result.status == 'failed':
            # Ralph Coding: 失败后自动修复
            write_progress_file(issue_number, result.error)
            continue  # 继续循环
        elif result.status == 'needs_user_input':
            send_gateway_message(result.question)
            user_response = wait_for_user_response()
            write_progress_file(issue_number, user_response)
            continue
```

#### OpenClaw 层实现

**位置**: 新的 skill（如 `spec-workflow`）

**关键特性**:
- 使用 OpenClaw 的 `sessions_spawn` with `runtime="acp"` and `agentId="opencode"`
- 状态持久化：`/data/.clawdbot/spec-workflow-state.json`
- 进度报告：通过 Gateway 消息发送给用户
- 并发支持：并行处理多个 Issues（最多 8 个）

**示例调用**:
```json
{
  "task": "Implement issue #42: Add SKU/SPU support",
  "runtime": "acp",
  "agentId": "opencode",
  "mode": "session",
  "thread": true,
  "runTimeoutSeconds": 3600
}
```
```

### 3. 进度报告机制不明确

**PRD 现状**:
- 使用 progress comment（Issue Comments）

**用户决策**:
- 使用 Gateway 消息（通过 OpenClaw 发送给用户）

**修改建议**:
```markdown
### 3.3 进度报告机制

#### Gateway 消息

**优先使用 Gateway 消息而不是 Issue Comments**:
- 更及时：用户在聊天界面实时看到进度
- 更灵活：可以发送富文本、图片等
- 更私密：不污染 Issue Comments 历史

**示例**:
```python
def send_progress_update(issue_number, message):
    send_gateway_message(
        channel="telegram",  # 或其他渠道
        target=user_chat_id,
        message=f"🔄 **Issue #{issue_number} Progress**\n\n{message}"
    )
```

**何时使用 Issue Comments**:
- Design PR merged 通知
- Implementation PR created 通知
- 重要的里程碑（如 Re-evaluation 触发）

**何时使用 Gateway 消息**:
- Ralph Loop 进度更新（每完成一个任务）
- Agent Review 完成
- 测试/编译失败通知
- 需要用户输入的问题
```

### 4. Re-evaluation 触发条件过于自动化

**PRD 现状**:
- 自动检测（基于 Ralph Loop 发现重大问题）

**用户决策**:
- 用户手动触发

**修改建议**:
```markdown
### 1.2.6 Re-evaluation（重新评估）

- **触发**: 用户手动触发
  - 用户在对话中说"重新评估这个设计"
  - 用户在 Issue 上添加 `action:reevaluate` label
- **执行**: OpenCode 重新分析，可能回到 Design 或 Refinement
- **输出**: 新的 Design 或更新后的 issue
- **标签**: `stage:re-evaluation`
- **自动化**: OpenCode 分析 + OpenClaw 通知用户决策

**注意**: 不自动触发 Re-evaluation，避免误判和过度反应
```

### 5. Labels 体系过于复杂

**PRD 现状**:
- 复杂的 labels 体系（stage:* + action:* + tech-debt:*）

**用户决策**:
- 简化版（PRD 当前）

**修改建议**:
```markdown
### 1.3 Labels 体系（简化版）

#### Stage Labels（必需）

| Label | 阶段 | 含义 |
|-------|------|------|
| `stage:exploration` | Exploration | 初始需求，不能启动设计 |
| `stage:refinement` | Refinement | 完整需求，可以准备设计 |
| `stage:design` | Design | 设计阶段，生成 specs |
| `stage:implementation` | Implementation | 实现阶段，Ralph Loop |
| `stage:review` | Review | Review 阶段，等待合并 |
| `stage:done` | Done | 已完成，Issue closed |
| `stage:re-evaluation` | Re-evaluation | 重新评估（用户手动触发）|

#### Action Labels（可选）

| Label | 含义 |
|-------|------|
| `action:pause` | 暂停 Ralph Loop |
| `action:reevaluate` | 触发 Re-evaluation |
| `action:cancel` | 取消任务 |

**注意**: 不使用 `tech-debt:*` labels，质量债务流程通过 Issue Comments 跟踪
```

### 6. Design PR 内容不明确

**PRD 现状**:
- Design PR + specs/ 目录（没有明确是否包含代码）

**用户决策**:
- 仅 specs 文件

**修改建议**:
```markdown
#### Design（设计）

- **触发**: 用户说"可以设计了"
- **执行**: OpenCode `/opsx:new` + `/opsx:ff` 生成 artifacts
- **输出**: 
  - Design PR（仅包含 `specs/issue-{N}.md`）
  - 不包含代码骨架或接口定义
- **标签**: `stage:design`
- **自动化**: 100%
```

### 7. OpenSpec 触发方式不明确

**PRD 现状**:
- 手动触发（/opsx:new 和 /opsx:apply 命令）或 label 触发

**用户决策**:
- OpenClaw 智能触发

**修改建议**:
```markdown
#### Design（设计）

- **触发**: OpenClaw 智能判断需求完整
  - 检测 Issue Body 是否包含完整的任务清单
  - 检测用户是否说"可以设计了"
  - 检测是否在 Refinement 阶段停留足够时间（可选）
- **执行**: OpenClaw 自动调用 OpenCode `/opsx:new`
- **输出**: Design PR（仅 specs 文件）
- **标签**: `stage:design`
- **自动化**: 100%

**注意**: OpenSpec 是可选的，如果不使用 OpenSpec 模式，要求产出 specs（可以手动编写）
```

### 8. 新的 Skill 名称和架构

**PRD 现状**:
- 没有明确提到新的 skill

**用户决策**:
- 创建新的 skill（名称待定）

**修改建议**:
```markdown
## 7. 技术实现

### 7.1 新的 Skill: `spec-workflow`（名称待定）

#### 架构

```
用户 ←→ OpenClaw (Gateway + Pi Agent)
         ↓
      spec-workflow skill
         ↓ (sessions_spawn with runtime="acp")
      OpenCode (作为 ACP harness agent)
         ↓
      OpenSpec (skill 在 OpenCode 内)
```

#### 关键特性

1. **7 阶段工作流**: Exploration → Refinement → Design → Implementation → Review → Done + Re-evaluation
2. **Ralph Loop**: 基于 Ralph Coding 理念，在 OpenClaw 层实现
3. **OpenSpec 集成**: 智能触发 OpenSpec `/opsx:new` 和 `/opsx:apply`
4. **并发支持**: 并行处理多个 Issues（最多 8 个）
5. **状态持久化**: 文件存储（`/data/.clawdbot/spec-workflow-state.json`）
6. **进度报告**: Gateway 消息

#### 参考 gh-issues Skill

gh-issues skill 提供了很好的参考：
- 6 阶段工作流
- --watch 和 --cron 模式
- 并行子代理
- Claim-based tracking
- Cursor file

**差异**:
- spec-workflow 支持 7 阶段（而不是 6 阶段）
- spec-workflow 集成 OpenSpec
- spec-workflow 使用 Gateway 消息（而不是 Issue Comments）
- spec-workflow 使用 Ralph Coding 理念（而不是简单的 --watch）
```

---

## 其他修改建议

### 1. 简化 Re-evaluation 流程

**当前 PRD**: 过于复杂，包含回退到 Design 或 Refinement 的详细流程

**修改建议**: 简化为：
```markdown
### Re-evaluation 流程

1. 用户手动触发（对话或 label）
2. OpenClaw 暂停 Ralph Loop
3. OpenCode 重新分析
4. OpenClaw 发送 Gateway 消息给用户，说明问题和建议
5. 用户决定下一步：
   - 回到 Design：生成新的 Design PR
   - 回到 Refinement：更新 Issue Body
   - 继续 Implementation：忽略问题
```

### 2. 明确质量债务流程

**当前 PRD**: 过于自动化

**修改建议**:
```markdown
### 质量债务流程

1. Ralph Loop 在实现过程中发现技术债务
2. OpenCode 记录到 `/data/.clawdbot/tech-debt-{issue}.json`
3. Ralph Loop 完成后，OpenClaw 发送 Gateway 消息给用户：
   "⚠️ **发现技术债务**\n\n"
   "- UserService 过于臃肿，建议拆分\n"
   "- 缺少错误处理中间件\n\n"
   "是否创建新的 Issue 跟踪？"
4. 用户确认后，OpenClaw 创建新的 Issue（label: `tech-debt`）
```

### 3. 明确并发处理机制

**修改建议**:
```markdown
### 并发处理

**支持并行处理多个 Issues**:
- 每个 Issue 独立的 branch + PR
- 最多 8 个并发（参考 gh-issues skill）
- 使用 Claim-based tracking 防止重复处理

**示例**:
```
Issue #42: feature/issue-42
Issue #43: feature/issue-43
Issue #44: feature/issue-44
```

**状态文件**: `/data/.clawdbot/spec-workflow-claims.json`
```json
{
  "owner/repo#42": "2026-03-05T10:00:00Z",
  "owner/repo#43": "2026-03-05T10:05:00Z"
}
```
```

---

## 总结

### 需要修改的主要部分

1. **Issue Body 演进规则**（新增 1.4 节）
2. **Ralph Loop 实现方案**（新增 2.4 节）
3. **进度报告机制**（修改 3.3 节）
4. **Re-evaluation 触发条件**（修改 1.2.6 节）
5. **Labels 体系**（简化 1.3 节）
6. **Design PR 内容**（明确 1.2.3 节）
7. **OpenSpec 触发方式**（修改 1.2.3 节）
8. **新的 Skill 架构**（新增第 7 节）

### 下一步行动

1. 根据修改建议更新 `prd.md`
2. 设计新的 skill（`spec-workflow`）
3. 技术验证：OpenClaw ACP 调用 OpenCode
4. 实现 Ralph Loop
