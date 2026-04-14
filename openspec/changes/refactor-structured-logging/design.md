## Context

mohist 当前有 244 处 `console.log/error/warn` 散落在 30+ 个源文件中，分布在 CLI 命令、server、agent runner、workflow 等模块。唯一的"日志文件"是 server daemon 模式下用 `fs.openSync` 将 stdout/stderr 重定向到 `~/.mohist/logs/server.log`（追加模式，无轮转）。

opencode 项目有成熟的 `Log` 命名空间实现（`opensrc/opencode/packages/opencode/src/util/log.ts`，182 行），零外部依赖，已在生产环境验证。其核心特性：4 级别过滤、`key=value` 结构化格式、命名 logger 缓存、tag 链式调用、`time()` 自动计时、文件输出 + 自动轮转。

当前痛点：
- Issue #2 在 11 秒后 blocked，无法从日志中定位原因
- `workflow_log` 表为空 — `setStatus()` 和 `transitionToStage()` 不写日志
- server daemon 日志无轮转，单文件无限增长
- CLI 的 `console.log` 输出与日志混在一起，无法分离

## Goals / Non-Goals

**Goals:**
- 引入 `src/util/log.ts`，借鉴 opencode 的 `Log` 命名空间设计
- 替换全部 `console.*` 调用为结构化日志
- 默认写文件，支持级别过滤和自动轮转
- Agent 执行路径上的关键事件可追踪（issue number、stage、耗时）
- CLI 命令的交互式输出（表格、状态信息）保持 console 输出不变

**Non-Goals:**
- 不引入外部日志依赖（winston、pino 等）
- 不改变 `workflow_log` 表的设计（那是业务事件持久化，与日志是不同关注点）
- 不做日志远程传输或集中式日志收集
- 不做 JSON 格式日志输出（保持 key=value 人类可读格式）
- 不改 Electron/desktop 相关代码（mohist 没有）

## Decisions

### Decision 1: 直接移植 opencode 的 `log.ts`，做最小适配

**选择**: 将 `opensrc/opencode/packages/opencode/src/util/log.ts` 移植到 `packages/cli/src/util/log.ts`，替换依赖（`Global.Path.log` → `~/.mohist/logs/`，`Glob.scan` → `fs.readdir` + glob 手动过滤，移除 `zod` 依赖）。

**理由**: opencode 的实现已经过验证，182 行代码，API 设计简洁。不引入任何新依赖。

**备选方案**:
- 用 pino/winston 等成熟库 → 引入外部依赖，API 风格与项目不匹配
- 从零实现 → 重复造轮子，opencode 的实现已经很好

### Decision 2: CLI 交互式输出保留 console，操作日志走 Log

**选择**: CLI 命令中面向用户的输出（表格、状态信息、`chalk` 着色文本）继续用 `console.log`。内部操作日志（API 调用、数据库操作、agent 状态变化）用 `Log`。

**分类规则**:
```
console.log/error/warn  →  保留的场景：
  - CLI 命令的用户交互输出（issue list 表格、server status 等）
  - chalk 着色的终端输出

Log.info/error/warn/debug  →  迁移的场景：
  - server 端所有日志
  - agent runner 状态变化
  - workflow 状态转换
  - 数据库操作
  - HTTP 请求日志
  - 错误处理（catch 块中的 console.error）
  - 子进程管理（spawn opencode）
```

**理由**: CLI 工具的输出面向终端用户，需要格式化和着色。日志面向开发者诊断，需要结构化和持久化。两者职责不同。

### Decision 3: 日志文件路径与轮转策略

**选择**:
- 目录: `~/.mohist/logs/`
- 文件名: `<ISO-timestamp>.log`（如 `2026-04-14T100354.log`）
- Dev 模式: 固定 `dev.log`，每次启动 truncate
- 轮转: 保留最近 10 个文件，启动时清理

**理由**: 与 opencode 保持一致，按会话隔离日志文件，便于定位特定时间段的日志。

### Decision 4: Server daemon 模式改造

**选择**: 
- 移除 server.ts 中的 `fs.openSync` + `spawn(detached)` **stdout** 重定向
- **保留 stderr 重定向**到 `~/.mohist/logs/server.log`，作为 `Log.init()` 之前的兜底错误捕获
- Server 子进程在 `main()` 中通过 `Log.init()` 直接写结构化日志文件
- `mo server logs` 和 `mo server status` 适配为读取最新的时间戳日志文件（`getLatestLogFile()` 辅助函数）

**理由**: 
- 当前 stdout 重定向将所有 console 输出（包括无关信息）都写入了同一文件
- 改为 Log 模块直接写文件后，结构化日志更干净
- 但如果完全移除 stderr 重定向，`Log.init()` 之前发生的异常（如 `loadConfig()` 失败）会完全静默，导致 `mo server start` 失败但无迹可寻
- 保留 stderr 兜底可以捕获 `Log.init()` 之前的 console.error

**具体改动**:
```typescript
// server.ts
const child = spawn(process.execPath, [serverPath], {
  detached: true,
  stdio: ['ignore', 'ignore', logStream],  // stdin/stdout ignore, stderr → 兜底文件
  cwd: process.cwd()
});
```

```typescript
// server logs/status 中新增
function getLatestLogFile(): string | null {
  const logsDir = path.join(process.env.HOME || '', '.mohist', 'logs');
  const files = fs.readdirSync(logsDir)
    .filter(f => /^\d{4}-\d{2}-\d{2}T\d{6}\.log$/.test(f))
    .sort();
  return files.length > 0 ? path.join(logsDir, files[files.length - 1]) : null;
}
```

### Decision 5: Logger 命名约定

**选择**: 按模块命名 service，建立一致的命名空间：

| service 名 | 对应模块 |
|------------|---------|
| `server` | HTTP server、middleware |
| `agent-runner` | AgentRunnerService |
| `workflow` | WorkflowService、WorkflowController |
| `issue` | IssueService、issues API |
| `session` | SessionManager、ACP session |
| `spawn-coder` | spawn_coder tool |
| `project` | ProjectManager |
| `worktree` | WorktreeManager |
| `db` | 数据库操作 |

**理由**: 按 service 名称过滤日志，快速定位问题模块。

### Decision 6: Log.init() 的调用位置和时机

**选择**: 在 `src/server/index.ts` 的 `ensureDataDir()` 之后、`loadConfig()` 之前调用 `Log.init()`。

```typescript
async function main(): Promise<void> {
  ensureDataDir();
  
  await Log.init({
    print: process.argv.includes('--print-logs'),
    dev: process.env.NODE_ENV === 'development',
    level: process.env.LOG_LEVEL || 'INFO',
  });
  
  // 之后的 loadConfig()、服务初始化、server.start() 全部走 Log
}
```

**理由**:
- `ensureDataDir()` 之后：`~/.mohist/logs/` 目录已存在，不会写入失败
- `loadConfig()` 之前：配置加载失败的异常能被日志记录，而不是完全静默
- 后续所有服务初始化代码都能输出结构化日志

### Decision 7: 新增 log.level 配置

**选择**: 在 `config-schema.ts` 中新增 `log` 块，支持 `log.level` 字段（DEBUG/INFO/WARN/ERROR）。server 启动时优先使用 `config.jsonc` 中的配置，其次环境变量 `LOG_LEVEL`，最后默认 INFO。

```typescript
// config-schema.ts
log: z.object({
  level: z.enum(['DEBUG', 'INFO', 'WARN', 'ERROR']).optional(),
}).strip().optional(),

// config-loader.ts
export function getLogConfig(config: ConfigInfo) {
  return {
    level: config.log?.level ?? 'INFO',
  };
}

// server/index.ts
const logConfig = getLogConfig(fileConfig);
await Log.init({
  level: logConfig.level,
  // ...
});
```

**理由**: 让用户能通过 `config.jsonc` 控制日志详细程度，不需要改代码或重启环境变量。

### Decision 8: unhandledRejection 的分阶段注册

**选择**: 
- 在 `Log.init()` **之前**注册一个兜底 `unhandledRejection` handler，用 `console.error`（此时 stderr 仍被重定向到底层文件）
- 在 `Log.init()` **之后**再注册（或替换）一个走 `Log.Default.error()` 的 handler

```typescript
// 兜底 handler（Log.init 之前）
process.on('unhandledRejection', (reason) => {
  console.error('[FATAL] Unhandled Promise Rejection:', reason);
});

// Log.init 之后
process.removeAllListeners('unhandledRejection');
process.on('unhandledRejection', (reason) => {
  Log.Default.error('Unhandled Promise Rejection', { reason });
});
```

**理由**: 确保 Log.init() 前后的任何 unhandledRejection 都不会被吞掉。

## Risks / Trade-offs

**[风险] `time()` 的 `Symbol.dispose` 需要 Node 22+ 或 `--harmony-using-top-level`** → mohist 项目已使用 `using` 语法（AGENTS.md 中的 opencode AGENTS.md 提到），如果运行时不支持，用 `timer.stop()` 手动调用即可。

**[风险] 迁移 244 处 console 调用可能引入遗漏** → 分批迁移，先迁移 server 端和 agent runner（诊断最需要的），CLI 端可以后续处理。

**[风险] 日志文件路径从 `server.log` 变为 `<timestamp>.log`** → `mo server logs` 命令已设计适配，读取最新的日志文件而非固定文件名。

**[权衡] CLI 交互式输出不迁移到 Log** → 保留了 `console.log` 在 CLI 中的使用，但这部分输出不需要持久化和结构化，是合理的分离。
