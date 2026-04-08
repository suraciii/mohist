# 修复 Ralph Executor 稳定性问题

## 问题描述

在审查 `mohist-openspec-workflow` 变更的已实现代码时，发现 `ralph-executor.ts` 存在多个严重的稳定性问题，可能导致生产环境故障。

### 发现的问题

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

## 目标

**Goals:**

1. 修复资源泄漏问题，确保连接和 stream 正确关闭
2. 消除竞态条件，确保进程清理的原子性
3. 限制 agentText 最大长度，防止 OOM
4. 增强 detector.ts，正确处理多个 matching changes 的情况
5. 添加失败类型分类框架，为 T-006 实现做准备

**Non-Goals:**

- 不修改 Ralph 循环的核心业务逻辑（任务排序、上下文组装）
- 不更改 ACP 协议交互方式
- 不实现完整的 T-006 失败处理（仅添加分类框架）

## 范围

**文件变更:**
- `packages/cli/src/openspec/ralph-executor.ts` - 核心修复
- `packages/cli/src/openspec/detector.ts` - 增强检测逻辑
- `packages/cli/tests/ralph-executor.test.ts` - 更新测试

**影响:**
- 阻塞: `mohist-openspec-workflow/T-005-D` (main-agent 集成)
- 下游: `mohist-openspec-workflow/T-006` (完整失败处理)

## 成功标准

- [ ] 所有 task 执行后资源完全释放（通过 valgrind 或类似工具验证）
- [ ] agentText 长度限制在 10MB 以内
- [ ] detector 能正确处理多个 changes 的情况
- [ ] 测试覆盖率保持在 90% 以上
- [ ] 向后兼容：无 Change 的 issue 继续走传统流程