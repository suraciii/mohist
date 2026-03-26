# Plan 阶段

## 概述

制定技术方案，分解任务，创建 OpenSpec change。

## 触发

Explore 阶段完成 + 用户确认 "可以规划了"

## 执行引擎

opencode 会话

```
opencode --message "/opsx:propose 搜索功能"
```

## Agent 行为

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│   1. 调用 OpenSpec 创建 change                                   │
│      openspec new change add-search --schema ralph-driven       │
│                                                                 │
│   2. 读取 Issue Body（已梳理好的需求）                            │
│                                                                 │
│   3. 分析代码库，制定技术方案                                     │
│                                                                 │
│   4. 生成 artifacts:                                             │
│      - proposal.md (为什么做，做什么)                            │
│      - specs/ (详细规格)                                         │
│      - design.md (技术设计)                                       │
│      - prd.json (Ralph 任务列表)                                 │
│                                                                 │
│   5. 更新 Issue Body（添加方案摘要 + 任务清单 + change 链接）     │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

## 与 OpenSpec 的关系

Plan 阶段相当于 OpenSpec 的 `/opsx:propose`：

```
crawlph Plan          OpenSpec
─────────────────────────────────────────
技术方案           →   proposal.md
详细规格           →   specs/*/spec.md
技术设计           →   design.md
任务清单           →   prd.json
```

## 输出

### Issue Body 更新

```markdown
# 加一个搜索功能

## 状态
planning → developing (用户确认后)

## 描述
(Explore 阶段已产出)

## 验收标准
(Explore 阶段已产出)

## 技术方案
使用 PostgreSQL 全文搜索 (FTS)：
- 添加 gin 索引到 articles 表
- 实现 SearchService
- 前端使用 React 组件

关键决策：
- 不引入 Elasticsearch（成本考虑）
- 使用 PostgreSQL 内置 FTS（足够满足需求）

## 任务
- [ ] 添加搜索索引 (articles 表)
- [ ] 实现 SearchService
- [ ] 实现搜索 API
- [ ] 添加搜索 UI 组件
- [ ] 添加关键词高亮
- [ ] 测试

## OpenSpec
openspec/changes/add-search/
```

### OpenSpec change 目录

```
openspec/changes/add-search/
├── .openspec.yaml
├── proposal.md
├── design.md
├── specs/
│   └── search/
│       └── spec.md
└── prd.json
```

## prd.json 结构

```json
{
  "project": "crawlph",
  "description": "Add full-text search for articles",
  "tasks": [
    {
      "id": "T-001",
      "title": "Add search index to articles table",
      "description": "Add GIN index for full-text search",
      "acceptanceCriteria": [
        "Migration runs successfully",
        "Index is created",
        "Typecheck passes"
      ],
      "priority": 1,
      "passes": false
    },
    {
      "id": "T-002",
      "title": "Implement SearchService",
      "description": "Create service class for search operations",
      "acceptanceCriteria": [
        "Can search by title and content",
        "Returns ranked results",
        "Tests pass"
      ],
      "priority": 2,
      "passes": false
    }
  ]
}
```

## 转换条件

用户确认 "可以开发了" → 触发 Dev 阶段

## 用户参与

- 中度参与
- 审查技术方案
- 确认任务分解合理
- 最终确认 "可以开发了"
