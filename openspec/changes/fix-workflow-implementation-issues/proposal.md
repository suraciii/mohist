# 修复 Mohist Workflow 实现问题（修订版）

## 问题陈述

经过深入代码审查，发现当前 Mohist Workflow 实现存在以下问题：

### 关键问题

1. **已有暂停机制未被充分利用** - `AgentRunnerService` 已有 `pause()/resume()` 机制，但配置和集成存在问题
2. **阶段转换规则不一致** - `types/index.ts` 和 `advance-stage.ts` 定义不同
3. **接口定义分散** - `PlanResult` 和 `ReviewResult` 在多个文件定义
4. **Build 阶段未集成 RalphExecutor** - OpenSpec 任务执行与主工作流分离
5. **审批状态检查逻辑有误** - `findPendingApproval` 按 project 查询而非 issue

### 原方案问题

原方案提议添加 `shouldPause` 标记到 `StageResult`，但经调查发现：
- 系统**已经存在**更完善的暂停机制（`AgentRunnerService`）
- 添加 `shouldPause` 是重复造轮子，且与现有机制冲突
- 应修复和增强现有机制，而非引入新机制

## 解决方案概述

### 核心策略：利用现有架构，修复集成问题

1. **修复暂停机制配置** - 确保 workflow.yaml 正确配置 approval 标记
2. **统一阶段转换规则** - 使用 `STAGE_TRANSITIONS` 作为唯一规则源
3. **统一接口定义** - 创建 `types/workflow-results.ts`
4. **集成 RalphExecutor** - 将 OpenSpec 任务执行统一到 Build 阶段
5. **修复审批状态检查** - 修正 `findPendingApproval` 逻辑

### 不变更的原则

- ✅ 保留 `AgentRunnerService` 现有暂停机制
- ✅ 保留 `Main Agent` 提示词主体（保守修改）
- ✅ 保留 Draft/Check 阶段向后兼容
- ✅ 保留 RalphExecutor 功能（集成而非删除）

## 影响范围

- `packages/cli/src/types/` - 新增 workflow-results.ts
- `packages/cli/src/workflow/workflow-controller.ts` - Build 阶段集成 RalphExecutor
- `packages/cli/src/tools/advance-stage.ts` - 统一阶段转换规则
- `packages/cli/src/db/issue-repo.ts` - 修复 findPendingApproval
- `packages/cli/src/services/agent-runner-service.ts` - 添加重复启动检查
- `packages/cli/src/agents/main-agent.ts` - 保守简化提示词

## 预期结果

修复后：
1. Plan/Review stage 自动触发暂停，等待用户审批
2. resume() 正确恢复执行，继续下一阶段
3. Build stage 支持 OpenSpec 任务（通过 RalphExecutor）
4. 所有接口类型统一，无重复定义
5. 旧 issues (Draft/Check) 和新 issues (Explore/Review) 都能正常工作

## 验证方式

1. 运行 `npm test` 确保所有测试通过
2. 创建新 issue，验证 Explore → Plan → Build → Review → Done 完整流程
3. 验证暂停和恢复机制
4. 验证 OpenSpec 任务执行
5. 验证旧 issues 向后兼容
