# Runner 指南

Runner 是 Mohist 的执行后端。Server 是大脑（决定做什么），Runner 是手脚（实际执行）。

## 为什么有 Runner

Mohist 的架构原则：**控制平面和执行平面分离**。

- **Server**（控制平面）：维护状态、做决策、推事件
- **Runner**（执行平面）：执行 Action、操作 git、写文件并连接 OpenCode 等执行后端

为什么分开？

- Runner 可以崩溃、重启、替换，不影响 Server 状态
- 未来可以多个 Runner 并行执行（不同机器、不同 capacity）
- Server 不需要信任 Runner 的报告（带 ownership 校验）

## 启动 Runner

```bash
npm run dev:runner
# 或
mo service start runner
```

Runner 启动后会：

1. 连接到本地 Server（默认 `http://localhost:3456`）
2. 注册自己（声明 capacity、能力）
3. 进入"等任务"状态
4. Server 分配 task 时拉起执行

**必须在 Server 之后启动**。Server 没起来，Runner 连不上。

## 检查 Runner 状态

```bash
mo server status
# 输出包含 runner 状态
```

或 Web UI：

- 看板顶部的 Runner 不可用警告条
- Settings → Runtime 看 runner 状态
- Activity 页看 runner 心跳事件

## 并发 Capacity

Runner 有最大并发限制（默认 8）。意思是：

- 同时最多 8 个 task 在执行
- 第 9 个 issue 启动会等待空位

调 capacity：

- Web UI Settings → Runtime
- 或 runner 启动参数（看 `mo service start runner --help`）

**别盲目调高**：

- 每个 AgentSession 执行占 CPU 和内存
- 同时跑太多 → 模型 API 限速、机器负载爆、git lock 冲突
- 个人开发机建议 4-8

## Runner 做的事

每个 task 来了，Runner 会：

1. **准备 workspace**：为 WorkflowRun 创建独立分支和工作目录
2. **渲染 prompt**：用 issue body、artifacts、模板拼出 prompt
3. **执行 Action**：`mohist/opencode` 把本次输入交给已安装的 OpenCode
4. **流式接收**：实时把 Action 执行事实回传 Server（更新 UI）
5. **验证产出**：检查 expect.files 是否存在
6. **回收 workspace**：WorkflowRun 结束后清理（或暂时保留供 debug）

## Workspace 在哪

默认在 Runner 数据目录的 `workspaces/<workflow-run-id>/`。路径和分支都由
WorkflowRun ID 决定，不包含 Issue 标题或仓库名。

这是可重建的执行状态。WorkflowRun 结束后 Runner 会回收；需要保留的代码应先提交到
对应远端分支。不要在 task 运行时手工删除或修改 workspace 的 branch、marker 或 origin。

## Runner 挂了怎么办

如果 Runner 进程崩了：

- 尚未开始执行的 Workflow 会等待可用 Runner
- Mohist 会先尝试自动恢复正在执行的 task
- 自动恢复失败时，Issue 进入 blocked health，并展示原因和推荐的恢复操作

不会丢失 workflow 状态——状态在 Server，不在 Runner。

## 多 Runner（未来）

当前 Mohist 假设单机单 Runner。未来支持：

- 多台机器各跑一个 Runner
- Server 调度 task 到不同 Runner
- 不同 Runner 声明不同能力（如"我会跑 Docker"）

这块在 roadmap。当前别折腾。

## 调试 Runner

### Runner 日志

```bash
mo service logs runner          # runner 受管服务的运维日志（service-manager）
# 或直接看 runner 进程的 stdout
```

### 单个 issue 的执行日志

```bash
mo issue logs <number>
mo issue events <number>     # 事件流
mo session list --issue <number>  # 该 Issue 的 AgentSession 记录
```

### 常见 Runner 问题

| 症状 | 原因 | 解决 |
|---|---|---|
| 看板显示 "No runner is connected" | Runner 没起 | `npm run dev:runner` |
| Issue 启动后一直等待 | 没有可用 Runner | 启动 Runner；Workflow 会自动继续 |
| Task 长时间无输出 | opencode 卡了 | `mo run pause --issue <number>`，查 logs |
| Workspace identity 错误 | marker、branch 或 origin 被手工修改 | 保留需要的提交后移除该 workspace，再 retry |
| Git push 失败 | 远程仓库权限 | 配 SSH key 或 token |

## Runner 配置

Runner 行为可通过环境变量或 config 文件调（看 `mo service start runner --help`）：

- 并发 capacity
- Server URL
- Workspace 路径

Runtime 后端不由 Runner 全局 `type` 选择；Workflow task 的 `uses` 决定用哪个执行后端
Action（`mohist/opencode`、`mohist/pi`）。模型等选项由 Action Input 提供，见
[Action 契约](actions/README.md)。

## Self-host 场景

Runner 长跑（不像 dev:runner 那样前台），看 [Self-host 部署](self-host.md)。

---

对应源码：`packages/runner/`、`packages/server/src/Mohist.Server/Runner/`。
