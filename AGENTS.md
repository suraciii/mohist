# Agent Instructions

## Project Overview

crawlph 是一个 AI 驱动的开发工作流自动化工具，使用本地 SQLite 存储，通过 opencode agents 自动完成 Issue 的设计、实现和审查。

## 目录职责

| 目录 | 职责 | 内容 |
|------|------|------|
| `crawlph-cli/` | 核心实现 | CLI + Server + Agent Runner |
| `prd/` | 产品文档 | 产品定位、功能规划、用户故事 |
| `design/` | 技术设计 | 架构设计、技术规格、流程设计 |
| `docs/` | 用户文档 | README、CONTRIBUTING、使用指南 |
| `openspec/` | 变更管理 | OpenSpec 变更提案、任务追踪 |

## 核心实现结构

```
crawlph-cli/
├── src/
│   ├── cli/           # CLI 命令入口
│   ├── server/        # HTTP Server + 状态管理
│   ├── agent/         # Agent Runner (spawn opencode)
│   ├── services/      # 业务逻辑层
│   ├── db/            # SQLite 数据层
│   ├── api/           # REST API 路由
│   ├── workflow/      # 工作流状态机
│   └── providers/     # Issue 来源接口 (local/github)
├── tests/             # 测试文件
└── dist/              # 编译输出
```

## 工作流阶段

```
draft → designing → waiting-design-review → implementing → waiting-review → done
```

用户审批点：
- `waiting-design-review`: 设计完成后等待用户审批
- `waiting-review`: 实现完成后等待用户审批

## 常用命令

```bash
# 开发
cd crawlph-cli && npm run build
cd crawlph-cli && npm test

# 运行 Server
cd crawlph-cli && npm run server

# CLI 使用
node dist/cli/index.js server start
node dist/cli/index.js issue list
node dist/cli/index.js issue start 1
```

## 数据存储

```
~/.crawlph/
├── crawlph.db    # SQLite 数据库
└── logs/         # 日志文件
```

## 非显而易见的发现

### Agent Runner
- 使用 `opencode agent --local --message "..."` spawn 子进程
- 超时默认 30 分钟
- Prompt 在 `src/agent/prompts.ts` 中定义

### 工作流状态机
- 只能顺序推进，不能跳过阶段
- `WorkflowService` 控制状态转换
- `StateManager` 管理运行时状态

### Provider 接口
- 当前只实现了 `local` provider
- `github` provider 接口已定义但未实现
- Provider 切换通过配置控制
