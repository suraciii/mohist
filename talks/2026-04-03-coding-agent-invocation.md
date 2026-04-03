# 2026-04-03 mohist 如何调用 opencode

## 背景

mohist 需要搞清楚如何调用 opencode（coding agent）来完成 issue 的 plan/build/check 阶段。当前 `spawn_agent` 使用 `opencode agent --local --message "..."` 命令，但这个命令在实际的 opencode (v1.3.3) 中不存在。

## 决策记录

1. **~~M1 使用 `opencode run`，后续迁移到 `opencode acp`~~ → M1 直接使用 `opencode acp`**（见下方决策变更）
2. **worktree 使用 git worktree，自带完整代码上下文**
3. **opencode 使用用户已安装的版本**（从 PATH 查找，不内置）
4. **Prompt 策略**: mohist 内置 prompt 模板，通过 task message 注入；后续支持用户自定义或扩展 agent 指令

## openclaw 的两套 agent 调用机制

### 机制 1: CLI Backend (subagent-spawn.ts + cli-runner/)

- **方式**: spawn 外部 CLI 子进程（claude/codex/gemini），同步等待退出
- **CLI Backend 配置**: 每个 backend 定义 `command`、`args`、`modelArg`、`systemPromptArg`、`sessionArg` 等
- **关键特性**:
  - supervisor.spawn 子进程，cwd = workspaceDir
  - 支持 session 复用（--session-id）
  - 支持 system prompt 注入（--append-system-prompt）
  - 输出模式灵活（jsonl/text）
  - 有 no-output-timeout watchdog
- **参考**: claude-cli backend 配置
  ```
  command: claude
  args: ["-p", "--output-format", "stream-json", "--verbose", "--permission-mode", "bypassPermissions"]
  modelArg: --model
  sessionArg: --session-id
  systemPromptArg: --append-system-prompt
  systemPromptWhen: first
  ```

### 机制 2: ACP (acp-spawn.ts + acp/client.ts)

- **方式**: 通过 ACP 协议 (JSON-RPC over stdio) 双向通信
- **关键特性**:
  - 持久 session，可多次 prompt
  - 实时流式输出（agent_message_chunk, tool_call_update）
  - 动态切换 agent/mode/model
  - 权限审批机制
  - 自动 announce 机制（子 agent 结果推送到父 session）
- **子 agent 结果回传**: 异步 fire-and-forget，完成后自动 announce 到父 session
- **参考**: `npx -y opencode-ai acp` 是 opencode 的 ACP 入口

## opencode 的两种可用接口

### `opencode run` (单次执行)

```bash
opencode run "task" \
  --agent build          # 选择 agent 类型
  --model provider/model # 覆盖模型
  --format json          # 机器可读输出
  --dir /path            # 工作目录
  --thinking             # 显示思考过程
  --variant high         # 推理强度
  --session <id>         # 复用 session
  --continue             # 继续上次 session
  --fork                 # fork session
```

- 完整的 agentic loop（多轮 tool call）
- session idle → 进程退出
- **没有** `--local` 参数
- **没有** `--system-prompt` 参数
- 配置发现：walk up from cwd to worktree root，查找 AGENTS.md、.opencode/、opencode.json

### `opencode acp` (持久会话，双向流)

```bash
opencode acp --cwd /path
```

- ACP 协议 (JSON-RPC over stdio) 通信
- 支持: session/new, session/prompt, session/setMode, session/setModel, session/cancel, session/fork, session/list
- 实时事件流: agent_message_chunk, agent_thought_chunk, tool_call/tool_call_update, requestPermission
- 使用 `@agentclientprotocol/sdk` v0.14.1

### opencode 配置发现机制

```
cwd/ (walk up to worktree root)
├── opencode.json       # 项目配置 (model, agent, instructions)
├── .opencode/
│   ├── opencode.json   # 目录级配置
│   ├── agent/
│   │   ├── build.md    # build agent 定义 (含 system prompt)
│   │   └── *.md        # 自定义 agent
│   ├── command/*.md    # 自定义命令
│   └── plugin/*.{ts,js} # 本地插件
├── AGENTS.md           # 自动注入 system prompt
├── CLAUDE.md           # 同上 (兼容)
└── CONTEXT.md          # 同上

~/.config/opencode/
├── opencode.json       # 全局配置
└── AGENTS.md           # 全局 agent 指令
```

## mohist 需要修复的问题

### 阻塞性

1. **spawn_agent 命令错误**: `opencode agent --local --message` 不存在，应改为 `opencode run "task" --agent build --format json`
2. **agent_type 不匹配**: mohist 用 `"code"`，opencode 叫 `"build"`

### 设计性

1. **System prompt 注入策略**: mohist 内置 prompt 模板，通过 task message 传入 opencode
   - M1: 内置 stage-specific prompt 模板（plan/build/check），拼接 issue 信息后作为 task message
   - M2+: 支持用户通过配置自定义 prompt 模板，或扩展 agent 指令
2. **--format json 输出格式**: 需要搞清楚 JSON 输出的结构，影响 mohist 解析结果的能力
3. **后续 ACP 迁移**: M1 用 run，M2+ 迁移到 acp 获得流式输出和持久 session
4. **opencode 版本**: 使用用户已安装的版本（从 PATH 查找），不内置依赖

### worktree

git worktree 自带完整代码上下文，不需要特殊处理。如果目标项目在 git 里追踪了 .opencode/ 和 AGENTS.md，worktree 自动继承。

## 演进路线

```
M1: opencode acp (异步，流式，持久 session)
    spawn opencode acp --cwd <worktree>
    通过 stdio JSON-RPC 通信 (ACP 协议)
    一个 session 贯穿 plan → build → check
    实时流式进度 (session/update 通知)

M2+: 更丰富的 ACP 能力利用
    session/fork → 并行尝试多个方案
    session/setModel → 动态切换模型
    session/setMode → 切换 agent 模式
    mo attach → 用户实时查看子 agent 输出
```

## 决策变更: 从 opencode run 切换到 opencode acp

### 原计划: M1 用 run, M2+ 用 acp

### 变更原因

1. **上下文连贯性**（核心原因）: run 模式每次启动新进程，LLM 上下文从零开始。plan → build → check 是同一件事的三个阶段，plan 的理解和 build 的改动对 check 至关重要。run 模式要么失忆要么靠重复传上下文（脆弱、费 token）。acp 的 session 天然跨 prompt 保留上下文。

2. **opencode run 有 bug**: `OPENCODE_SERVER_PASSWORD` 环境变量导致 run 无法工作。acp 的 stdio 通道不受此影响（实测验证通过）。

3. **进度可见性**: run 只能等退出后看 JSONL。acp 有 `session/update` 实时通知（text chunk、tool progress、reasoning）。

4. **取消能力**: run 无法中途取消。acp 有 `session/cancel`。

5. **openclaw 的经验**: openclaw 用 acp，不 用 run，说明 acp 在生产环境中更成熟。

### run vs acp 对比

| 维度 | opencode run | opencode acp |
|------|-------------|-------------|
| 执行模式 | 同步，阻塞 | 异步，长驻进程 |
| 上下文 | 每次从零开始 | session 内保留 |
| 进度 | 退出后看 JSONL | 实时流式通知 |
| 取消 | 不支持 | session/cancel |
| agent 切换 | 启动时固定 | session/setMode |
| 多 prompt | 每次新进程 | 同一 session 多轮 |
| OPENCODE_SERVER_PASSWORD | ❌ 必须清 env | ✅ 不受影响 |

### acp 的实际架构 (v1.3.13)

```
opencode acp --cwd <worktree>
  │
  ├── 内部 HTTP server (随机端口)
  │   └── opencode SDK 用它跟自身通信
  │       (有 OPENCODE_SERVER_PASSWORD 问题，但 ACP 层不受影响)
  │
  └── stdio JSON-RPC (ACP 协议)
      ├── stdin ← JSON-RPC requests
      └── stdout → JSON-RPC responses + notifications
```

### ACP 协议流程 (mohist 视角)

```
mohist                           opencode acp
  │                                    │
  │  spawn --cwd <worktree>            │
  │────────────────────────────────────│
  │                                    │
  │  initialize (协议版本协商)           │
  │───────────────────────────────────▶│
  │◀───────────────────────────────────│
  │                                    │
  │  session/new (创建 session)         │
  │───────────────────────────────────▶│
  │◀───────────────────────────────────│ sessionId
  │                                    │
  │  session/prompt ("plan阶段任务...")  │
  │───────────────────────────────────▶│
  │◀─ session/update: agent_message... │  ← 实时流式通知
  │◀─ session/update: tool_call...     │
  │◀─ session/update: agent_message... │
  │◀───────────────────────────────────│ prompt response
  │                                    │
  │  session/prompt ("build阶段任务...") │  ← 同一 session，上下文保留
  │───────────────────────────────────▶│
  │◀─ session/update: ...              │
  │◀───────────────────────────────────│
  │                                    │
  │  session/prompt ("check阶段任务...") │  ← build 改动的上下文还在
  │───────────────────────────────────▶│
  │◀─ session/update: ...              │
  │◀───────────────────────────────────│
  │                                    │
  │  kill 子进程                        │
  │────────────────────────────────────│
```

### ACP session/update 通知类型

| update 类型 | 说明 |
|-------------|------|
| `agent_message_chunk` | LLM 文本输出片段 |
| `agent_thought_chunk` | LLM 思维过程片段 |
| `tool_call_update` | 工具调用进度更新 |
| `tool_call` | 工具调用完成 |
| `plan` | 计划内容更新 |
| `usage_update` | token 用量和费用 |

注意: `src/acp/README.md` 说 streaming 未实现，但实际源码已实现（README 过时）。

### mohist 侧实现要点

1. **spawn**: `spawn("opencode", ["acp", "--cwd", worktreePath])`
2. **SDK**: 使用 `@agentclientprotocol/sdk` 的 Client 端连接 stdio
3. **环境变量**: 建议清 `OPENCODE_SERVER_PASSWORD` 和 `OPENCODE_SERVER_USERNAME`（内部 HTTP server 可能受影响）
4. **生命周期**: 一个 issue 一个 acp 子进程，一个 session 贯穿 plan → build → check
5. **进度**: 订阅 `session/update` 通知，转发到 mohist 的状态系统
6. **退出**: 工作流完成后 kill 子进程

## 实测验证 (Phase 0)

### 环境

- opencode v1.3.13 (实际安装版本，比之前假设的 v1.3.3 新)
- 安装位置: `~/.opencode/bin/opencode`
- 运行环境: Linux, 非交互式子进程

### Bug: OPENCODE_SERVER_PASSWORD 导致 opencode run 失败

**现象**: `opencode run` 在设置了 `OPENCODE_SERVER_PASSWORD` 环境变量时立即报错 `Session not found`。

**根因**: `opencode run` 内部通过 `bootstrap()` 启动一个内部 HTTP server（`http://opencode.internal`），然后创建 SDK client 连接它。但内部 server 的 auth 中间件会读取 `OPENCODE_SERVER_PASSWORD` 环境变量并启用认证，而 `run.ts` 创建 SDK client 时没有传递凭据，导致所有 API 请求被拒（401）。

```
run.ts:672
  const sdk = createOpencodeClient({ baseUrl: "http://opencode.internal", fetch: fetchFn })
  // ❌ 没有 headers, 没有 password!

server.ts:102-103
  const password = Flag.OPENCODE_SERVER_PASSWORD
  if (!password) return next()  // ← 这里拦截了所有请求
```

**验证**: 去掉 `OPENCODE_SERVER_PASSWORD` 和 `OPENCODE_SERVER_USERNAME` 后 `opencode run` 正常工作。

**acp 影响**: `opencode acp` 也启动内部 HTTP server（`acp.ts:26` 的 `Server.listen()`），理论上也受影响。但 ACP 协议层走 stdio JSON-RPC，不经过内部 HTTP server，实测 acp 在有 auth env 时能正常 `setup connection`。建议 spawn 时仍清除这两个变量以避免内部通信问题。

**mohist 对策**: spawn opencode 子进程时清除这两个环境变量：
- `OPENCODE_SERVER_PASSWORD`
- `OPENCODE_SERVER_USERNAME`

### JSON 输出格式已确认

`opencode run --format json` 输出 JSONL（每行一个 JSON 对象），实测输出：

```json
{"type":"step_start","timestamp":1775149860442,"sessionID":"ses_...","part":{"id":"prt_...","messageID":"msg_...","sessionID":"ses_...","type":"step-start"}}
{"type":"text","timestamp":1775149860715,"sessionID":"ses_...","part":{"id":"prt_...","messageID":"msg_...","sessionID":"ses_...","type":"text","text":"hello world","time":{"start":1775149860714,"end":1775149860714}}}
{"type":"step_finish","timestamp":1775149860720,"sessionID":"ses_...","part":{"id":"prt_...","reason":"stop","messageID":"msg_...","sessionID":"ses_...","type":"step-finish","tokens":{"total":15364,"input":15247,"output":34,"reasoning":30,"cache":{"write":0,"read":83}},"cost":0}}
```

事件类型：

| type | 说明 | part 关键字段 |
|------|------|--------------|
| `step_start` | LLM 开始一轮推理 | `type: "step-start"` |
| `step_finish` | LLM 一轮推理结束 | `reason: "stop"\|"tool_use"`, `tokens: {...}`, `cost` |
| `text` | LLM 输出文本（已完成） | `text`, `time: {start, end}` |
| `tool_use` | 工具调用完成 | `tool`, `state: {status, input, output}` |
| `error` | 错误 | `error: {name, data: {message}}` |

关键行为：
- 进程正常退出 exit code = 0
- 即使发生 error 事件，exit code 仍为 0（error 通过 JSON 事件传递）
- `step_finish.reason = "stop"` 表示 LLM 认为任务完成，session 进入 idle
- `step_finish.reason = "tool_use"` 表示 LLM 调用了工具，会继续推理
- session 进入 idle 后进程退出（run.ts:539-542 的 `session.status === "idle"` break）

### 其他发现

- `--agent build` 如果 build agent 不存在会 fallback 到 default agent（不会报错）
- `--model google/gemini-2.0-flash` 不存在，会返回 error 事件；不传 model 则使用配置默认模型
- `--dir` 通过 `process.chdir()` 实现，在 bootstrap 之前执行

## 关键代码参考

- openclaw CLI Backend: `opensrc/openclaw/src/agents/cli-runner/execute.ts`
- openclaw CLI Backend helpers: `opensrc/openclaw/src/agents/cli-runner/helpers.ts`
- openclaw ACP spawn: `opensrc/openclaw/src/agents/acp-spawn.ts`
- openclaw ACP client (真实 spawn): `opensrc/openclaw/src/acp/client.ts`
- openclaw subagent announce: `opensrc/openclaw/src/agents/subagent-announce.ts`
- opencode run command: `opensrc/opencode/packages/opencode/src/cli/cmd/run.ts`
- opencode acp command: `opensrc/opencode/packages/opencode/src/cli/cmd/acp.ts`
- opencode ACP agent: `opensrc/opencode/packages/opencode/src/acp/agent.ts`
- opencode config loading: `opensrc/opencode/packages/opencode/src/config/config.ts`
- opencode instruction discovery: `opensrc/opencode/packages/opencode/src/session/instruction.ts`
