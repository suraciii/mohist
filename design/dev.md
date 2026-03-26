# Dev 阶段

## 概述

代码实现，执行任务，创建 PR。

## 触发

Plan 阶段完成 + 用户确认 "可以开发了"

## 执行引擎

opencode 会话（Ralph Loop）

```
opencode --message "/opsx:ralph --change add-search"
```

## Agent 行为

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│   Ralph Loop 迭代执行:                                           │
│                                                                 │
│   while (有未完成任务):                                          │
│       1. 读取 prd.json，找到下一个未完成任务                      │
│       2. 读取 context (proposal, specs, design)                  │
│       3. 执行任务                                                │
│       4. 运行质量检查 (types, lint, tests)                       │
│       5. 通过 → 更新 prd.json (passes: true)                     │
│       6. 更新 Issue Body (任务进度)                              │
│       7. 检查用户是否有新指令                                    │
│                                                                 │
│   所有任务完成 → 创建 PR                                         │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## 用户干预

用户可以随时向 opencode 会话追加指令：

```
方式 1: CLI
$ crawlph attach 42
> 搜索结果需要按相关度排序
> exit

方式 2: 直接评论
$ crawlph comment 42 "搜索结果需要按相关度排序"

方式 3: Issue Comment (将来)
用户在 GitHub Issue 评论 → 自动同步到会话
```

## 进度查看

```
$ crawlph status 42

Issue #42: 加一个搜索功能
Stage: Dev (3/6 tasks)
Session: opencode-abc123

Progress:
✓ T-001 添加搜索索引
✓ T-002 实现 SearchService
✓ T-003 实现搜索 API
● T-004 添加搜索 UI (进行中)
○ T-005 添加关键词高亮
○ T-006 测试

Attach: crawlph attach 42
```

## 输出

### 代码提交

```
PR #6: Add full-text search for articles

Commits:
- T-001: Add search index to articles table
- T-002: Implement SearchService
- T-003: Implement search API
- T-004: Add search UI component
- T-005: Add keyword highlighting
- T-006: Add tests
```

### prd.json 更新

```json
{
  "tasks": [
    { "id": "T-001", "passes": true },
    { "id": "T-002", "passes": true },
    { "id": "T-003", "passes": true },
    { "id": "T-004", "passes": true },
    { "id": "T-005", "passes": true },
    { "id": "T-006", "passes": true }
  ]
}
```

### Issue Body 更新

```markdown
## 任务
- [x] 添加搜索索引 (articles 表)
- [x] 实现 SearchService
- [x] 实现搜索 API
- [x] 添加搜索 UI 组件
- [x] 添加关键词高亮
- [x] 测试

## PR
#6 - Add full-text search for articles
```

## 转换条件

所有任务完成 + PR 创建 → 自动触发 Verify 阶段

## 用户参与

- 低度参与
- 可随时查看进度
- 可随时追加指令
- 仅在 Agent 卡住时干预
