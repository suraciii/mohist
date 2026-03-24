# crawlph 产品文档

**状态**: 草稿  
**创建时间**: 2026-03-24  
**版本**: 0.1

---

## 1. 产品定位

### 1.1 愿景

**一人成军的开发流水线**

让一个人能够像一支团队一样工作：同时处理多个任务，自动化重复工作，只在关键决策点介入。

### 1.2 核心价值

| 价值 | 描述 |
|------|------|
| 并行 | 同时处理多个 Issues，而不是串行排队 |
| 自动化 | 设计、实现、测试自动化，用户只审查 |
| 控制 | 用户在关键决策点拥有完全控制权 |
| 可视 | 全局状态一目了然，随时知道进展 |

### 1.3 目标用户

- 独立开发者
- 小团队的技术负责人
- 需要同时推进多个任务的开发者

### 1.4 产品形态

| 阶段 | 形态 | 特点 |
|------|------|------|
| 初期 | CLI 工具 | 用户触发执行，本地存储状态 |
| 后期 | CLI + Server | 后台持续运行，支持通知推送 |

---

## 2. 核心概念

### 2.1 Issue

GitHub Issue，代表一个需求或任务。

crawlph 管理 Issue 的整个生命周期：从启动到完成。

### 2.2 Stage（阶段）

Issue 在处理过程中会经历以下阶段：

```
draft → refining → designing → implementing → reviewing → done
```

| 阶段 | 描述 | 用户介入 |
|------|------|----------|
| draft | 初始状态，等待启动 | 无 |
| refining | 分析需求，完善任务清单 | 可能需要对话 |
| designing | 生成设计文档，创建 Design PR | 无 |
| waiting-design-review | 等待用户审查设计 | **需要审查** |
| implementing | 执行任务，实现代码 | 无 |
| waiting-review | 等待用户审查实现 | **需要审查** |
| merging | 合并 PR | 无 |
| done | 完成 | 无 |

**特殊状态**（可发生在任何阶段）：

| 状态 | 描述 |
|------|------|
| paused | 用户手动暂停 |
| blocked | 遇到技术阻塞（如缺少 API Key） |
| conflict | 与其他 Issue 冲突 |
| waiting-dep | 等待依赖的 Issue 完成 |

### 2.3 PR（Pull Request）

一个 Issue 对应一个 PR（单 PR 模式）：

- PR 初始包含设计文档
- 实现阶段逐步添加代码
- 最终 PR 包含设计 + 完整实现

### 2.4 Checkpoint（检查点）

需要用户介入的关键时刻：

1. **Design PR 审查** - 确认设计方向正确
2. **Impl PR 审查** - 确认实现质量
3. **冲突解决** - 决定处理策略
4. **Blocker 处理** - 解决技术阻塞

### 2.5 Conflict（冲突）

多个 Issue 同时修改同一文件时产生冲突。

用户需要选择解决策略：
- 串行：先完成一个，另一个 rebase 后继续
- 并行：同时开发，合并时手动解决

### 2.6 Dependency（依赖）

Issue A 依赖 Issue B 时，A 必须等 B 完成后才能启动。

---

## 3. 用户场景

### 3.1 场景 1：启动新项目

**目标**：从零开始构建一个产品

**用户操作**：
```
$ crawlph start "博客系统"
```

**系统响应**：
1. 分析需求，识别核心模块
2. 创建 Epic Issue 和子 Issues
3. 分析依赖关系
4. 展示执行计划（哪些并行，哪些串行）
5. 等待用户确认

**用户决策**：确认启动计划

### 3.2 场景 2：并行开发多个功能

**目标**：同时开发"用户登录"和"文章管理"

**用户操作**：
```
$ crawlph start #101 #102
```

**系统响应**：
1. 启动两个 Issue 的处理流程
2. 分别生成设计文档
3. 分别创建 Design PR
4. 检测潜在冲突
5. 如果有冲突，提示用户选择策略

**用户决策**：审查设计、解决冲突

### 3.3 场景 3：查看全局状态

**目标**：了解所有 Issue 的当前状态

**用户操作**：
```
$ crawlph status
```

**系统响应**：
```
┌────────────────────────────────────────────────────────┐
│ crawlph 状态 - 14:30                                   │
├────────────────────────────────────────────────────────┤
│                                                        │
│ 需要你的关注                                           │
│ ─────────────────────────────────────────────────────  │
│ • #102 Design PR 等待审查                              │
│ • #103 与 #101 冲突，需要决策                          │
│                                                        │
├────────────────────────────────────────────────────────┤
│                                                        │
│ 进行中                                                 │
│ ─────────────────────────────────────────────────────  │
│ #101 用户登录                                          │
│     ████████░░ implementing (task 3/5)                 │
│                                                        │
│ #102 文章管理                                          │
│     ████░░░░░░ waiting-design-review                   │
│                                                        │
└────────────────────────────────────────────────────────┘
```

### 3.4 场景 4：审查设计

**目标**：审查 Design PR，确认设计方向

**用户操作**：
```
$ crawlph review #201
```

**系统响应**：打开浏览器，展示 Design PR

**用户审查**：
- 如果满意：`crawlph approve #201`
- 如果需要修改：`crawlph request-changes #201 "用 JWT 而不是 session"`

### 3.5 场景 5：解决冲突

**目标**：处理 Issue 间的代码冲突

**系统检测到冲突后**：

```
检测到冲突:
  #101 修改 src/auth.js (添加 JWT 验证)
  #102 计划修改 src/auth.js (添加权限检查)

建议:
  A. 先完成 #101，然后 #102 rebase 后继续
  B. 先完成 #102，然后 #101 rebase 后继续
  C. 同时进行，合并时手动解决

请选择: A / B / C
```

**用户决策**：选择策略

### 3.6 场景 6：处理依赖

**目标**：Issue #103 依赖 #101

**Issue Body 中声明依赖**：
```markdown
## Dependencies
Depends on: #101
```

**系统行为**：
1. #103 启动时检测到依赖
2. 检查 #101 状态
3. 如果 #101 未完成，#103 进入 waiting-dep 状态
4. #101 完成后，自动启动 #103

---

## 4. 功能规格

### 4.1 CLI 命令

#### 启动命令

| 命令 | 描述 |
|------|------|
| `crawlph init` | 初始化项目 |
| `crawlph start [issue...]` | 启动 Issue(s) |
| `crawlph start --all` | 启动所有 draft Issues |
| `crawlph resume [issue]` | 恢复暂停的 Issue |

#### 状态命令

| 命令 | 描述 |
|------|------|
| `crawlph status` | 查看全局状态 |
| `crawlph status --json` | JSON 输出 |
| `crawlph show <issue>` | 查看 Issue 详情 |

#### 控制命令

| 命令 | 描述 |
|------|------|
| `crawlph pause <issue>` | 暂停 Issue |
| `crawlph cancel <issue>` | 取消 Issue |
| `crawlph priority <issue> <level>` | 设置优先级 |

#### 审查命令

| 命令 | 描述 |
|------|------|
| `crawlph review <pr>` | 打开 PR 审查页面 |
| `crawlph approve <pr>` | 批准 PR |
| `crawlph request-changes <pr> <msg>` | 请求修改 |

#### 决策命令

| 命令 | 描述 |
|------|------|
| `crawlph resolve` | 解决冲突/阻塞 |
| `crawlph resolve --choice <A/B>` | 选择解决策略 |

#### 配置命令

| 命令 | 描述 |
|------|------|
| `crawlph config <key> <value>` | 设置配置 |
| `crawlph config --list` | 查看配置 |

#### 全局选项

| 选项 | 描述 |
|------|------|
| `--yes, -y` | 跳过确认提示 |
| `--json` | JSON 格式输出 |
| `--watch` | 持续监控模式 |
| `--verbose, -v` | 详细输出 |

### 4.2 并行执行

- 支持同时处理多个 Issues
- 默认并发上限：8 个
- Issue 间相互独立执行
- 遇到检查点时暂停，等待用户

### 4.3 冲突检测

- 检测粒度：文件级
- 检测时机：设计阶段、实现开始时、实际修改时
- 检测到冲突后暂停相关 Issues
- 等待用户选择解决策略

### 4.4 依赖管理

- 依赖声明：Issue Body 中 `Depends on: #xxx`
- 自动构建依赖图
- 拓扑排序生成执行计划
- 依赖完成时自动触发等待的 Issue

---

## 5. MVP 范围

### 5.1 MVP 目标

验证核心价值：
- 并行处理 2+ 个 Issues
- 自动化设计到实现流程
- 用户在关键点介入

### 5.2 MVP 功能

#### 必须有

- `crawlph init` - 初始化项目
- `crawlph start <issue>` - 启动单个 Issue
- `crawlph status` - 查看状态
- `crawlph review <pr>` - 打开 PR 审查
- `crawlph approve <pr>` - 批准 PR
- 基本工作流执行
- 本地状态存储
- 单 Issue 的 Ralph Loop（无限重试）

#### 应该有

- `crawlph start <issue1> <issue2>` - 并行启动多个
- 基本冲突检测（文件级）
- `crawlph resolve` - 解决冲突
- 依赖声明和等待

#### MVP 不需要

- `--watch` 模式
- 复杂的冲突检测（函数级）
- 自动依赖分析
- 通知推送
- 多项目管理
- 配置系统（使用默认值）

### 5.3 MVP 验收场景

#### 场景 A：单 Issue 完整流程

```
$ crawlph start #101
$ crawlph status              # 看到 waiting-design-review
$ crawlph review #201         # 打开 Design PR
$ crawlph approve #201        # 批准设计
$ crawlph status              # 看到 implementing
# 等待实现完成
$ crawlph status              # 看到 waiting-review
$ crawlph review #201         # 打开 Impl PR
$ crawlph approve #201        # 批准实现
$ crawlph status              # 看到 done
```

#### 场景 B：并行 Issues（有冲突）

```
$ crawlph start #101 #102
$ crawlph status              # 看到两个 Issue 都在运行
# 检测到冲突
$ crawlph resolve             # 选择策略 A
$ crawlph status              # 看到 #102 暂停，#101 继续
# 等待 #101 完成
$ crawlph status              # 看到 #102 恢复执行
```

---

## 6. 非功能需求

### 6.1 性能

- CLI 启动时间 < 1s
- 状态查询响应 < 500ms
- 支持同时管理 50+ Issues

### 6.2 可靠性

- 状态持久化，支持中断恢复
- 网络错误自动重试
- 清晰的错误提示

### 6.3 可用性

- 命令简洁，符合直觉
- 状态输出清晰
- 支持 `--help` 和 `--verbose`

---

## 7. 未来规划

### 7.1 Phase 2: CLI + Server

- 后台持续运行（watch mode）
- 通知推送（Telegram、Email）
- 多终端访问
- Web Dashboard

### 7.2 Phase 3: 协作

- 多人协作支持
- 权限管理
- 团队工作区

### 7.3 Phase 4: 智能

- 自动依赖分析
- 智能任务分解
- 代码质量预测
- 自动测试生成

---

## 8. 附录

### 8.1 术语表

| 术语 | 定义 |
|------|------|
| Issue | GitHub Issue，代表一个需求或任务 |
| Stage | Issue 处理的阶段 |
| PR | Pull Request |
| Checkpoint | 需要用户介入的关键时刻 |
| Conflict | 多个 Issue 修改同一文件 |
| Dependency | Issue 间的依赖关系 |
| Ralph Loop | 无限重试机制，直到成功或达到上限 |

### 8.2 参考

- [prd.md](../prd.md) - 原始产品需求（7 阶段模型）
- [design/workflow.md](./workflow.md) - 工作流设计
- [design/issueflow.md](./issueflow.md) - Issue 流程设计

---

**更新历史**:
- 2026-03-24: v0.1 初始版本
