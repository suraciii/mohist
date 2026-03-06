## Context

crawlph 是一个 OpenClaw 专用 Agent，用于自动化 spec-driven development workflow。它运行在独立的 agent session 中，不占用用户的 main session，支持后台持续运行和多种交互方式。

### 架构层次（专用 Agent 架构）

```
┌─────────────────────────────────────────────────────────────┐
│                  OpenClaw Gateway                           │
│                                                             │
│   触发源 (多入口)                                            │
│   ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌──────────┐     │
│   │Telegram │  │ Discord │  │ GitHub  │  │  Main    │     │
│   │ Channel │  │ Channel │  │ Webhook │  │  Agent   │     │
│   └────┬────┘  └────┬────┘  └────┬────┘  └────┬─────┘     │
│        │            │            │             │            │
│        └────────────┴────────────┴────────────┘            │
│                          │                                   │
│                          ▼                                   │
│   ┌────────────────────────────────────────────────┐        │
│   │         crawlph Agent (专用编排器)              │        │
│   │                                                │        │
│   │   配置:                                         │        │
│   │   • id: "crawlph"                             │        │
│   │   • workspace: ~/.openclaw/workspace-crawlph/  │        │
│   │   • agentDir: ~/.openclaw/agents/crawlph/      │        │
│   │                                                │        │
│   │   行为:                                         │        │
│   │   • 启动后自动进入 watch mode                   │        │
│   │   • 每 60s 检查新 Issues                       │        │
│   │   • spawn sub-agents 处理具体 Issues            │        │
│   │                                                │        │
│   └─────────────┬──────────────────────────────────┘        │
│                 │ sessions_spawn (max 8)                     │
│                 ▼                                             │
│   ┌──────────────┐  ┌──────────────┐  ┌──────────────┐     │
│   │ Sub-agent #1 │  │ Sub-agent #2 │  │ Sub-agent #N │     │
│   │  (Issue 123) │  │  (Issue 456) │  │  (Issue 789) │     │
│   └──────────────┘  └──────────────┘  └──────────────┘     │
│                                                             │
│   同时...                                                    │
│                                                             │
│   ┌──────────────┐                                            │
│   │  main agent  │ ← 用户正常使用，不受影响                      │
│   │  (你的会话)   │                                            │
│   └──────────────┘                                            │
└─────────────────────────────────────────────────────────────┘
                           │
                           │ External Tools
                           ▼
┌─────────────────────────────────────────────────────────────┐
│   GitHub API    │   OpenSpec CLI   │   Channel API          │
│   (Issue/PR)    │   (specs)        │   (notifications)       │
└─────────────────────────────────────────────────────────────┘

状态持久化:
~/.openclaw/agents/crawlph/data/
├── crawlph-claims.json
├── crawlph-cursor.json
└── progress/
    └── issue-{N}.json
```

### 当前状态

- OpenClaw 支持 multi-agent 架构，每个 agent 完全隔离
- OpenSpec CLI 已安装（v1.2.0），可用于生成 specs
- OpenCode 可通过 ACP runtime 调用
- 支持 bindings 路由消息到不同 agent

## Goals / Non-Goals

**Goals:**
- 实现完整的 7-stage spec-driven workflow
- 支持 Ralph Loop（无限重试直到成功）
- 集成 OpenSpec CLI 生成设计规范
- 支持手动、watch、cron 三种触发模式
- 并发处理最多 8 个 Issues
- Design + Implementation 合并在同一个 PR

**Non-Goals:**
- 不实现 Plugin（Phase 1）
- 不实现自动 re-evaluation（需要手动触发）
- 不支持自定义 workflow 配置（使用固定 7-stage）
- 不实现多仓库支持（单仓库）

## Decisions

### D1: 专用 Agent 架构（替代纯 Skill）

**选择**: 使用专用 Agent (`crawlph`) 作为编排器，而非纯 Skill

**理由**:
- 用户需要 **后台持续运行** (watch mode)，长期占用会话，纯 Skill 会阻塞 main session
- 用户需要 **多种交互方式**：Telegram/Discord channel、GitHub webhook、main session 触发
- 专用 Agent 完全隔离，不影响用户正常使用 main session
- 利用 OpenClaw 的 agent 会话持久化，重启后自动恢复状态

**替代方案**: 纯 Skill 架构 - 拒绝，因为：
- watch mode 会阻塞 main session
- 无法通过 channel 直接交互
- 无法接收 GitHub webhook 事件

**配置示例** (`~/.openclaw/openclaw.json`):
```json5
{
  agents: {
    list: [
      {
        id: "crawlph",
        workspace: "~/.openclaw/workspace-crawlph",
        agentDir: "~/.openclaw/agents/crawlph/agent",
        model: "anthropic/claude-opus-4-5",
        thinking: "high",
        sandbox: { mode: "off" },
      },
    ],
  },
  bindings: [
    {
      agentId: "crawlph",
      match: { channel: "telegram", peer: { kind: "channel", id: "CHANNEL_ID" } },
    },
  ],
}
```

### D2: Orchestrator 循环实现 Ralph Loop

**选择**: Orchestrator agent 循环 spawn 新 sub-agent

```
while (!success) {
  spawn_sub_agent_with_clean_context()
  if (success) break
  send_progress_to_channel()
}
```

**理由**:
- 每次迭代都是干净上下文，符合 Ralph Coding 理念
- 避免上下文无限积累
- Orchestrator 只保留必要状态（已处理 Issues、进度）

**替代方案**: Sub-agent 内循环 - 拒绝，因为上下文会积累

### D3: Design + Implementation 同一个 PR

**选择**: 创建 Draft PR → 添加 specs → 实现 → 标记 Ready for Review

**理由**:
- 减少上下文切换
- specs 和实现紧密关联
- Review 时可以看到完整上下文

**替代方案**: Design PR 和 Implementation PR 分开 - 拒绝，因为增加复杂度

### D4: Issue Comments 记录状态

**选择**: 通过 Issue Comments 记录处理进度

**理由**:
- 直接在 Issue 中可见
- 不依赖外部状态存储
- 用户可以订阅 Issue 通知

**替代方案**: 仅使用 labels - 拒绝，因为 labels 无法表达详细进度

### D5: Telegram Channel 进度报告

**选择**: 通过 Telegram/其他 Channel 发送进度通知

**理由**:
- 实时通知
- 不污染 Issue Comments
- 支持多人协作

**配置方式**:
```json
// ~/.openclaw/openclaw.json
{
  "skills": {
    "entries": {
      "crawlph": {
        "notifyChannel": "telegram:123456"
      }
    }
  }
}
```

### D6: Agent 数据目录状态持久化

**选择**: 使用 OpenClaw Agent 数据目录（`~/.openclaw/agents/crawlph/data/`）

**文件结构**:
```
~/.openclaw/agents/crawlph/
├── agent/
│   └── auth-profiles.json   # GitHub token
├── sessions/
│   └── main.jsonl           # Agent 会话历史
└── data/                    # crawlph 专用状态
    ├── crawlph-claims.json  # Issue claims (防止重复处理)
    ├── crawlph-cursor.json # Watch mode cursor
    └── progress/           # 每个 Issue 的进度
        ├── issue-123.json
        └── issue-456.json
```

**理由**:
- 符合 OpenClaw 标准目录结构
- 利用 agent 的会话持久化机制
- 与 OpenClaw 其他数据分离
- GitHub token 可以通过 agent 的 auth-profiles 管理

**进度文件格式** (`progress/issue-{N}.json`):
```json
{
  "issueNumber": 123,
  "currentStage": "implementation",
  "attempts": 3,
  "prNumber": 456,
  "lastError": "TypeScript compilation failed",
  "checkpoints": {
    "exploration": "2024-01-01T00:00:00Z",
    "refinement": "2024-01-01T01:00:00Z",
    "design": "2024-01-01T02:00:00Z"
  },
  "context": {
    "branchName": "issue-123-add-auth",
    "specFile": "openspec/changes/issue-123/spec.md"
  }
}
```

### D7: 并发限制 8 个 sub-agents

**选择**: 最多同时运行 8 个 sub-agents

**理由**:
- 与 gh-issues 一致
- 平衡效率和资源消耗
- GitHub API rate limit 考虑

### D8: 进度报告策略（双通道）

**选择**: 同时使用 Channel Notifications + Issue Comments

**分工**:
| 机制 | 用途 | 触发时机 |
|------|------|----------|
| **Channel Notifications** | 实时进度更新 | Ralph Loop 进度、失败通知、重试计数 |
| **Issue Comments** | 重要里程碑 | Design PR created、Implementation PR ready、Merged |

**理由**:
- Channel 用于实时监控（开发者/运维）
- Issue Comments 用于持久记录（用户/PM 可订阅）
- 两者互补，不冲突

### D9: Design 阶段触发条件

**选择**: 用户确认触发（OR 条件）

**触发条件**（满足任一即可）:
1. **用户明确说 "可以设计了"** - 直接触发
2. **Issue Body 包含完整任务清单** - 至少 2 个可执行的 `- [ ]` checkbox

**辅助判断**（可选）:
- 在 Refinement 阶段停留 > 5 分钟
- Issue 无未回答的问题

**理由**:
- 避免过早进入 Design（需求不完整）
- 用户有最终控制权
- 自动检测作为辅助

### D10: Ralph Loop 实现细节

**选择**: Orchestrator 循环 + 进度文件持久化

**核心设计**: 无限重试是默认行为，但检测到连续失败模式后会自动标记 blocked 并需要人工干预。

**实现流程**:
```
// Orchestrator 层
while (!success) {
  // 1. 读取进度文件
  progress = read_progress_file(issue_number)
  
  // 2. Spawn 新 sub-agent（干净上下文）
  result = spawn_sub_agent({
    issue_number,
    current_stage: progress.current_stage,
    checkpoints: progress.checkpoints
  })
  
  // 3. 处理结果
  if (result.status === SUCCESS) {
    success = true
    cleanup_progress_file(issue_number)
  } else if (result.status === NEEDS_USER_INPUT) {
    send_channel_notification("需要用户输入")
    wait_for_user_action()
  } else {
    // 失败，更新进度文件
    progress.attempts += 1
    progress.last_error = result.error
    write_progress_file(issue_number, progress)
    send_channel_notification("重试中...")
  }
  
  // 4. 检测连续失败模式（安全阀）
  // 连续失败 10 次后标记 blocked，需人工干预
  if (progress.attempts >= 10) {
    add_label(issue_number, "stage:blocked")
    send_channel_notification("检测到持续失败，已标记为 blocked")
    break  // 跳出循环，等待人工干预
  }
}
```

**进度文件格式** (`~/.openclaw/agents/crawlph/data/progress/issue-{N}.json`):
```json
{
  "issueNumber": 123,
  "currentStage": "implementation",
  "attempts": 3,
  "prNumber": 456,
  "lastError": "TypeScript compilation failed",
  "checkpoints": {
    "exploration": "2024-01-01T00:00:00Z",
    "refinement": "2024-01-01T01:00:00Z",
    "design": "2024-01-01T02:00:00Z"
  },
  "context": {
    "branchName": "issue-123-add-auth",
    "specFile": "openspec/changes/issue-123/spec.md"
  }
}
```

**超时处理**:
- 每个 sub-agent 默认超时: 30 分钟
- 超时后视为失败，触发重试
- 可通过 `--timeout` 参数调整

### D11: OpenSpec 集成策略

**选择**: 自动调用 OpenSpec CLI（如果可用），否则手动生成 specs

**检测逻辑**:
```
if (openspec_cli_available() && version >= MIN_VERSION) {
  // 使用 OpenSpec
  run_in_sub_agent("openspec propose issue-{N}")
  specs_path = "openspec/changes/issue-{N}/"
} else {
  // 手动生成
  run_in_sub_agent("生成 specs/issue-{N}.md")
  specs_path = "specs/issue-{N}.md"
  pr_body += "\n\n**注意**: 未使用 OpenSpec 格式，specs 手动生成"
}
```

**最低版本要求**: OpenSpec CLI >= v1.0.0

### D12: Agent Bindings 和自动启动

**选择**: 通过 bindings 配置实现多入口触发，Agent 启动后自动进入 watch mode

**触发方式**:

| 触发源 | 配置方式 | 说明 |
|--------|----------|------|
| **Telegram Channel** | `bindings` 配置 | 绑定 channel ID 到 crawlph agent |
| **Discord Channel** | `bindings` 配置 | 绑定 channel ID 到 crawlph agent |
| **GitHub Webhook** | Webhook 配置 | 需要实现 webhook endpoint |
| **Main Agent** | `sessions_send` | 用户在 main session 中发送指令 |

**Bindings 配置示例**:
```json5
{
  bindings: [
    // Telegram 频道 → crawlph agent
    {
      agentId: "crawlph",
      match: {
        channel: "telegram",
        peer: { kind: "channel", id: "YOUR_CHANNEL_ID" },
      },
    },
    // Discord 频道 → crawlph agent
    {
      agentId: "crawlph",
      match: {
        channel: "discord",
        peer: { kind: "channel", id: "YOUR_CHANNEL_ID" },
      },
    },
  ],
}
```

**自动启动行为**:
- Agent 启动后立即进入 watch mode
- 不需要手动触发
- 从上次 cursor 位置继续（恢复状态）
- 可以通过发送 "停止" 消息停止 watch mode

**响应外部指令**:
- "状态" → 显示当前状态
- "处理所有 Issues" → 立即检查并处理
- "停止" → 停止 watch mode
- "帮助" → 显示可用命令

## Risks / Trade-offs

### R1: 上下文积累导致性能问题

**风险**: Orchestrator 会话长期运行，上下文可能积累

**缓解**: 
- 每次 iteration 后执行 Context hygiene
- 只保留必要状态（PROCESSED_ISSUES, OPEN_PRS）
- 定期重启 Orchestrator（watch mode）

### R2: GitHub API Rate Limit

**风险**: 频繁调用 GitHub API 可能触发 rate limit

**缓解**:
- 使用 GraphQL API 批量查询
- 缓存 Issue 数据
- 合理设置 watch interval（默认 60s）

### R3: Sub-agent 失败无限重试

**风险**: 如果 Issue 本身无法解决，会无限重试

**缓解**:
- 检测连续失败模式（如 10 次失败）
- 发送警告到 Channel
- 用户可以手动干预（添加 `stage:blocked` label）

### R4: OpenSpec CLI 依赖

**风险**: OpenSpec CLI 版本兼容性问题

**缓解**:
- 在 SKILL.md 中记录最低版本要求
- 启动时检查 OpenSpec CLI 版本

## Migration Plan

### Phase 1: 专用 Agent 配置

1. 创建 crawlph workspace 目录结构
   ```bash
   mkdir -p ~/.openclaw/workspace-crawlph
   mkdir -p ~/.openclaw/agents/crawlph/data/progress
   ```

2. 创建 AGENTS.md（Agent 核心逻辑）
3. 创建 SOUL.md（Agent persona）
4. 创建 TOOLS.md（工具说明）

5. 更新 openclaw.json
   - 添加 crawlph agent 配置
   - 添加 bindings 配置（Telegram/Discord）
   - 配置 GitHub token

6. 重启 Gateway 使配置生效

### Phase 2: Agent 逻辑实现

1. 实现 watch mode 自动检查逻辑
2. 实现 Issue orchestration
3. 实现 7-stage workflow
4. 实现 Ralph Loop
5. 集成 OpenSpec CLI
6. 实现 PR lifecycle
7. 实现进度报告（Channel + Issue Comments）

### Phase 3: 测试和优化

1. 测试各种触发模式（Telegram/Discord/Webhook/Main agent）
2. 测试并发处理
3. 测试失败重试
4. 测试 watch mode 恢复
5. 优化错误处理

### Rollback Strategy

如果出现问题：
1. 从 openclaw.json 移除 crawlph agent 配置
2. 清理 `~/.openclaw/agents/crawlph/` 目录
3. 手动处理未完成的 Issues

## Open Questions

| # | 问题 | 状态 | 决策 |
|---|------|------|------|
| 1 | Watch mode interval: 默认 60 秒是否合适？ | ✅ 已解决 | 60 秒，参考 issue-orchestration spec |
| 2 | Sub-agent 超时: 每个 sub-agent 应该有超时限制吗？ | ✅ 已解决 | 30 分钟，可配置 `--timeout` |
| 3 | Channel 配置: 是否支持多个通知 Channel？ | ✅ 已解决 | 通过 bindings 支持多个 channel |
| 4 | Specs 格式: 如果不使用 OpenSpec，specs 文件应该放在哪里？ | ✅ 已解决 | `specs/issue-{N}.md`，参考 openspec-integration spec |
| 5 | Agent 自动启动: Gateway 重启后是否自动启动 crawlph agent？ | ✅ 已解决 | 是，OpenClaw 会自动启动所有配置的 agents |
| 6 | GitHub Webhook: 如何实现 webhook endpoint？ | ❓ 待定 | 需要调研 OpenClaw 的 webhook 支持 |
