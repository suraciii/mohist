# Web UI 提案审查修复

## 发现的核心问题

通过深入审查 codebase 与提案的对比，发现了一个**根本性架构误判**。

### 误判：Signal File IPC

提案假设 Agent 是独立子进程（借鉴 nanoclaw 的 Docker 容器模型），设计了信号文件 IPC（Agent 写文件 → Server 轮询检测）。

**实际架构：** MainAgent 运行在 Server 同一进程内：

```
Server Process
  └── API Route (issues.ts)
        └── runMainAgent()        ← in-process
              └── agentLoop.run() ← Vercel AI SDK streamText()
                    ├── advance_stage tool → IssueRepo.updateStage()
                    ├── add_comment tool   → CommentRepo.create()
                    ├── get_issue tool     → IssueRepo.findById()
                    ├── read_workflow tool → loadWorkflow()
                    └── spawn_coder tool   → child_process.spawn('opencode acp')
                                                ↑ 唯一的子进程
                                                ↑ 结果通过 Promise 返回
```

只有 `spawn_coder` 启动 `opencode acp` 子进程执行具体编码任务，其结果通过 Promise 同步返回给 agent loop。MainAgent 的所有 tool 都可以直接调用 EventBus。

**结论：** 信号文件 IPC 完全不必要。改为 in-process EventBus。

### 误判：Exit-Respawn 模式

提案假设需要 Agent 进程 exit 再 respawn。实际上：

- Agent session 通过 `streamText()` 运行，自然结束即停止
- Session 是纯内存的 `Map<string, Session>`，无需 "exit" 任何进程
- 正确模型：**Stop & Resume** — session 自然结束 + 新 session 启动

### 未解决：上下文持久化

关键问题：新 session 如何获得前一个 stage 的 output？

**发现已有机制：** Agent 在每个 stage 结束时通过 `add_comment` 记录 output（系统 prompt 已指示）。Comments 持久化在 SQLite。恢复时从 DB comments 按时间倒序找到最新 agent comment，作为 `{plan.output}` 等变量注入新 session。

## 修改清单

| 文件 | 变更 |
|------|------|
| `design.md` | 重写决策 3（In-Process EventBus）、决策 4（Stop & Resume）、决策 5（AgentRunnerService 提取） |
| `proposal.md` | 删除 `signal-file-ipc` capability，更新描述 |
| `prd.json` | 删除 T-003（IPC Watcher），新增 T-003（AgentRunnerService），新增 T-004（EventBus 注入 tools），重新编号 12→13 任务 |
| `specs/signal-file-ipc/` | 整个删除 |
| `specs/web-ui-realtime/spec.md` | 重写：EventBus 直接从 tools emit，添加 projectId 过滤 |
| `specs/http-api/spec.md` | 添加 SSE project scoping，修复 approve 场景 |
| `specs/web-ui-issue-actions/spec.md` | 修复审批描述为 Stop & Resume |
| `specs/web-ui-project-switch/spec.md` | 添加 SSE 重连机制 |

## Codebase 关键发现

1. **Workflow stages**: `draft → plan → build → check → done`（AGENTS.md 旧名称已过时）
2. **Approval 是 prompt-based**: 系统 prompt 告诉 LLM "如果 approval: true 就停下来"，没有 API、没有 DB 状态
3. **单 Agent 限制**: `issues.ts` 的两个 `let` 变量，全局只能一个 agent
4. **Session 纯内存**: 重启即丢失，但 comments 在 SQLite 中持久化
5. **advance_stage tool 持有 stale issue**: `context.issue` 在 tool 创建时设置，多步调用后可能过期
6. **Labels 存储为 JSON 数组**: 在 issues 表中，无独立表
7. **ACP 协议**: `spawn_coder` 使用 `@agentclientprotocol/sdk` 与 opencode 子进程通信
