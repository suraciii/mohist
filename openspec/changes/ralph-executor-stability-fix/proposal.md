# 修复 OpenSpec Workflow 稳定性与集成完整性

## 问题描述

在对 `mohist-openspec-workflow` 变更进行两轮审查后，发现以下问题：

### 第一轮审查：ralph-executor 稳定性

1. **资源泄漏风险**
   - `ndJsonStream` 和 `ClientSideConnection` 对象没有显式关闭
   - 每个 task 都会创建新的 opencode 进程和连接
   - 长时间运行可能导致文件句柄或内存泄漏

2. **竞态条件**
   - `ensureKill()` 函数使用 `procExited` 标志防护，但存在异步时间窗口
   - 多路径调用（timeout、success、error）可能导致重复 kill

3. **内存无界增长**
   - `agentText` 字符串持续增长，无长度限制
   - 30分钟超时 + 无界字符串累积可能导致 OOM

4. **设计偏差**
   - 失败类型分类逻辑（AC不满足、环境错误、代码依赖、超时）尚未实现
   - 当前使用统一重试逻辑，不符合设计文档 D7 的要求

### 第二轮审查：集成完整性（T-008/T-009 实现后）

5. **8 个工具未注册到 main-agent（关键阻塞）**
   - `read_prd`, `read_spec`, `store_learning`, `load_learnings`,
     `update_task_status`, `get_task_status`, `run_self_review`, `generate_prd`
   - 这些工具已实现且有测试，但从未注册到 main-agent 的 ToolRegistry
   - Agent 无法在 plan 阶段调用 self-review 或生成 prd.json
   - Plan→Build 端到端流程完全断开

6. **Plan 阶段无 OpenSpec 感知**
   - Plan stage prompt 仍为原始模板：`'分析 issue #{issue.number}...'`
   - Agent 不知道应创建 specs、运行 self-review、生成 prd.json
   - `read_workflow` 工具的输出仅告知 build 阶段使用 `run_ralph_loop`，未提及 plan 阶段的 OpenSpec 行为

7. **change-creator.ts 两个 bug**
   - `--force` 时 `isNew` 标记为 `false`，但语义上应为 `true`（删旧建新）
   - `findNextVersion` 在已有版本化 Change 时可能产生命名冲突

8. **检测逻辑重复**
   - `workflow-loader.ts::detectOpenSpecForIssue()` 和 `detector.ts::detectOpenSpecChange()` 做重叠工作
   - 返回类型不同（三态 vs 二态），未来可能分歧

9. **测试覆盖不足**
   - `change-creator.ts`、`api/propose.ts`、`cli/commands/propose.ts` 无单元测试
   - `agent-workflow-e2e.test.ts` 断言工具注册数量为 5，新增注册后需更新

## 目标

**Goals:**

1. 修复资源泄漏问题，确保连接和 stream 正确关闭
2. 消除竞态条件，确保进程清理的原子性
3. 限制 agentText 最大长度，防止 OOM
4. 增强 detector.ts，正确处理多个 matching changes 的情况
5. 添加失败类型分类框架，为 T-006 实现做准备
6. 将 8 个缺失工具注册到 main-agent，打通 plan→build 端到端流程
7. 更新 plan stage 系统提示，加入 OpenSpec 模式指令
8. 修复 change-creator.ts 的 isNew 和 findNextVersion bug
9. 统一检测逻辑，消除 workflow-loader 和 detector 的重复
10. 补充 propose 相关测试覆盖

**Non-Goals:**

- 不修改 Ralph 循环的核心业务逻辑（任务排序、上下文组装）
- 不更改 ACP 协议交互方式
- 不实现完整的 T-006 失败处理（仅添加分类框架）
- 不实现 T-010（resume --skip-to-review）、T-011（check stage 扩展）、T-012（文档）

## 范围

**文件变更:**
- `packages/cli/src/openspec/ralph-executor.ts` - 资源泄漏/竞态/内存修复
- `packages/cli/src/openspec/detector.ts` - 多 Change 处理 + 统一检测
- `packages/cli/src/openspec/change-creator.ts` - bug 修复
- `packages/cli/src/agents/main-agent.ts` - 注册 8 个缺失工具
- `packages/cli/src/workflow/workflow-loader.ts` - 消除重复检测逻辑
- `packages/cli/tests/` - 更新和新增测试

**影响:**
- 阻塞: `mohist-openspec-workflow` 端到端流程可用性
- 下游: `mohist-openspec-workflow/T-010~T-012`

## 成功标准

- [ ] 所有 task 执行后资源完全释放
- [ ] agentText 长度限制在 10MB 以内
- [ ] detector 能正确处理多个 changes 的情况
- [ ] main-agent 注册所有 OpenSpec 相关工具（≥15 个）
- [ ] plan stage 在 OpenSpec 模式下能触发 self-review 并生成 prd.json
- [ ] change-creator 的 isNew 和 findNextVersion 行为正确
- [ ] 检测逻辑统一到单一入口
- [ ] 测试覆盖率保持在 90% 以上
- [ ] 向后兼容：无 Change 的 issue 继续走传统流程
