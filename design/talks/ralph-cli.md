# Ralph CLI 设计

**状态**: 设计阶段  
**创建时间**: 2026-03-06  
**关联 Issue**: #6 (API限流), #7 (验证测试覆盖)

---

## 1. 动机与目标

### 当前问题

#### Issue #6: API 限流卡住

**现象**:
- Design 阶段因 API 限流 (429) 而卡住
- 日志: `"429 该模型当前访问量过大，请您稍后再试"`
- SKILL.md 重试逻辑无法处理框架层面错误

**根本原因**:
- SKILL.md 中的 Ralph Loop 在 sub-agent 层面实现
- 无法捕获和处理框架层面错误（429 限流）
- 缺少统一的错误处理和重试机制

#### Ralph TUI 过重

**问题**:
- 需要 Bun 运行时（不是 Node.js）
- 多 Agent 支持（Claude, OpenCode, Gemini, Codex...）
- TUI 界面（终端 UI）
- Remote 多机器控制

**与 crawlph 不匹配**:
- crawlph 只需要 OpenCode 支持
- crawlph 在 SKILL.md 中调用，不需要 TUI
- crawlph 已用 Node.js，不想引入 Bun

### 设计目标

设计一个**轻量级 Ralph CLI**，解决上述问题：

#### 核心目标

1. **解决 API 限流问题**
   - 统一的错误处理机制
   - 指数退避重试策略
   - 区分可重试和不可重试错误

2. **轻量级设计**
   - 仅支持 OpenCode（单一 Agent）
   - 命令行工具（无 TUI）
   - Node.js 实现（已有环境）

3. **与 crawlph + OpenSpec 协同**
   - 可被 crawlph SKILL.md 调用
   - 支持 OpenSpec 的 tasks.md 格式
   - 可选集成 OpenSpec

#### 非目标

- 不支持多 Agent（Claude, Gemini 等）
- 不提供 TUI 界面
- 不支持 Remote 控制
- 不使用 Bun 运行时

---

## 2. 核心设计

### 2.1 命令设计

```bash
# 主命令
ralph run [options]

# 选项
--tasks, -t      # tasks.md 文件路径
--change, -c     # OpenSpec change 名称
--max-retries, -r    # 最大重试次数 (默认 3)
--base-delay, -d     # 基础延迟 ms (默认 2000)
--timeout, -o        # 单任务超时 ms (默认 1800000)
--verbose, -v        # 详细输出
```

### 2.2 使用示例

```bash
# 方式1: 直接从 tasks.md 执行
ralph run --tasks openspec/changes/my-change/tasks.md

# 方式2: 指定 change 名称
ralph run --change my-change

# 方式3: 自定义重试和超时
ralph run --tasks tasks.md --max-retries 5 --timeout 600000
```

### 2.3 核心流程

```
start: 解析参数
   ↓
加载 tasks.md
   ↓
生成 prd.json (提取 acceptance criteria)
   ↓
while (有未完成任务):
   选择下一个未完成任务
   ↓
   执行 OpenCode (prompt + acceptance criteria)
   ↓
   质量验证 (Quality Gates)
   ↓
   通过? → 更新 passes 状态
   ↓
   保存 prd.json
   ↓
end: 打印完成摘要
```

---

## 3. 核心机制

### 3.1 重试机制

**指数退避 + Jitter 策略**:

```javascript
async function executeWithRetry(task, maxRetries = 3) {
  for (let attempt = 0; attempt < maxRetries; attempt++) {
    const result = await runOpenCode(task.prompt);
    
    if (result.success) return result;
    
    // 区分可重试错误
    if (isRetryableError(result.error)) {
      // 指数退避: 2s, 4s, 8s...
      const delay = Math.min(
        Math.pow(2, attempt) * 1000 + Math.random() * 1000,
        60000 // 最大 60s
      );
      await sleep(delay);
    } else {
      return { success: false, error: result.error };
    }
  }
  return { success: false, error: 'max retries exceeded' };
}
```

**重试延迟计算**:
- 第1次失败: 2s + random(0-1s) = ~2-3s
- 第2次失败: 4s + random(0-1s) = ~4-5s
- 第3次失败: 8s + random(0-1s) = ~8-9s
- 最大延迟: 60s

### 3.2 错误分类

| 错误类型 | 示例 | 可重试 | 处理 |
|---------|------|--------|------|
| **限流** | 429 Too Many Requests | ✅ | 指数退避 |
| **超时** | Request timeout | ✅ | 重试 |
| **服务不可用** | 503 Service Unavailable | ✅ | 重试 |
| **客户端错误** | 400 Bad Request | ❌ | 立即失败 |
| **认证失败** | 401 Unauthorized | ❌ | 立即失败 |

**分类逻辑**:
```javascript
function isRetryableError(error) {
  // 可重试: 429, 503, timeout
  if (error.code === 429 || error.code === 503) return true;
  if (error.code === 'ETIMEDOUT' || error.code === 'ECONNRESET') return true;
  
  // 不可重试: 400, 401, 403, 404
  return false;
}
```

### 3.3 状态持久化

**存储结构**:
```
.ralph-cache/{issue}/
  ├── prd.json        # tasks passes 状态
  ├── checkpoint.json # 断点恢复信息
  └── logs/           # 执行历史
      ├── 2026-03-06-001.log
      └── 2026-03-06-002.log
```

**prd.json 格式**:
```json
{
  "userStories": [
    {
      "id": "task-1",
      "description": "实现 TaskStateMachine",
      "acceptanceCriteria": [
        "支持状态转换",
        "持久化状态"
      ],
      "passes": true
    }
  ]
}
```

**checkpoint.json 格式**:
```json
{
  "currentTask": "task-3",
  "completedTasks": ["task-1", "task-2"],
  "timestamp": "2026-03-06T10:30:00Z"
}
```

**崩溃恢复流程**:
1. 启动时检查 `.ralph-cache/{issue}/checkpoint.json`
2. 如果存在，读取 `currentTask` 和 `completedTasks`
3. 跳过已完成的 tasks，从 `currentTask` 继续
4. 每完成一个 task，立即更新 checkpoint

---

## 4. 与 crawlph 集成

### 4.1 调用方式

crawlph 在 implementation 阶段调用 Ralph CLI:

```yaml
# SKILL.md 中的子代理命令
implementation:
  - name: "Execute tasks with Ralph CLI"
    command: |
      ralph run \
        --tasks "$PROGRESS/specs/$SPEC/tasks.md" \
        --max-retries 3 \
        --timeout 1800000 \
        --verbose
    timeout: 3600000
```

**参数说明**:
- `--tasks`: 从 OpenSpec 生成的 tasks.md 路径
- `--max-retries 3`: 最多重试 3 次
- `--timeout 1800000`: 单任务超时 30 分钟
- `--verbose`: 详细输出，方便调试

### 4.2 集成架构

```
crawlph (SKILL.md)
  ├── 监听 GitHub Issues 标签变化
  ├── ready-for-design → 调用 OpenSpec 生成设计
  ├── ready-for-implement → 调用 Ralph CLI
  ├── 监控 ralph 执行状态
  └── 完成后更新 Issue labels

Ralph CLI
  ├── 读取 tasks.md
  ├── 生成/更新 prd.json
  ├── 循环执行 OpenCode
  ├── 质量验证
  └── 返回执行结果给 crawlph
```

**状态同步**:
- Ralph CLI 通过 exit code 返回状态
  - `0`: 成功完成
  - `1`: 失败（可重试）
  - `2`: 失败（不可重试）
- crawlph 根据状态更新 Issue labels

### 4.3 与 OpenSpec 协同

**OpenSpec 在流程中的位置**:
- **设计阶段**: 管理 proposal/design/tasks/specs
- **执行阶段**: 仅提供 tasks.md 作为输入
- **不执行验证**: OpenSpec 的验证是 AI slash 命令，不是 CLI

**tasks.md → prd.json 转换**:
```markdown
# tasks.md
- [ ] 实现 TaskStateMachine
- [ ] 添加单元测试
```

↓ Ralph CLI 转换 ↓

```json
{
  "userStories": [
    {
      "id": "task-1",
      "description": "实现 TaskStateMachine",
      "acceptanceCriteria": ["实现状态机"],
      "passes": false
    },
    {
      "id": "task-2",
      "description": "添加单元测试",
      "acceptanceCriteria": ["测试覆盖核心逻辑"],
      "passes": false
    }
  ]
}
```

---

## 5. 验收标准

- [ ] 能正确解析 tasks.md checkbox 格式
- [ ] 按 tasks.md 顺序调度任务
- [ ] 失败后指数退避重试
- [ ] 实时保存 checkbox 状态
- [ ] 支持崩溃恢复
- [ ] CLI 参数解析正确
- [ ] 错误信息清晰可读
- [ ] 正确区分可重试和不可重试错误
- [ ] 与 crawlph 集成测试通过

---

## 6. 参考资料

- Ralph TUI: https://github.com/richelbilderbeek/ralph-tui
- Smart Ralph: https://github.com/tzachbon/smart-ralph
- OpenSpec: https://github.com/Fission-AI/OpenSpec
- 相关调研: [workflow-research.md](./workflow-research.md)
