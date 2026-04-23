## Context

E2E walkthrough (Issue #11) 暴露了 5 个影响 pipeline 可靠性的问题。当前 pipeline 在 happy path 上可以走通（draft → plan → build → review → done），但在以下场景下失效：

1. 服务器重启后丢失审批门禁状态
2. Agent 崩溃后 recoverIssues() 不加区分地重置所有 issue
3. Build 完成后代码未提交到 git
4. Done 阶段 issue 状态仍为 active
5. ACP 连接关闭时产生 EPIPE 错误

核心代码在：
- `agent-runner-service.ts`（pendingGates、recoverIssues）
- `workflow-controller.ts`（阶段转换、build stage）
- `api/issues.ts`（approve 端点）
- `agent-runtime/acp-session.ts`（ACP 连接管理）

DB 层 `issue-repo.ts` 已有 `setApprovalState()`、`findPendingApproval()` 等方法，但 approve 端点没有使用 DB fallback。

## Goals / Non-Goals

**Goals:**
- 审批门禁状态在服务器重启后可恢复（通过 DB fallback）
- recoverIssues() 不再重置正在等待审批的 issue
- Build 阶段完成后的代码变更被自动 commit 到 worktree
- Done 阶段 issue status 为 completed
- 减少 ACP EPIPE 错误日志噪音

**Non-Goals:**
- 不改变 approval 的交互流程（仍然是通过 CLI/API 审批）
- 不实现 worktree 到主分支的自动 merge（后续变更）
- 不实现 pendingGates 的完整持久化方案（使用 DB fallback 已足够）
- 不重新设计 ACP 协议（仅改进 stream 关闭时序）

## Decisions

### D1: 审批门禁使用 DB fallback 而非持久化 pendingGates

**选择**: approve 端点在 `hasPendingGate()` 返回 false 时，查询 DB 的 `approval_state` 作为 fallback

**备选方案**:
- A: 将 pendingGates 持久化到 SQLite（需要新表或字段）
- B: 完全去掉内存 Map，每次都查 DB
- C: 在服务器启动时从 DB 重建 pendingGates

**理由**: 方案 A 增加持久化复杂度；方案 B 增加每次请求的 DB 查询；方案 C 需要在启动时遍历所有 issue。DB fallback（当前选择）是最小改动——只在内存 miss 时查一次 DB，正常路径性能无影响。

### D2: recoverIssues 区分 awaiting 和 crashed

**选择**: recoverIssues() 在重置前检查 `approval_state.status === 'awaiting'`，对 awaiting issue 恢复 pendingGates 而非重置

**理由**: 这是最安全的恢复策略——awaiting 状态的 issue 已经完成工作，只需要恢复审批入口。crashed 的 issue 才需要重置。

### D3: Build 后通过 worktree git commit

**选择**: 在 `runPipelineBuildStage()` 完成后调用 `simpleGit(worktreePath).add('.').commit(message)`

**备选方案**:
- A: 让 agent 在 ACP session 中自行 commit（依赖 agent 行为）
- B: 在 workflow controller 中增加 post-build hook

**理由**: 方案 A 不可靠（agent 可能不 commit）；方案 B 过度设计。直接在 build 完成后 commit 最简单可靠。

**commit 内容**: 只提交代码变更，不提交 openspec/changes/ 下的产物（这些在 worktree 创建时已经存在，且不应被 commit 到代码仓库）。

### D4: IssueStatus 增加 Completed 枚举值

**选择**: 在 `IssueStatus` 枚举中增加 `Completed = 'completed'`，workflow 到达 done 阶段时设置

**理由**: 语义清晰——done 阶段的 issue 应该标记为完成状态。`active` 在 done 阶段语义错误。

**影响范围检查**: 
- `api/status.ts` 使用 `status === 'active'` 字符串比较，需要确保 `'completed'` 能正确处理
- `issue-service.ts` 的 `resume()` 方法不应将 completed issue 恢复为 active（需要增加检查）
- `agent-runner-service.ts` 的 `detectRecoverableIssues()` 只找 active issue，completed 不会被 recover，正确

### D5: ACP stream 关闭时序改进

**选择**: 在 `proc.on('exit')` 中立即 destroy stdin/stdout streams，而非在 `ensureKill()` 中

**理由**: EPIPE 发生在子进程退出后、cleanup() 调用 abort() 时。此时底层 pipe 已关闭，但 Web Stream adapter 仍尝试写入。在 proc.on('exit') 中立即 destroy streams 可以切断 write 路径，避免 EPIPE。

**原方案（T-005）的问题**: 在 ensureKill() 中 destroy 时机太晚——ensureKill() 在 cleanup() 末尾调用，而 cleanup() 先调用 abort() 才调用 ensureKill()。abort() 时 streams 还未 destroy，仍可能产生 EPIPE。

## Risks / Trade-offs

- **[Risk] DB fallback 增加 approve 请求延迟** → 仅在内存 miss（服务器重启后）触发，正常路径无影响
- **[Risk] git commit 可能因 worktree 状态异常失败** → commit 失败时记录警告但不阻塞 pipeline；无文件变更时跳过 commit
- **[Risk] Completed 状态可能影响依赖 IssueStatus 的现有代码** → 已全面检查：
  - `api/status.ts`: 使用字符串比较 `'active'`，completed 不会被计入 active，正确
  - `issue-service.ts resume()`: 需要增加 guard，防止 resume completed issue
  - `agent-runner-service.ts`: completed 不会被 recover，正确
- **[Risk] recoverIssues 恢复 awaiting issue 后 agent 未自动启动** → 恢复 pendingGates 只恢复审批入口，approve 后仍需手动 resume 或自动恢复 agent（这是正确行为）
- **[Risk] ACP stream destroy 可能丢失未读取的数据** → proc.on('exit') 触发时子进程已退出，stdout 数据已读取完毕（或已触发 error），destroy 不会丢失有效数据

## 实施顺序建议

```
T-001 (IssueStatus.Completed)
    │
    ▼
T-002 (approve DB fallback) ──→ T-003 (recoverIssues)
    │                              │
    ▼                              ▼
T-004 (build git commit)      T-006 (tests)
    │
    ▼
T-005 (ACP EPIPE fix)
    │
    ▼
T-006 (tests - 全部)
```

T-001 和 T-002 可以并行（修改不同文件）。
T-003 依赖 T-002（使用相同的 DB fallback 逻辑）。
T-006 依赖所有其他任务。
