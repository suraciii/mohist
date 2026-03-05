# IssueFlow 设计

**状态**: 演进中  
**创建时间**: 2026-03-06  
**关联文档**: [workflow.md](../workflow.md)

---

## 1. 核心思想

### 1.1 标签驱动的 Issue 生命周期

```
Issue 的生命周期由标签控制：

- 标签表示"阻碍继续的条件"
- 有标签 → 不能进入下一步
- 无标签 → 自动继续
- 流程 = 逐步移除标签

核心标签:
[draft]         需求阶段
[need-design]   设计阶段
[need-review]   审查阶段

就这三个！
```

### 1.2 用户对流程的控制权

```
用户决定审查级别：

路径 A: 用户要求设计
  → 添加 [need-design]
  → 必须用户审查设计
  → 复杂任务，有人工审查点

路径 B: 用户不要求设计
  → Agent 自主判断是否需要设计
  → Agent 自动审查设计
  → 简单任务，可全自动

自然的复杂度分级:
- 简单 Bug:    [draft] → (不需要设计) → implement → [need-review] → done
- 中等功能:    [draft] → [need-design] → (Agent 审查) → implement → [need-review] → done
- 复杂特性:    [draft] → [need-design] → (用户审查) → implement → [need-review] → done
```

---

## 2. 完整流程

### 2.1 Issue 生命周期

```
1. [draft] - 需求阶段
   ├─ 用户创建 Issue
   ├─ 用户主动挑选 draft issue
   ├─ Agent 与用户对话 (Refinement)
   ├─ 完善 Issue Body，添加任务清单
   ├─ 用户审查需求
   │   ├─ 通过 → 移除 [draft]
   │   └─ 不通过 → 继续对话 (仍在 [draft])
   │
   └─ 需求通过后，用户决策:
       ├─ 要求设计 → 添加 [need-design]
       └─ 不要求 → Agent 自主判断

 2. 创建 PR
   ├─ 条件: 无 [draft]
   ├─ 动作: 创建 PR #1 (初始为空或最小文件)
   ├─ 时机: 移除 [draft] 后立即创建（无论是否需要设计）
   └─ PR 关联 Issue #N

3. [need-design] - 设计阶段 (可选)
   ├─ Agent 生成 design.md, tasks.md, specs/
   ├─ 提交到 PR #1
   ├─ 审查设计:
   │   ├─ 用户要求设计 → 用户审查
   │   └─ 用户未要求 → Agent 自动审查
   ├─ 通过 → 移除 [need-design]
   └─ 不通过 → 重新设计 (仍在 [need-design])

4. Implement - 实现阶段
   ├─ 条件: 无 [need-design] (或设计完成)
   ├─ Agent 执行 tasks (Ralph Loop)
   ├─ 逐步提交实现代码到 PR #1
   ├─ 技术失败 → 自动重试 (指数退避)
   └─ 完成 → 添加 [need-review]

5. [need-review] - 审查阶段
   ├─ Agent Review (自动)
   ├─ User Review (手动)
   ├─ 通过 → 移除 [need-review]
   └─ 不通过 → 修复问题 (仍在 [need-review])

6. 合并
   ├─ 条件: 无任何标签
   ├─ 动作: 合并 PR #1
   └─ 关闭 Issue
```

### 2.2 状态转换图

```
┌─────────────────────────────────────────────────────────────┐
│                    Issue 状态转换                            │
└─────────────────────────────────────────────────────────────┘

用户创建 Issue
      ↓
  [draft]
      ↓ 用户挑选
  Refinement (对话)
      ↓ 用户审查通过
  移除 [draft]
      ↓
  ┌─────────────┐
  │ 用户要求设计? │
  └─────────────┘
      ├─ Yes → [need-design]
      │           ↓
      │      创建 PR #1
      │           ↓
      │      生成设计
      │           ↓
      │      审查设计 (用户)
      │           ↓
      │      移除 [need-design]
      │
       └─ No → 创建 PR #1
                 ↓
            Agent 判断是否需要设计
                 ├─ 需要 → [need-design]
                 │           ↓
                 │      生成设计
                 │           ↓
                 │      审查设计 (Agent)
                 │           ↓
                 │      移除 [need-design]
                 │
                 └─ 不需要 → Implement
                              ↓
                         [need-review]
                              ↓
                         Agent + User Review
                              ↓
                         移除 [need-review]
                              ↓
                           合并 PR
                              ↓
                            Done
```

---

## 3. Review 被拒绝的处理

### 3.1 三种 Review 场景

```
┌─────────────────────────────────────────────────────────────┐
│ 1. 需求 Review                                               │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  状态: [draft]                                              │
│  用户: "需求不完整/不准确"                                   │
│  Agent: 继续对话，完善需求                                   │
│  结果: 仍然在 [draft] 中                                    │
│                                                             │
│  不需要额外状态，因为还在需求阶段                             │
│                                                             │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ 2. 设计 Review                                               │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  状态: [need-design]                                        │
│  用户: "设计方向不对"                                        │
│  Agent: 重新设计                                            │
│  结果: 仍然在 [need-design] 中                              │
│                                                             │
│  不需要额外状态，因为还在设计阶段                             │
│                                                             │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ 3. 实现 Review                                               │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  状态: [need-review]                                        │
│  用户: "实现有问题"                                          │
│  Agent: 修复问题                                            │
│  结果: 仍然在 [need-review] 中                              │
│                                                             │
│  不需要额外状态，因为还在审查阶段                             │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### 3.2 核心洞察

```
Review 被拒绝 = 继续停留在当前标签中

- 需求被拒绝 → 继续在 [draft] 中对话
- 设计被拒绝 → 继续在 [need-design] 中重新设计
- 实现被拒绝 → 继续在 [need-review] 中修复问题

每个标签都包含"正在进行"和"返工"两种情况
不需要额外的 [need-rework] 标签
```

---

## 4. 与原始设计的对比

### 4.1 原始 prd.md 设计

```
7 个阶段: Exploration → Refinement → Design → Implementation → Review → Re-evaluation → Done

特点:
- 双向通信: Issue Comments 作为命令总线
- 三层 Review: User Review / Agent Review / Auto Verification
- Re-evaluation: 独立阶段
- PR 策略: 未明确
```

### 4.2 IssueFlow 设计

```
3 个标签: [draft] → [need-design] → [need-review]

特点:
- 标签驱动: 每个标签表示一个阶段
- 用户控制: 用户决定审查级别
- Review 被拒绝: 继续停留在当前标签
- PR 策略: 单 PR 跟踪设计和实现
- Agent 自主: Agent 可判断是否需要设计

核心差异:
┌────────────────┬─────────────────┬──────────────────┐
│ 特性           │ prd.md          │ IssueFlow        │
├────────────────┼─────────────────┼──────────────────┤
│ 状态表示       │ 阶段            │ 标签             │
│ 标签数量       │ 7+              │ 3                │
│ Re-evaluation  │ 独立阶段        │ 隐式 (返工)      │
│ 用户控制       │ 固定流程        │ 可选审查点       │
│ Agent 自主性   │ 遵循流程        │ 判断是否需要设计 │
│ PR 策略        │ 未明确          │ 单 PR            │
└────────────────┴─────────────────┴──────────────────┘
```

---

## 5. 设计决策

### 5.1 为什么只需要 3 个标签？

```
原因:

1. Refinement 和 Design 不存在"失败"
   - Refinement = 对话过程，持续到通过
   - Design = 生成文档，持续到通过
   - 只有 Review 可能被拒绝

2. Review 被拒绝不需要额外状态
   - 被拒绝 = 继续在当前标签中
   - 通过 = 移除当前标签

3. 状态的语义清晰
   - [draft] = 需求未完成
   - [need-design] = 设计未完成
   - [need-review] = 审查未完成
```

### 5.2 为什么用户控制审查级别？

```
原因:

1. 复杂度分级
   - 简单任务: 用户不要求设计 → 全自动
   - 复杂任务: 用户要求设计 → 人工审查

2. 信任机制
   - 用户信任 Agent → Agent 自主
   - 用户不信任 → 人工审查点

3. 灵活性
   - 不同团队有不同标准
   - 不同 Issue 有不同复杂度
```

### 5.3 为什么单 PR 设计？

```
原因:

1. 历史连续性
   - 设计和实现在同一个 PR
   - 用户一站式审查

2. 简化流程
   - 一个 Issue 对应一个 PR
   - 不需要关联多个 PR

3. Review 被拒绝时
   - 在同一个 PR 上修改
   - 历史可见，可追溯
```

---

## 6. 待解决问题

### 6.1 技术失败的处理

```
方案: 记录在 PR Comment 中，继续重试

实现:
1. Implement 阶段遇到技术失败 (429 限流、网络错误等)
2. 指数退避重试 (1s → 2s → 4s → 8s → 16s → 32s)
3. 重试 N 次 (默认 5 次) 失败后:
   - 在 PR 中添加 Comment，记录失败原因和重试次数
   - 继续重试，但降低频率 (每 60s 重试一次)
4. 用户可以看到进度，决定是否干预

优点:
- 用户可见性: 失败信息记录在 PR 中
- 不阻塞: 继续尝试，等待问题自动恢复
- 灵活性: 用户可以决定是否手动干预
```

### 6.2 Agent 如何判断是否需要设计？

```
实现方式: 通过 Agent 提示词控制

Agent 根据以下因素综合判断:
- Issue 复杂度 (任务数量、代码改动量)
- 涉及的模块数量
- 是否有架构影响
- 是否有 API 变更
- 是否有数据库 schema 变更

判断结果:
- 需要设计 → Agent 添加 [need-design] 标签
- 不需要 → 直接进入 Implement

具体判断标准由提示词定义，不在工作流层面硬编码
```

### 6.3 如何检测 Review 通过？

```
实现方式: 组合检测（优先级从高到低）

1. PR Approved Review (优先)
   - GitHub PR review 状态为 "approved"
   - 最可靠的信号

2. 用户评论关键词 (备选)
   - 检测评论中的 "通过", "approved", "lgtm", "好" 等关键词
   - 适用于非正式审查

3. 用户手动移除标签 (兜底)
   - 用户直接移除 [need-review] 标签
   - 明确的通过信号

检测流程:
   检查 PR review 状态
       ├─ approved → 移除 [need-review]
       └─ 无 approved → 检查评论关键词
                        ├─ 发现关键词 → 移除 [need-review]
                        └─ 无关键词 → 检查标签是否被手动移除
```

---

## 7. 异常流程

### 7.1 Issue 被用户关闭

```
场景: 用户关闭 Issue

处理:
1. 检测 Issue 状态为 closed
2. 如果有关联的 PR:
   - 在 PR 中添加 Comment 说明 Issue 已关闭
   - 关闭 PR (可选)
3. 停止处理该 Issue

Agent 行为:
- 定期检查 Issue 状态
- 发现关闭后停止相关工作
```

### 7.2 合并冲突

```
场景: PR 有合并冲突

处理:
1. Agent 检测到合并冲突
2. 尝试自动解决:
   - git pull origin main
   - 解决冲突
   - 提交解决
3. 如果自动解决失败:
   - 在 PR 中添加 Comment
   - 等待用户手动解决或提供指导
4. 冲突解决后继续流程

技术实现:
- 在 [need-review] 阶段检测冲突
- 优先处理冲突，避免阻塞合并
```

### 7.3 依赖的 Issue 未完成

```
场景: Issue #A 依赖 Issue #B，但 #B 未完成

处理:
1. Issue #A 标注依赖: "Depends on #B"
2. Agent 检测到依赖关系
3. 检查 #B 状态:
   - 未完成 → 在 Issue #A 中添加 Comment 说明等待依赖
   - 已完成 → 继续处理 #A

建议:
- 用户在创建 Issue 时标注依赖关系
- Agent 可识别 "Depends on #N" 或 "依赖 #N" 关键词
```

### 7.4 需求太大

```
场景: Review 发现需求太大，难以在一个 PR 中完成

处理:
1. 用户 Review 时指出需求太大
2. Agent 提出拆分方案:
   - 识别可独立的功能模块
   - 创建子 Issue #N.1, #N.2, #N.3
   - 在原 Issue 中添加子任务清单
3. 用户确认拆分方案
4. 原 Issue 关闭或标记为 Epic
5. 子 Issue 分别处理

技术实现:
- Agent 通过提示词识别可拆分的点
- 子 Issue 引用父 Issue: "Part of #N"
```

---

## 8. 流程示例

### 8.1 简单 Bug 修复

```
Issue #42: 修复登录页面按钮对齐问题

流程:
1. 用户创建 Issue #42，添加 [draft]
2. 用户挑选 Issue #42
3. Agent 与用户对话 (Refinement):
   - Agent: "请提供截图和复现步骤"
   - 用户: 添加截图
   - Agent: 更新 Issue Body，添加任务清单
4. 用户审查需求通过，移除 [draft]
5. 创建 PR #100，关联 Issue #42
6. Agent 判断: 简单 CSS 修复，不需要设计
7. Agent Implement:
   - 修改 styles/login.css
   - 提交 commit
8. Agent 添加 [need-review]
9. Agent 自动 Review (lint, test) 通过
10. 用户 Review:
    - 查看预览效果
    - 添加 "approved" review
11. Agent 检测到 approved，移除 [need-review]
12. Agent 合并 PR #100
13. 关闭 Issue #42

总时长: ~30 分钟
```

### 8.2 复杂特性开发

```
Issue #88: 添加用户权限管理功能

流程:
1. 用户创建 Issue #88，添加 [draft]
2. 用户挑选 Issue #88
3. Agent 与用户对话 (Refinement):
   - Agent: "需要哪些角色？每个角色有什么权限？"
   - 用户: "管理员、编辑、查看者三个角色..."
   - Agent: "是否需要权限审计日志？"
   - 用户: "需要"
   - Agent: 更新 Issue Body，添加详细任务清单
4. 用户审查需求通过，移除 [draft]，添加 [need-design]
5. 创建 PR #120，关联 Issue #88
6. Agent 生成设计:
   - design.md: 架构设计、权限模型
   - tasks.md: 15 个任务
   - specs/: API spec, DB schema
7. 提交到 PR #120
8. 用户 Review 设计:
   - 用户: "权限继承逻辑需要调整"
   - Agent: 修改设计文档
   - 用户: 添加 "approved" review
9. Agent 移除 [need-design]
10. Agent Implement (Ralph Loop):
    - 执行 tasks.md 中的任务
    - 分批提交:
      - commit 1: DB schema
      - commit 2: API endpoints
      - commit 3: 前端 UI
      - commit 4: 测试
11. Agent 添加 [need-review]
12. Agent 自动 Review:
    - npm run lint: 通过
    - npm run test: 通过
    - npm run typecheck: 通过
13. 用户 Review:
    - 测试功能
    - 添加评论: "角色切换有个小 bug"
    - Agent: 修复 bug，提交 commit
    - 用户: 添加 "approved" review
14. Agent 检测到 approved，移除 [need-review]
15. Agent 合并 PR #120
16. 关闭 Issue #88

总时长: ~2-3 天 (含 Review 时间)
```

---

## 9. 实现建议

### 9.1 标签命名

```
使用简洁命名:

- draft (颜色: 灰色 #6e7681)
- need-design (颜色: 黄色 #fbca04)
- need-review (颜色: 蓝色 #1d76db)

不使用前缀，保持简洁易读
```

### 9.2 Agent 行为

```
Agent 需要实现:

1. 检测标签
   - 定期查询 draft issues
   - 检测标签变化

2. Refinement
   - 与用户对话
   - 更新 Issue Body
   - 检测用户审查通过

3. Design
   - 生成设计文档
   - 判断是否需要设计
   - 自动审查设计 (如果用户未要求)

4. Implement
   - 执行 tasks
   - 处理技术失败
   - 添加 [need-review]

5. Review
   - Agent Review
   - 检测用户审查通过
   - 合并 PR
```

---

## 10. 演进历史

### 2026-03-06: IssueFlow 设计诞生

**背景**:
- 从 `ready-for-*` 命名讨论开始
- 发现 blocker-driven 的设计模式
- 简化为 3 个核心标签

**关键洞察**:
1. 所有标签都是"阻碍条件"
2. Review 被拒绝 = 继续停留在当前标签
3. 用户控制审查级别，Agent 有自主性
4. Refinement 和 Design 不存在失败，只有 Review 被拒绝

**产出**:
- `design/issueflow.md`: 本文档
- 极简的 3 标签体系
- 用户驱动的审查级别设计

---

## 11. 参考资料

- [workflow.md](../workflow.md) - 原始工作流设计
- [workflow-design.md](./workflow-design.md) - 工作流设计讨论
- [ralph-cli.md](./ralph-cli.md) - Ralph CLI 设计

---

**标签**: workflow, issueflow, design-decisions
