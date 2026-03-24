# crawlph CLI 技术规格

**状态**: 进行中  
**创建时间**: 2026-03-24  
**版本**: 0.1

---

## 1. 技术栈

| 组件 | 选择 | 理由 |
|------|------|------|
| 语言 | TypeScript | 类型安全、生态丰富、AI SDK 完善 |
| 运行时 | Node.js 18+ | LTS 版本，支持现代特性 |
| CLI 框架 | Commander.js | 成熟、文档完善、TypeScript 支持好 |
| GitHub API | @octokit/rest | 官方 SDK、类型完整 |
| 配置管理 | cosmiconfig | 支持多种配置文件格式 |
| 输出美化 | chalk + ora | 行业标准 |
| 测试 | Vitest | 快速、现代、TS 原生支持 |
| 构建 | tsc | 简单直接，不引入 bundler 复杂性 |

---

## 2. 架构决策

### 2.1 AI Agent 调用方式

**当前选择**: Spawn opencode agents (Phase 1)

```
┌─────────────────────────────────────────────────────────┐
│              AI Agent 调用架构                          │
├─────────────────────────────────────────────────────────┤
│                                                         │
│   crawlph CLI                                           │
│        │                                                │
│        │ child_process.spawn()                          │
│        ▼                                                │
│   ┌─────────────────┐                                   │
│   │ opencode agent  │                                   │
│   │ (sub-process)   │                                   │
│   │                 │                                   │
│   │ • Designer      │                                   │
│   │ • Implementer   │                                   │
│   │ • Reviewer      │                                   │
│   └─────────────────┘                                   │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

**优点**:
- 保持与现有 opencode 生态兼容
- 无需学习新 SDK
- 快速启动

**后续调研** (Phase 2):
- 评估 @opencode/sdk 的可行性
- 调研直接调用 OpenAI/Claude API 的方案

**调研任务**:
- [ ] **SDK 可行性调研** (优先级: 高)
  - URL: https://opencode.ai/docs/zh-cn/sdk/
  - 问题:
    1. SDK 是否支持完整的 agent 能力？
    2. 能否替代 spawn 方式？
    3. 迁移成本有多大？
    4. SDK 与 spawn 的性能差异？
  - 决策点: 当需要脱离 opencode 生态独立运行时
  - 目标: 评估是否在 Phase 2 迁移到 SDK

### 2.2 状态存储

**选择**: 纯 GitHub Labels (MVP)

```
┌─────────────────────────────────────────────────────────┐
│                  状态存储架构                            │
├─────────────────────────────────────────────────────────┤
│                                                         │
│   Issue 状态 = GitHub Labels                            │
│   ────────────────────────────                          │
│                                                         │
│   crawlph:stage/draft                                   │
│   crawlph:stage/refining                                │
│   crawlph:stage/designing                               │
│   crawlph:stage/waiting-design-review                   │
│   crawlph:stage/implementing                            │
│   crawlph:stage/waiting-review                          │
│   crawlph:stage/merging                                 │
│   crawlph:stage/done                                    │
│                                                         │
│   crawlph:status/paused                                 │
│   crawlph:status/blocked                                │
│   crawlph:status/conflict                               │
│   crawlph:status/waiting-dep                            │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

**优点**:
- 零配置，无需本地数据库
- 状态在 GitHub 上可视化
- 跨设备同步
- 简化部署

**缺点**:
- 标签数量有限制
- 无法存储复杂关系（依赖图需要从 Issue body 解析）
- 无历史记录

**Phase 2 扩展路径** (如果需要):
- 本地 SQLite 存储历史、关系图、缓存
- 决策点: 当纯 Labels 不够用时再引入

### 2.3 并发模型

**选择**: 单进程 + Promise.all (初期)

```typescript
// 概念示意，非实现
processIssues(issues: Issue[]): void {
  const batches = chunk(issues, maxConcurrency);
  for (const batch of batches) {
    await Promise.all(batch.map(processIssue));
  }
}
```

**扩展路径**:
- Phase 2: Worker threads (CPU 密集型任务)
- Phase 3: 进程池 (更好的隔离性)

---

## 3. 项目结构

```
crawlph-cli/
├── src/
│   ├── commands/           # CLI 命令实现
│   │   ├── init.ts
│   │   ├── start.ts
│   │   ├── status.ts
│   │   ├── review.ts
│   │   ├── approve.ts
│   │   ├── pause.ts
│   │   └── resolve.ts
│   ├── core/               # 核心业务逻辑
│   │   ├── issue-manager.ts
│   │   ├── github-client.ts
│   │   ├── agent-spawner.ts
│   │   ├── state-machine.ts
│   │   └── config-manager.ts
│   ├── types/              # TypeScript 类型定义
│   │   ├── issue.ts
│   │   ├── stage.ts
│   │   ├── config.ts
│   │   └── index.ts
│   ├── utils/              # 工具函数
│   │   ├── logger.ts
│   │   ├── spinner.ts
│   │   └── errors.ts
│   └── index.ts            # CLI 入口
├── bin/
│   └── crawlph             # 可执行脚本
├── test/                   # 测试文件
├── package.json
├── tsconfig.json
└── README.md
```

---

## 4. 数据模型

### 4.1 Issue

```typescript
interface Issue {
  number: number;
  title: string;
  body: string;
  stage: Stage;
  status: Status | null;
  labels: string[];
  assignee: string | null;
  createdAt: Date;
  updatedAt: Date;
  
  // 解析后的元数据 (从 body)
  dependencies: number[];
  
  // 关联的 PR
  designPR: number | null;
  implPR: number | null;
}
```

### 4.2 Stage (状态机)

```
┌─────────────────────────────────────────────────────────┐
│                    Issue 生命周期                        │
├─────────────────────────────────────────────────────────┤
│                                                         │
│   draft ──▶ refining ──▶ designing                      │
│                                │                        │
│                                ▼                        │
│                     waiting-design-review               │
│                                │                        │
│                     ┌──────────┴──────────┐            │
│                     │  用户审查设计        │            │
│                     └──────────┬──────────┘            │
│                                │                        │
│                                ▼                        │
│                        implementing                     │
│                                │                        │
│                                ▼                        │
│                        waiting-review                   │
│                                │                        │
│                     ┌──────────┴──────────┐            │
│                     │  用户审查实现        │            │
│                     └──────────┬──────────┘            │
│                                │                        │
│                                ▼                        │
│                           merging                       │
│                                │                        │
│                                ▼                        │
│                            done                         │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

```typescript
type Stage = 
  | 'draft'
  | 'refining' 
  | 'designing'
  | 'waiting-design-review'
  | 'implementing'
  | 'waiting-review'
  | 'merging'
  | 'done';

type Status =
  | 'paused'       // 用户手动暂停
  | 'blocked'      // 技术阻塞
  | 'conflict'     // 与其他 Issue 冲突
  | 'waiting-dep'; // 等待依赖完成
```

### 4.3 Config

```typescript
interface Config {
  // GitHub
  githubToken: string;         // 从 GH_TOKEN 或交互式输入
  
  // AI Agent
  opencodePath: string;        // opencode 可执行路径
  agentTimeout: number;        // 默认 30 分钟
  maxConcurrency: number;      // 默认 8
  
  // 存储 (Phase 2)
  // dataDir?: string;
  
  // 通知 (Phase 2)
  // notifyChannel?: string;
}
```

---

## 5. CLI 命令

### 5.1 命令概览

| 命令 | 描述 | MVP |
|------|------|-----|
| `crawlph init` | 初始化项目 | ✅ |
| `crawlph status` | 查看所有 Issue 状态 | ✅ |
| `crawlph start <issue>` | 启动单个 Issue | ✅ |
| `crawlph review <pr>` | 打开 PR 审查 | ✅ |
| `crawlph approve <pr>` | 批准 PR | ✅ |
| `crawlph start <i1> <i2>` | 并行启动多个 | Phase 2 |
| `crawlph resolve` | 解决冲突 | Phase 2 |
| `crawlph pause <issue>` | 暂停 Issue | Phase 2 |

### 5.2 命令详解

#### `crawlph init`

```
初始化 crawlph 项目

Usage:
  crawlph init [options]

Options:
  --github-token <token>   GitHub token (或使用 GH_TOKEN)
  --opencode-path <path>   opencode 可执行路径 (默认: opencode)

流程:
  1. 检测是否在 git 仓库中
  2. 检测 GitHub remote
  3. 创建 .crawlphrc 配置文件
  4. 创建必要的 GitHub Labels
  5. 验证 opencode 是否可用
```

#### `crawlph status`

```
查看所有 Issue 状态

Usage:
  crawlph status [options]

Options:
  --json          JSON 格式输出
  --stage <name>  过滤特定阶段

输出示例:
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

#### `crawlph start <issue>`

```
启动 Issue 处理流程

Usage:
  crawlph start <issue-number> [options]

Options:
  --yes, -y       跳过确认提示
  --timeout <min> 子 agent 超时时间 (默认: 30)

流程:
  1. 获取 Issue 信息
  2. 检查依赖是否满足
  3. 检查冲突
  4. 启动 Ralph Loop
     - spawn designer agent
     - 创建 Design PR
     - 等待用户审查
     - spawn implementer agent
     - 等待用户审查
     - 合并 PR
```

#### `crawlph review <pr>`

```
打开 PR 审查页面

Usage:
  crawlph review <pr-number>

行为:
  使用 gh pr view <pr> --web 打开浏览器
```

#### `crawlph approve <pr>`

```
批准 PR

Usage:
  crawlph approve <pr-number> [options]

Options:
  --message, -m <msg>  审查评论

行为:
  1. gh pr review <pr> --approve
  2. 更新 Issue 状态 (label)
  3. 如果是 Design PR，触发 implementing 阶段
  4. 如果是 Impl PR，触发 merging 阶段
```

---

## 6. 关键设计决策

### 6.1 单 PR 模式

**决策**: 一个 Issue 对应一个 PR

```
Issue #123
    │
    └──▶ PR #201
         │
         ├── commit 1: design.md (创建)
         ├── commit 2: design.md (修改，如果用户要求)
         ├── commit 3: src/auth.ts (实现)
         └── commit 4: test/auth.test.ts (测试)
```

**优点**:
- 减少 PR 数量
- 设计到实现的连续性
- 审查历史完整

### 6.2 Ralph Loop

**概念**: 无限重试，直到成功或用户干预

```
┌─────────────────────────────────────────────────────────┐
│                    Ralph Loop                           │
├─────────────────────────────────────────────────────────┤
│                                                         │
│   ┌─────────┐                                           │
│   │  开始   │                                           │
│   └────┬────┘                                           │
│        │                                                │
│        ▼                                                │
│   ┌─────────┐     成功      ┌─────────┐                │
│   │ 执行    │─────────────▶│  结束   │                │
│   │ 阶段    │              └─────────┘                │
│   └────┬────┘                                           │
│        │ 失败                                           │
│        ▼                                                │
│   ┌─────────┐     可恢复    ┌─────────┐                │
│   │ 错误    │─────────────▶│  重试   │──┐             │
│   │ 分类    │              │ (退避)  │  │             │
│   └────┬────┘              └─────────┘  │             │
│        │ 不可恢复           │            │             │
│        ▼                    └────────────┘             │
│   ┌─────────┐                                           │
│   │ blocked │                                           │
│   │ 等待    │                                           │
│   │ 用户    │                                           │
│   └─────────┘                                           │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

**错误分类**:
| 类型 | 示例 | 处理 |
|------|------|------|
| 可恢复 | 网络错误、API 限流 | 重试 (指数退避) |
| 可恢复 | Agent 超时 | 重试 (新 context) |
| 不可恢复 | 编译错误、测试失败 | blocked，等待用户 |

### 6.3 冲突检测 (Phase 2)

**粒度**: 文件级

```
┌─────────────────────────────────────────────────────────┐
│                    冲突检测                              │
├─────────────────────────────────────────────────────────┤
│                                                         │
│   Issue #101         Issue #102                         │
│       │                  │                              │
│       │ 修改             │ 计划修改                      │
│       ▼                  ▼                              │
│   src/auth.js ◀──────▶ 冲突!                            │
│                                                         │
│   检测时机:                                             │
│   1. 设计阶段 (分析修改范围)                            │
│   2. 实现开始 (再次确认)                                │
│   3. 实际修改时 (git diff)                              │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

### 6.4 依赖管理 (Phase 2)

**声明方式**: Issue Body 中

```markdown
## Dependencies
Depends on: #101, #102
```

**处理流程**:
1. 解析依赖声明
2. 构建依赖图
3. 检测循环依赖
4. 拓扑排序
5. 生成执行计划

---

## 7. 实现路线

### Phase 1: 脚手架 (本周)
- [ ] 初始化项目 (package.json, tsconfig.json)
- [ ] 创建目录结构
- [ ] 实现 CLI 入口和命令路由
- [ ] 实现配置管理
- [ ] 实现 GitHub 客户端基础

### Phase 2: MVP (本月)
- [ ] 实现 `crawlph init` 命令
- [ ] 实现 `crawlph status` 命令
- [ ] 实现 `crawlph start` 命令
- [ ] 实现 Agent Spawner
- [ ] 实现 Ralph Loop
- [ ] 完整单 Issue 流程测试

### Phase 3: 进阶 (下月)
- [ ] 并行 Issues 支持
- [ ] 冲突检测
- [ ] `crawlph resolve` 命令
- [ ] 依赖管理

### Phase 4: SDK 迁移 (待定)
- [ ] SDK 可行性调研
- [ ] 迁移方案设计
- [ ] 实施迁移 (如果决定采用)

---

## 8. 待调研事项

### 8.1 SDK 可行性 (优先级: 高)
- URL: https://opencode.ai/docs/zh-cn/sdk/
- 问题:
  1. SDK 是否支持完整的 agent 能力？
  2. 能否替代 spawn 方式？
  3. 迁移成本有多大？
  4. SDK 与 spawn 的性能差异？
- 决策点: 当需要脱离 opencode 生态独立运行时

### 8.2 冲突检测算法
- 文件级检测策略
- 与 git diff 集成
- 性能优化

### 8.3 依赖图算法
- 拓扑排序实现
- 循环依赖检测
- 大规模图性能

---

## 9. 附录

### 9.1 参考资料

- [prd.md](./prd.md) - 产品文档
- [SKILL.md](../skills/crawlph/SKILL.md) - 现有 skill 实现
- opencode SDK: https://opencode.ai/docs/zh-cn/sdk/

### 9.2 命名约定

- 命令: kebab-case (`crawlph start-issue`)
- 文件: kebab-case (`issue-manager.ts`)
- 类: PascalCase (`IssueManager`)
- 函数/变量: camelCase (`processIssue`)
- 常量: UPPER_SNAKE_CASE (`MAX_CONCURRENCY`)

---

**更新历史**:
- 2026-03-24: v0.1 初始版本
