# Agentic Testing

Container-isolated end-to-end tests for mohist.

## Structure

```
test/agentic/
├── README.md
├── AGENTS.md
├── shared/                           # Shared infrastructure
│   ├── Containerfile                 # Base container (Node.js + mohist build)
│   └── entrypoint.sh                 # Server startup
└── verify-<feature>/                 # Per-test
    ├── TESTPLAN.md                   # Agent-readable test plan (natural language + @ references)
    ├── scripts/                      # Helper scripts (each does ONE thing)
    │   └── <name>.sh
    └── run.sh                        # podman build + run
```

## TESTPLAN.md Convention

TESTPLAN.md 是 **agent 阅读并执行的测试计划**。

- 以自然语言描述每个 Phase 的步骤和预期结果
- Agent 自行执行简单命令（`mo issue create`、`curl`、`which` 等）
- 只在需要复杂确定性操作时，用 `@scripts/<name>.sh` 调用辅助脚本

Example:
```markdown
## Phase 5: Data Persistence

1. 记录当前 issue 数量
2. @scripts/restart-server.sh
3. 验证数据完整
```

Agent 读懂"重启 server 并验证数据"，但进程管理（kill + 重启 + 等待健康检查）
这种复杂操作委托给脚本。

## scripts/ Convention

每个脚本只做 **一件事**，命名自解释：

```bash
scripts/restart-server.sh   # 停止 mo-server，重启，等待健康检查通过
```

脚本应幂等、有明确退出码（0=成功，1=失败）、输出简明状态信息。

## Running Tests

```bash
cd test/agentic/verify-m1-infra
bash run.sh            # 构建容器，启动交互式 shell（server 已在运行）
```

容器内 agent 可读取 `/app/TESTPLAN.md` 并按步骤执行。

## Creating a New Test

1. `mkdir -p test/agentic/verify-<feature>/scripts`
2. Write `TESTPLAN.md` — 自然语言测试计划，复杂操作用 `@scripts/<name>.sh`
3. Write `scripts/<name>.sh` — 每个只做一件事
4. Write `run.sh` — podman build + run

Layer B (需要 opencode) 时，加 per-test Containerfile：

```dockerfile
FROM mohist-test
COPY test/agentic/shared/bin/opencode /usr/local/bin/opencode
```
