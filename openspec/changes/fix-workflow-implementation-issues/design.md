# 设计文档：修复 Mohist Workflow 实现问题（修订版）

## 重大发现：已有完善的暂停机制

经过深入代码审查，发现系统**已经存在**完善的暂停机制，原方案（添加 shouldPause 标记）是重复造轮子。

### 现有暂停架构

```
┌─────────────────────────────────────────────────────────────┐
│                    现有暂停架构                              │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  AgentRunnerService                                         │
│  ├─ shouldPauseAtCurrentStage()                            │
│  │   └─ 检查 workflow.yaml 中的 approval: true             │
│  ├─ sessionManager.pause()                                 │
│  ├─ sessionManager.resume()                                │
│  └─ pausedSessions Map<issueNumber, Session>               │
│                                                             │
│  暂停触发条件：                                              │
│  1. Main Agent 执行完成                                     │
│  2. 当前 stage 的下一个 stage 需要 approval                 │
│  3. 调用 sessionManager.pause() 保存会话                    │
│                                                             │
│  恢复机制：                                                  │
│  1. 用户调用 resume()                                       │
│  2. 从 pausedSessions 恢复会话                              │
│  3. 追加用户消息，继续执行                                   │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### RalphExecutor 暂停机制

```
RalphExecutor (OpenSpec Task Loop)
├─ 任务失败时设置 shouldPause = true
├─ 调用 onAskUser() 回调询问用户
└─ 根据用户回答决定：重试/跳过/暂停

注意：这是 task 级别的暂停，不是 stage 级别
```

### 两个暂停机制的关系

| 层级 | 组件 | 触发条件 | 恢复方式 |
|------|------|---------|---------|
| Stage | AgentRunnerService | Stage 完成，需要 approval | resume() |
| Task | RalphExecutor | Task 失败，需要决策 | onAskUser 回调 |

**结论：** 应该统一使用 AgentRunnerService 的暂停机制，RalphExecutor 的暂停通过事件触发。

---

## 修订后的设计决策

### 决策 1（修订）: 利用现有的 AgentRunnerService 暂停机制

**原方案:** 添加 shouldPause 标记到 StageResult

**修订方案:** 修复和增强现有暂停机制

**原因:**
- 已有成熟实现，无需重复
- sessionManager.pause()/resume() 更可靠
- 与 workflow.yaml 配置集成

**修复内容:**
1. 修复 workflow.yaml 配置（添加缺失的 stage approval 配置）
2. 确保 advance_stage 正确更新 stage
3. 确保 AgentRunnerService 能检测到 stage 变化

---

### 决策 2: 统一工作流模式（保持不变）

**问题:** 传统模式、OpenSpec/Ralph 模式、WorkflowController 模式三种并存

**解决方案:** 
1. **保留 RalphExecutor** - 它提供了精细的任务执行和失败处理
2. **集成到 Build 阶段** - WorkflowController.executeBuildStage 内部调用 RalphExecutor
3. **统一暂停机制** - RalphExecutor 的 onAskUser 触发 AgentRunnerService 的暂停

**架构图:**
```
Main Agent
    │──▶ execute_stage('build')
    │       │──▶ WorkflowController.executeBuildStage()
    │       │       │──▶ 检查 prd.json 是否存在
    │       │       │       ├─ 存在: RalphExecutor.execute()
    │       │       │       │       ├─ 正常执行 tasks
    │       │       │       │       └─ 失败时 onAskUser → 触发暂停
    │       │       │       └─ 不存在: spawn_coder 执行构建
    │       │       └──▶ 返回结果
    │       └──▶ AgentRunnerService 检查是否需要暂停
    │               └─ 需要: pause() → 等待 resume()
    └──▶ advance_stage() 推进到下一阶段
```

---

### 决策 3: 统一阶段转换规则（保持不变）

使用 `types/index.ts` 中的 `STAGE_TRANSITIONS` 作为唯一规则源。

**关键修复:**
- 确保所有 stage 都在 workflow.yaml 中有配置
- 确保 approval 标记正确设置

---

### 决策 4（修订）: 修复审批状态管理

**原方案:** 添加重复检查到 execute_stage

**修订方案:** 
1. 利用现有的 `findPendingApproval(projectId)` 
2. 修复其逻辑：应该按 issue 检查，而不是 project
3. 在 AgentRunnerService.start() 中添加重复启动检查

**修复内容:**
```typescript
// issue-repo.ts - 修复 findPendingApproval
// 原：按 projectId 查询（返回单个 issue）
// 新：按 issueId 查询，或添加新方法

findPendingApprovalByIssueId(issueId: string): Issue | null {
  const row = this.db.get<IssueRow>(
    `SELECT * FROM issues WHERE id = ? AND approval_state IS NOT NULL`,
    [issueId]
  );
  if (!row) return null;
  const issue = rowToIssue(row);
  return issue.approvalState?.status === 'awaiting' ? issue : null;
}
```

---

### 决策 5: 统一接口定义（保持不变）

创建 `types/workflow-results.ts` 作为单一来源。

---

### 决策 6（修订）: 保守地简化系统提示词

**原方案:** 完全重写提示词结构

**修订方案:** 最小化修改，保留现有结构

**修改内容:**
1. 保留现有 136 行提示词的主体
2. 删除已废弃工具的引用（run_ralph_loop）
3. 添加 execute_stage 使用说明
4. 保留 OpenSpec 相关说明（仍需要）

**原因:** 提示词工程风险高，最小修改更安全

---

### 决策 7: 增强 JSON 解析容错（保持不变）

多策略解析是合理的需求。

---

### 决策 8（修订）: Build 阶段集成 RalphExecutor

**修订方案:**
```typescript
// workflow-controller.ts
private async executeBuildStage(issue: Issue): Promise<StageResult> {
  const prd = this.artifactManager.readPrd(issue.number);
  
  if (prd && prd.tasks && prd.tasks.length > 0) {
    // 使用 RalphExecutor 执行 OpenSpec 任务
    const change = detectOpenSpecChange(this.worktreePath, issue);
    if (change) {
      const executor = new RalphExecutor({
        worktreePath: this.worktreePath,
        projectPath: this.worktreePath,
        issueId: issue.id,
        onAskUser: async (question, taskId) => {
          // 触发暂停，等待用户回复
          this.eventBus?.emit('ask_user', { issueId: issue.id, question, taskId });
          // 返回用户回复（需要外部传入）
          return await this.waitForUserResponse(issue.id);
        }
      });
      
      const result = await executor.execute(change);
      return {
        success: result.success,
        requiresApproval: result.paused || !result.success,
        output: result,
      };
    }
  }
  
  // 非 OpenSpec 模式，使用传统 spawn_coder
  // ...现有逻辑
}
```

---

## 向后兼容策略

### 处理 Draft/Check 阶段的 Issues

```
数据库中可能存在的 issues：
- Draft → Plan → Build → Check → Done（旧流程）
- Explore → Plan → Build → Review → Done（新流程）

迁移策略：
1. 保留 Draft/Check 阶段支持（STAGE_TRANSITIONS 已包含）
2. 新问题统一使用新流程（Explore 开始）
3. workflow.yaml 支持两种配置
```

### workflow.yaml 配置

```yaml
# 支持新旧两种 stage 名称
stages:
  # 新流程
  - stage: explore
    prompt: "探索需求..."
    approval: false
  - stage: plan
    prompt: "生成设计..."
    approval: true
  - stage: build
    prompt: "执行构建..."
    approval: false
  - stage: review
    prompt: "代码审查..."
    approval: true
  - stage: done
    prompt: "完成"
    approval: false
    
  # 旧流程（向后兼容）
  - stage: draft
    prompt: "起草..."
    approval: false
  - stage: check
    prompt: "检查..."
    approval: true
```

---

## 修订后的任务清单

### Phase 1: 关键修复（必须）

1. **T-001**: 创建 `types/workflow-results.ts` 统一接口
2. **T-002**: 更新所有模块使用统一接口
3. **T-003**: 修复 `advance-stage.ts` 使用 `STAGE_TRANSITIONS`
4. **T-004**: 修复 `findPendingApproval` 方法（添加 issueId 版本）
5. **T-005**: 添加重复启动检查到 `AgentRunnerService`

### Phase 2: Build 阶段集成（重要）

6. **T-006**: 调研 RalphExecutor 功能完整性
7. **T-007**: 修改 `executeBuildStage` 集成 RalphExecutor
8. **T-008**: 实现 onAskUser 到暂停的转换
9. **T-009**: 测试 Build 阶段 OpenSpec 任务执行

### Phase 3: 健壮性增强（建议）

10. **T-010**: 实现多策略 JSON 解析
11. **T-011**: 简化 Main Agent 提示词（保守修改）
12. **T-012**: 添加集成测试
13. **T-013**: 全面测试验证

### Phase 4: 可选优化（延后）

14. **T-014**: 进一步简化提示词
15. **T-015**: 优化错误恢复机制

---

## 风险与缓解（修订）

| 风险 | 概率 | 影响 | 缓解措施 |
|------|------|------|---------|
| 现有暂停机制有 bug | 低 | 高 | 充分测试 pause/resume 流程 |
| RalphExecutor 集成复杂 | 中 | 中 | 分步骤集成，先测试单独功能 |
| 向后兼容性问题 | 低 | 中 | 保留 Draft/Check 支持，渐进迁移 |
| 提示词修改引入 regression | 中 | 高 | 保守修改，保留 80% 原有内容 |

---

## 成功标准（修订）

1. **功能**: 
   - Plan/Review stage 正确触发暂停
   - resume() 正确恢复执行
   - Build stage 支持 OpenSpec 任务
   
2. **兼容性**:
   - 旧 issues (Draft/Check) 仍可执行
   - 新 issues (Explore/Review) 正常工作
   
3. **稳定性**:
   - 所有测试通过
   - 无 regression
   
4. **代码质量**:
   - 类型统一
   - 规则一致
