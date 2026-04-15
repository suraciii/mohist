# 用户交互

## 你的角色

你是**产品负责人**，不是程序员。

- 你决定做什么
- 你确认方案
- 你审查结果
- AI 负责实现

## 你需要投入的时间

```
假设一个需求从想法到合并需要 2 小时：

你的时间：
├── Explore 面试: 30 分钟   (AI 问你问题，你理清需求)
├── Plan gate 确认: 10 分钟 (审查技术方案和任务拆分)
├── Check gate 审查: 15 分钟(审查最终代码)
└── 总计: ~55 分钟

AI 自动执行的时间：
└── ~65 分钟（你可以去做别的事）
```

## 你在哪个阶段参与最多

```
Explore Mode  ████████ 高   AI 面试你，你理清需求
Plan gate     ████ 中       审查方案+任务拆分
Build         █ 低          AI 自己写，你忙别的
Check gate    ████ 中       审查结果
```

## 你怎么和 mohist 交互

### 查看进度

```
$ mo status 42

Issue #42: 添加搜索功能
Stage: BUILD
Change: openspec/changes/42-add-search/

EXPLORE:
  ✓ proposal.md
PLAN:
  ✓ specs/search.md (approved)
  ✓ design.md
  ✓ tasks.json (8 tasks)
BUILD:
  ✓ T-001 添加搜索索引 (AFK)
  ✓ T-002 实现 SearchService (AFK)
  ● T-003 实现搜索 API (AFK, 进行中)
  ○ T-004 添加搜索 UI (AFK)
  ○ T-005 数据迁移 (HITL ← 需要你确认)
CHECK:
  (pending)
```

### 回答面试问题 (Explore)

```
$ mo attach 42

AI: 你的搜索功能面向谁？所有用户还是仅管理员？
你: 所有用户，但管理员能看到额外的内部结果

AI: 搜索失败时（比如索引不可用），用户看到什么？
你: 显示空结果+提示"搜索暂时不可用"，不要报错页面
```

### 确认方案 (Plan gate)

```
$ mo approve 42    # Plan gate：确认方案和任务拆分
$ mo approve 42    # Check gate：确认最终结果
```

## 你可以随时说

- **"可以规划了"** — 需求聊清楚了，进入 Plan
- **"可以开发了"** — 方案没问题，进入 Build
- **"暂停"** — 先停一下
- **"继续"** — 恢复执行
- **"完成"** — 审查通过，Issue 完成
- **"取消"** — 不做了，关闭 Issue
