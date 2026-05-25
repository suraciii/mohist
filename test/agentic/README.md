# Agentic Testing

Container-isolated end-to-end tests for mohist.

## Structure

```
test/agentic/
├── README.md
├── AGENTS.md
├── shared/                           # Shared infrastructure
│   ├── Containerfile                 # Base container (.NET SDK + Web UI build)
│   └── entrypoint.sh                 # Server startup
└── verify-<feature>/                 # Per-test
    ├── TESTPLAN.md                   # Agent-readable test plan (natural language + @ references)
    └── scripts/                      # Helper scripts (each does ONE thing)
        └── <name>.sh
```

## Commands

```bash
/test-create <feature>    # 创建测试计划
/test-run <feature>       # 构建容器，执行测试计划
```

## TESTPLAN.md Convention

TESTPLAN.md 是 **agent 阅读并执行的测试计划**。

- 以自然语言描述每个 Phase 的步骤和预期结果
- Agent 自行执行简单命令（`curl`、API calls、`which` 等）
- 只在需要复杂确定性操作时，用 `@scripts/<name>.sh` 调用辅助脚本

Example:
```markdown
## Phase 5: Data Persistence

1. 记录当前 issue 数量
2. @scripts/restart-server.sh
3. 验证数据完整
```

## scripts/ Convention

每个脚本只做 **一件事**，命名自解释：

```bash
scripts/restart-server.sh   # 停止 Mohist.Server，重启，等待健康检查通过
```

脚本应幂等、有明确退出码（0=成功，1=失败）、输出简明状态信息。

## Creating a New Test

1. `/test-create <feature>` — 自动探索代码库并生成 TESTPLAN.md + scripts
2. 或手动：`mkdir -p test/agentic/verify-<feature>/scripts`，写 TESTPLAN.md 和脚本
