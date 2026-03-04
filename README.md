# OpenClaw + OpenCode + OpenSpec 工作流设计

**核心理念**: 以 Issue 为中心的 Spec-Driven Development + Ralph Coding 自动化工作流

---

## 1. 工作流概览

### 1.1 七个阶段

```
┌─────────────────────────────────────────────────────────────────┐
│  Exploration    →   Refinement    →   Design    →   Implementation │
│   (探索)            (提炼)            (设计)          (实现)      │
│     ↓                  ↓               ↓               ↓        │
│ 创建需求             完善需求         生成设计         Ralph Loop │
└─────────────────────────────────────────────────────────────────┘
                                    ↓
                              Review → Done
                              (审查)   (完成)
                                    ↓
                           Re-evaluation
                           (重新评估)
```

### 1.2 阶段详细说明

#### Exploration（探索）
- **触发**: 用户提出初步想法
- **执行**: OpenCode `/opsx:explore` 分析代码库
- **输出**: GitHub Issues（需求占位符）
- **标签**: `stage:exploration`
- **自动化**: OpenClaw 自动创建 Issues，用户仅对话

#### Refinement（提炼）
- **触发**: 用户选择 issue 深入讨论
- **执行**: OpenClaw 对话 → OpenCode 更新 issue
- **输出**: 完善的 issue body（含任务清单）
- **标签**: `stage:refinement`
- **自动化**: OpenClaw 自动更新 issue，用户仅对话

#### Design（设计）
- **触发**: 用户说"可以设计了" 或 issue 标记 `ready-for-design`
- **执行**: OpenCode `/opsx:new` + `/opsx:ff` 生成 artifacts
- **输出**: Design PR + specs/ 目录
- **标签**: `stage:design`
- **自动化**: 100%

#### Implementation（实现）
- **触发**: Design PR merged
- **执行**: OpenCode `/opsx:apply` + Ralph Loop
- **输出**: Implementation PR + commits
- **标签**: `stage:implementation`
- **自动化**: 95%（仅异常时中断）

#### Review（审查）
- **触发**: Implementation PR created
- **执行**: 
  - Agent Review: OpenCode `/review` 命令
  - User Review: 用户在 PR 上评论
  - Ralph 根据 review 评论调整代码
- **输出**: 修复 commits
- **标签**: `stage:review`
- **自动化**: Agent Review 100%，User Review 可选

#### Re-evaluation（重新评估）
- **触发**: 
  - 发现重大设计缺陷
  - 需求理解偏差
  - 技术方案不可行
- **执行**: OpenCode 重新分析，可能回到 Design 或 Refinement
- **输出**: 新的 Design 或更新后的 issue
- **标签**: `stage:re-evaluation`
- **自动化**: OpenCode 分析 + OpenClaw 通知用户决策

#### Done（完成）
- **触发**: PR merged
- **标签**: `stage:done` + issue closed
- **后续**: 可选进入质量债务流程

### 1.3 为什么叫"Re-evaluation"?

**备选名称对比**:
- `Rework`（返工）: 太负面，暗示失败
- `Redesign`（重新设计）: 过于局限，可能需要回到需求阶段
- `Re-evaluation`（重新评估）: 中性、准确，表示重新评估当前方案
- `Pivot`（转向）: 太激进，有时只是微调
- `Adjust`（调整）: 力度不够，可能涉及重大变更

**选择 Re-evaluation 的理由**:
1. **准确**: 表示重新评估当前状态，决定下一步
2. **中性**: 不暗示失败，只是发现更好的方案
3. **灵活**: 可以回到 Design 或 Refinement
4. **专业**: 符合工程实践中的"重新评估"概念

---

## 2. 完整工作流

### 2.1 标准流程（成功路径）

```
User: 我想做一个博客系统
    ↓
OpenClaw: 启动 Exploration
    ↓
OpenCode: /opsx:explore
    → 创建 Issues: #1, #2, #3...
    → 标签: stage:exploration
    ↓
OpenClaw: "发现3个模块，聊哪个？"
    ↓
User: 聊聊 #2 文章管理
    ↓
OpenClaw: 进入 Refinement 模式
    → OpenCode 读取并更新 issue
    → 标签: stage:refinement
    → 添加评论: "当前讨论: 版本历史功能"
    ↓
User: 可以设计了
    ↓
OpenClaw: 进入 Design 阶段
    → OpenCode /opsx:new → artifacts
    → 创建 Design PR
    → 标签: stage:design
    ↓
[用户/Agent Review Design PR]
    ↓
User: 合并 Design PR
    ↓
OpenClaw: 进入 Implementation
    → OpenCode /opsx:apply
    → Ralph Loop 后台执行
    → 标签: stage:implementation
    ↓
[定期更新 progress comment]
    ↓
OpenClaw: Implementation PR created
    → OpenCode /review (Agent Review)
    → 标签: stage:review
    ↓
User: PR 上评论 "这里需要优化"
    ↓
Ralph Loop 读取评论
    → 修复代码
    → 提交新 commit
    → PR 评论: "已修复，请审查"
    ↓
User: 合并 PR
    ↓
OpenClaw: Done
    → 标签: stage:done
    → 询问: "开始质量债务审查？"
```

### 2.2 回退流程（Re-evaluation 路径）

```
[Implementation 阶段]
    ↓
User: "这个设计有问题，需要重新考虑"
    ↓
OpenClaw: 创建 Re-evaluation Comment
    → "用户标记需要重新评估"
    → 标签: stage:re-evaluation
    ↓
OpenClaw: 暂停 Ralph Loop
    ↓
OpenCode: 分析当前状态
    → 读取 issue history
    → 识别问题根源
    → 生成评估报告
    ↓
OpenClaw: 提供选项
    "问题分析:
    1. 数据库模型设计不合理
    
    建议:
    A. 回到 Design 阶段，重新设计模型
    B. 回到 Refinement，调整需求范围
    
    请选择:"
    ↓
User: 选择 A
    ↓
OpenClaw: 回到 Design 阶段
    → 标签: stage:design
    → OpenCode 重新生成 artifacts
    → 创建新的 Design PR (v2)
```

---

## 3. 双向通信机制

### 3.1 通信协议

**设计原则**: Issue Comments 作为命令总线

#### 3.1.1 OpenClaw → Issue（自动）

当用户与 OpenClaw 对话时，OpenClaw 自动同步到 Issue:

```
User: 文章管理需要支持多规格
    ↓
OpenClaw 理解意图
    ↓
OpenClaw 调用 GitHub API
    POST /repos/{owner}/{repo}/issues/{number}/comments
    {
      "body": "💬 **需求提炼**\n\n用户提出: 支持多规格 (SKU/SPU)\n\n状态: 已添加到任务清单"
    }
    ↓
OpenClaw: "已更新 issue #2"
```

#### 3.1.2 User → Issue（手动或经 OpenClaw）

**方式 A**: 用户直接在 GitHub 评论
```
User 在 Issue #2 评论:
"我觉得还应该支持批量导入"
    ↓
GitHub Webhook → OpenClaw
    ↓
OpenClaw: "收到新评论，是否需要添加到需求？"
    ↓
User: 是
    ↓
OpenCode 更新 issue
```

**方式 B**: 用户通过 OpenClaw
```
User: 给 issue #2 添加评论 "支持批量导入"
    ↓
OpenClaw 直接调用 GitHub API 添加评论
```

#### 3.1.3 Ralph Loop ↔ Issue（自动）

Ralph Loop 定期读取 Issue Comments:

```python
# Ralph Loop 伪代码
def ralph_iteration():
    # 读取任务清单
    tasks = read_file('tasks.md')
    
    # 读取最新评论
    comments = github_api.get_issue_comments(issue_number)
    
    # 检查是否有干预指令
    for comment in comments:
        if comment.user == 'user' and comment.created_at > last_check_time:
            if '暂停' in comment.body or 'PAUSE' in comment.body:
                pause_loop()
                return 'PAUSED_BY_USER'
            elif '方向错了' in comment.body:
                trigger_reevaluation()
                return 'REEVALUATION_NEEDED'
    
    # 执行当前任务
    current_task = get_next_task(tasks)
    result = opencode(f'Implement: {current_task}')
    
    # 更新进度评论
    github_api.add_comment(
        issue_number,
        f'✅ **Progress Update**\n\n'
        f'Completed: {current_task}\n'
        f'Remaining: {len(remaining_tasks)} tasks\n'
        f'Next: {next_task}'
    )
```

### 3.2 中断机制

#### 3.2.1 标签中断

用户添加特定标签触发中断:

| 标签 | 含义 | 动作 |
|------|------|------|
| `action:pause` | 暂停任务 | Ralph Loop 优雅暂停，保存状态 |
| `action:reevaluate` | 重新评估 | 进入 Re-evaluation 阶段 |
| `action:cancel` | 取消任务 | 停止 Ralph Loop，清理资源 |
| `action:priority-high` | 提高优先级 | 调整队列顺序 |

#### 3.2.2 评论中断

特定关键词触发:

```
用户评论: "@openclaw 暂停这个任务"
    ↓
OpenClaw 检测 @mention
    ↓
添加标签: action:pause
    ↓
Ralph Loop 读取标签，暂停执行
```

#### 3.2.3 智能中断（OpenCode 触发）

当 OpenCode 遇到无法解决的问题:

```
OpenCode: "实现 JWT 认证时发现没有 SECRET_KEY 环境变量"
    ↓
OpenCode 无法继续
    ↓
通知 OpenClaw: "需要用户干预"
    ↓
OpenClaw: 在 Issue 添加评论
    "🛑 **需要干预**\n\n"
    "OpenCode 遇到阻塞问题:\n"
    "- 缺少 SECRET_KEY 环境变量\n\n"
    "请提供后回复 '继续'"
    → 标签: action:needs-input
    ↓
等待用户回复
```

---

## 4. 回退机制

### 4.1 触发条件

进入 Re-evaluation 阶段的条件:

1. **用户主动标记**
   - 评论: "需要重新设计"
   - 添加标签: `action:reevaluate`

2. **Ralph Loop 发现重大问题**
   - 连续 3 次实现失败
   - 发现设计与代码冲突
   - 技术方案不可行

3. **Review 阶段发现根本问题**
   - Agent Review: "架构存在缺陷"
   - User Review: "需求理解错了"

### 4.2 回退流程

```
[当前阶段: Implementation]
    ↓
触发: 用户评论 "设计有问题"
    ↓
OpenClaw:
    1. 暂停 Ralph Loop
    2. 添加标签: stage:re-evaluation
    3. 在 Issue 创建评估 Comment
    4. 通知 OpenCode 进行分析
    ↓
OpenCode:
    1. 读取完整历史（issue + PRs + commits）
    2. 识别问题根源
    3. 生成评估报告
    ↓
OpenClaw 向用户展示:
    "📋 **Re-evaluation 报告**\n\n"
    "问题:\n"
    "- 数据库模型未考虑并发写入\n\n"
    "影响:\n"
    "- 需要修改 User 和 Article 表\n"
    "- 已实现的 3 个 commits 需要调整\n\n"
    "建议:\n"
    "A. 回到 Design，重新设计模型 [推荐]\n"
    "B. 回到 Refinement，缩小需求范围\n\n"
    "请选择:"
    ↓
User: 选择 A
    ↓
OpenClaw:
    1. 标签改为: stage:design
    2. 创建 Design PR v2
    3. 在 PR 中说明回退原因
    ↓
[继续标准流程]
```

### 4.3 状态保留与迁移

**保留的内容**:
- 所有 Git commits（历史可追溯）
- Issue comments（决策记录）
- OpenCode 记忆（上下文连续性）

**废弃的内容**:
- 未完成的 tasks（重新设计后重新分解）
- 临时代码（rebase 或新建分支）

**迁移策略**:
```
回退到 Design:
    - 新建分支: feature/#2-v2
    - 保留原有分支: feature/#2 (存档)
    - 重新生成 artifacts
    
回退到 Refinement:
    - 保持当前分支
    - 更新 issue body
    - 调整任务清单
```

---

## 5. Review 体系

### 5.1 三层 Review 架构

```
┌─────────────────────────────────────────────┐
│  Layer 3: User Review（用户审查）            │
│  - 最终决定权                                │
│  - 关键决策确认                              │
│  - 可选（可配置为全自动）                    │
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│  Layer 2: Agent Review（Agent 审查）         │
│  - OpenCode /review 命令                     │
│  - 代码质量检查                              │
│  - 设计一致性验证                            │
│  - 100% 自动                                 │
└──────────────────┬──────────────────────────┘
                   │
┌──────────────────▼──────────────────────────┐
│  Layer 1: Auto Verification（自动验证）      │
│  - types/tests/lint                          │
│  - CI/CD checks                              │
│  - 硬性门槛                                  │
└─────────────────────────────────────────────┘
```

### 5.2 Agent Review 流程

```
Implementation PR created
    ↓
OpenClaw: 启动 Agent Review
    ↓
OpenCode: /review --pr 6
    ↓
OpenCode 执行:
    1. 读取 Design artifacts
    2. 对比实现 vs 设计
    3. 代码质量分析
    4. 生成 Review Report
    ↓
OpenCode 在 PR 添加评论:
    "🤖 **Agent Review Report**\n\n"
    "## 设计一致性 ✅\n"
    "- 实现符合 architecture.md\n"
    "- API 符合 api-spec.md\n\n"
    "## 代码质量 ⚠️\n"
    "- 建议优化: UserService 过于臃肿\n"
    "- 建议提取: Auth 逻辑到独立模块\n\n"
    "## 测试覆盖 ✅\n"
    "- 85% 覆盖率，超过 80% 门槛\n\n"
    "## 建议\n"
    "可合并，但建议后续重构 UserService"
    ↓
Ralph Loop 读取 Review 评论
    → 如果发现 issues，自动修复
    → 提交修复 commits
    → 更新评论: "已修复: UserService 已拆分"
    ↓
User Review（可选）
    ↓
合并 PR
```

### 5.3 Review 评论处理

Ralph Loop 自动处理 Review 评论:

```python
def handle_review_comments(pr_number):
    comments = github_api.get_pr_comments(pr_number)
    
    for comment in comments:
        if comment.user == 'opencode-bot':  # Agent Review
            # 解析建议
            issues = parse_review_comment(comment.body)
            
            for issue in issues:
                # 自动修复
                fix_result = opencode(f'Fix: {issue.description}')
                
                # 提交修复
                git_commit(f'fix: {issue.title}')
            
            # 回复 Review
            github_api.reply_comment(
                comment.id,
                f'✅ 已修复 {len(issues)} 个问题'
            )
            
        elif comment.user == 'user':  # User Review
            # 解析用户反馈
            if '需要修改' in comment.body:
                # 执行用户要求的修改
                opencode(f'根据用户反馈修改: {comment.body}')
                git_commit('fix: address user review feedback')
                
                # 通知用户
                github_api.add_comment(
                    pr_number,
                    '已根据反馈修改，请再次审查'
                )
```

### 5.4 全自动 Review 配置

对于信任度高的项目，可配置全自动:

```yaml
# .openclaw/config.yaml
review:
  user_review_required: false  # 不需要用户审查
  auto_merge_on_pass: true     # Agent Review 通过后自动合并
  merge_conditions:
    - agent_review: passed
    - ci_checks: passed
    - test_coverage: ">= 80%"
    - no_critical_issues: true
```

---

## 6. 异常处理

### 6.1 异常分级

| 级别 | 描述 | 处理 | 用户通知 |
|------|------|------|----------|
| **Info** | 信息提示 | 记录日志，继续执行 | 否 |
| **Warning** | 警告 | 尝试自动修复 | 结束后的总结 |
| **Error** | 错误 | 重试 3 次，然后暂停 | 立即通知 |
| **Critical** | 严重错误 | 立即停止，保存状态 | 立即通知 + 详细报告 |

### 6.2 OpenCode 无法继续的情况

当 OpenCode 遇到无法解决的问题:

```
OpenCode: "实现支付接口时发现没有 API Key"
    ↓
OpenCode 尝试:
    1. 检查环境变量
    2. 检查配置文件
    3. 搜索文档
    ↓
所有尝试失败
    ↓
OpenCode 返回错误代码: NEEDS_USER_INPUT
    ↓
OpenClaw 接收错误
    ↓
OpenClaw:
    1. 暂停 Ralph Loop
    2. 在 Issue 添加详细评论:
       "🛑 **执行中断**\n\n"
       "问题: 缺少支付 API Key\n\n"
       "已尝试:\n"
       "- 检查环境变量: 未找到 STRIPE_KEY\n"
       "- 检查 .env 文件: 不存在\n\n"
       "需要您:\n"
       "1. 设置 STRIPE_KEY 环境变量，或\n"
       "2. 创建 .env 文件添加 API Key\n\n"
       "完成后回复 '继续' 恢复执行"
    3. 添加标签: action:needs-input
    4. 通知用户（桌面通知/邮件）
    ↓
等待用户干预...
```

### 6.3 恢复执行

用户解决问题后:

```
User: 继续
    ↓
OpenClaw:
    1. 检查问题是否解决
    2. 移除标签: action:needs-input
    3. 恢复 Ralph Loop
    4. 添加评论: "✅ 恢复执行"
    ↓
Ralph Loop 继续从断点执行
```

---

## 7. 质量债务流程

### 7.1 设计原则

- **不阻塞主流程**: 质量债务不阻塞功能交付
- **可追溯**: 所有债务登记到独立 issues
- **可量化**: 债务有严重程度和估算工时
- **可偿还**: 定期安排债务清理 sprint

### 7.2 债务识别

在 Implementation 阶段，Ralph Loop 可以标记债务:

```
OpenCode: "为了赶进度，这里用了临时方案"
    ↓
在代码中添加标记:
    // TODO-DEBT: 临时使用内存缓存，应改为 Redis
    // SEVERITY: medium
    // ESTIMATE: 4h
    // REASON: MVP 快速验证
    
    ↓
OpenClaw 检测 TODO-DEBT 标记
    ↓
创建技术债务 Issue:
    标题: "[TECH-DEBT] 替换内存缓存为 Redis"
    标签: tech-debt, severity:medium
    内容:
        - 位置: src/cache.js:15
        - 原因: MVP 临时方案
        - 影响: 无法水平扩展
        - 估算: 4 小时
        - 关联: #2 文章管理功能
```

### 7.3 债务审查流程

```
功能完成后
    ↓
OpenClaw: "是否进行质量债务审查？"
    ↓
User: 是
    ↓
OpenCode 执行债务分析:
    1. 扫描所有 TODO-DEBT 标记
    2. 分析代码复杂度
    3. 检查测试覆盖盲区
    4. 识别重复代码
    ↓
生成债务报告:
    "📊 **质量债务报告**\n\n"
    "功能: #2 文章管理\n\n"
    "发现债务:\n"
    "1. [medium] 内存缓存 → Redis (4h)\n"
    "2. [low] UserService 过于臃肿 (2h)\n"
    "3. [high] 缺少并发测试 (3h)\n\n"
    "总估算: 9 小时\n\n"
    "建议: 下个 Sprint 清理"
    ↓
创建债务清理 Issue
    ↓
进入债务队列，等待排期
```

### 7.4 债务偿还

债务清理时:

```
User: 开始清理技术债务 #10
    ↓
OpenClaw: 将债务 Issue 转为功能 Issue
    → 标签: stage:refinement
    ↓
进入标准工作流:
    Refinement → Design → Implementation → Review → Done
    ↓
完成后:
    - 关闭债务 Issue
    - 移除代码中的 TODO-DEBT 标记
    - 更新债务统计
```

---

## 8. 系统架构

### 8.1 架构图

```
┌──────────────────────────────────────────────────────────────┐
│                         User Layer                           │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐       │
│  │   Terminal   │  │   GitHub     │  │ Notification │       │
│  │   (对话)     │  │  (Issues/PRs)│  │  (邮件/桌面) │       │
│  └──────────────┘  └──────────────┘  └──────────────┘       │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                     OpenClaw Layer                           │
│  ┌─────────────────────────────────────────────────────┐    │
│  │                 Orchestrator                        │    │
│  │  - Intent Recognition (识别用户意图)                │    │
│  │  - State Machine (阶段状态管理)                     │    │
│  │  - Task Scheduler (任务调度器)                      │    │
│  │  - Communication Bridge (双向通信)                  │    │
│  └─────────────────────────────────────────────────────┘    │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                    OpenCode Layer                            │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐       │
│  │   Explore    │  │    Design    │  │   Implement  │       │
│  │  /opsx:expl  │  │  /opsx:new   │  │  /opsx:apply │       │
│  └──────────────┘  └──────────────┘  └──────────────┘       │
│  ┌──────────────┐  ┌──────────────┐                          │
│  │    Review    │  │    Memory    │                          │
│  │   /review    │  │   System     │                          │
│  └──────────────┘  └──────────────┘                          │
└──────────────────────────┬───────────────────────────────────┘
                           │
┌──────────────────────────▼───────────────────────────────────┐
│                    GitHub Layer                              │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐       │
│  │    Issues    │  │      PRs     │  │    Labels    │       │
│  │  (需求状态)  │  │   (审查)     │  │   (阶段)     │       │
│  └──────────────┘  └──────────────┘  └──────────────┘       │
│  ┌──────────────┐  ┌──────────────┐                          │
│  │   Comments   │  │   Webhooks   │                          │
│  │  (通信总线)  │  │   (事件)     │                          │
│  └──────────────┘  └──────────────┘                          │
└──────────────────────────────────────────────────────────────┘
```

### 8.2 数据流

```
用户输入
    ↓
OpenClaw 意图识别
    ↓
决策:
    ├─ 对话模式 → OpenClaw 直接回复
    ├─ 探索任务 → OpenCode /opsx:explore
    ├─ 设计任务 → OpenCode /opsx:new
    ├─ 实现任务 → OpenCode /opsx:apply (Ralph Loop)
    └─ Review 任务 → OpenCode /review
    ↓
OpenCode 执行
    ↓
GitHub 更新 (Issues/PRs/Comments)
    ↓
Webhook 通知 OpenClaw
    ↓
OpenClaw 通知用户
    ↓
循环或结束
```

### 8.3 OpenCode 记忆系统利用

OpenCode 内部有记忆系统，可以跨任务保持上下文:

```
Exploration 阶段:
    OpenCode: "这个代码库使用 Next.js + Prisma"
    → 记忆存储

Design 阶段:
    OpenCode: "基于之前探索，技术栈是 Next.js..."
    → 读取记忆，保持一致性

Implementation 阶段:
    OpenCode: "按照之前设计的架构..."
    → 记忆确保实现符合设计

Review 阶段:
    OpenCode: "检查是否符合设计时的约定..."
    → 记忆用于验证
```

---

## 9. 对话示例

### 完整会话（含 Re-evaluation）

```
═══════════════════════════════════════════════════════════════
阶段 1-3: Exploration → Refinement → Design
═══════════════════════════════════════════════════════════════

[标准流程，省略...]

═══════════════════════════════════════════════════════════════
阶段 4: Implementation（含中断）
═══════════════════════════════════════════════════════════════

OpenClaw: ✅ Design PR 已合并，开始实现阶段。

🚀 启动 Ralph Loop
任务: 文章管理系统 (12个子任务)
预计时间: 1-2 小时

[30分钟后]

OpenClaw: 📊 进展更新 (自动评论到 Issue)
"🤖 **Ralph Loop Progress**\n\n"
"Progress: 5/12 (41%)\n"
"Current: Implementing version history\n"
"Commits: 3\n"
"Status: ✅ All checks passed"

[用户查看 Issue 评论]

User: 我觉得版本历史应该支持对比功能
    ↓
OpenClaw: 收到，添加到需求
    → OpenCode 更新 issue
    → 添加评论: "已添加: 版本对比功能"
    → Ralph Loop 读取新需求，调整实现

[1小时后]

OpenClaw: ⚠️ **需要干预**
"OpenCode 遇到阻塞:\n"
"- 数据库表 article_versions 已存在\n"
"- 冲突: 之前实验留下的表\n\n"
"建议:\n"
"A. 删除旧表重新创建\n"
"B. 迁移旧数据\n\n"
"请选择或提供其他方案"

User: 删除旧表吧，那是实验数据
    ↓
OpenClaw: ✅ 继续执行
    → OpenCode 删除旧表，继续实现

═══════════════════════════════════════════════════════════════
阶段 5: Review
═══════════════════════════════════════════════════════════════

[Implementation 完成，创建 PR #6]

OpenClaw: 启动 Agent Review

[2分钟后]

🤖 **Agent Review Report** (评论到 PR)
"## 设计一致性 ✅\n"
"## 代码质量 ⚠️\n"
"- UserService 过于臃肿，建议拆分\n"
"## 测试覆盖 ✅\n\n"
"Ralph 将自动修复质量问题"

[Ralph Loop 自动修复]

OpenClaw: ✅ 已修复 1 个问题
"UserService 已拆分为 UserService 和 AuthService"

[用户审查 PR]

User: PR 评论: "版本对比的 UI 还需要优化"
    ↓
Ralph Loop 读取评论
    → 修复 UI
    → 提交 commit
    → PR 回复: "已优化版本对比 UI"

User: 合并吧
    ↓
OpenClaw: ✅ 已合并
    → 标签: stage:done

═══════════════════════════════════════════════════════════════
阶段 6: Re-evaluation（模拟回退场景）
═══════════════════════════════════════════════════════════════

[假设在 Implementation 阶段发现设计问题]

User: 等等，我觉得版本历史的存储方式有问题
    ↓
OpenClaw:
    1. 添加标签: action:reevaluate
    2. 暂停 Ralph Loop
    3. 在 Issue 评论: "用户触发重新评估"
    ↓
OpenCode 分析历史
    → 识别问题: "使用 JSON 存储 diff 不利于查询"
    ↓
OpenClaw:
    "📋 **Re-evaluation 报告**\n\n"
    "问题: 版本历史存储方案不合理\n"
    "影响: 无法高效查询特定字段的变更\n\n"
    "建议:\n"
    "A. 回到 Design，改用单独的 versions 表 [推荐]\n"
    "B. 保持当前方案，牺牲查询性能\n\n"
    "请选择:"

User: 选择 A
    ↓
OpenClaw:
    1. 标签改为: stage:design
    2. 创建 Design PR v2
    3. "基于 Re-evaluation，重新设计版本存储"
    ↓
[继续标准流程]

═══════════════════════════════════════════════════════════════
阶段 7: 质量债务
═══════════════════════════════════════════════════════════════

OpenClaw: "功能已完成。是否进行质量债务审查？"

User: 是
    ↓
OpenCode 扫描代码:
    → 发现 2 个 TODO-DEBT 标记
    → 分析复杂度
    ↓
OpenClaw:
    "📊 **质量债务报告**\n\n"
    "1. [medium] 临时使用内存缓存 (4h)\n"
    "2. [low] 缺少单元测试覆盖 branches (2h)\n\n"
    "已创建债务 Issues: #11, #12"
    
User: 好的，下个 Sprint 清理
```

---

## 10. 配置示例

### 10.1 OpenClaw 配置

```yaml
# .openclaw/config.yaml
workflow:
  # 阶段配置
  stages:
    exploration:
      auto_create_issues: true
    refinement:
      sync_comments: true
    design:
      require_review: true  # 必须审查
    implementation:
      max_concurrent: 3
      ralph_loop:
        check_interval: 300  # 5分钟检查一次
        milestone_every: 5   # 每5个任务暂停检查
    review:
      agent_review: true
      user_review: optional  # 可选，可配置为 required
    reevaluation:
      enabled: true
      
  # 质量配置
  quality:
    auto_verify:
      - "npm test"
      - "npm run lint"
      - "npm run typecheck"
    tech_debt:
      enabled: true
      create_issues: true
      
  # 通信配置
  communication:
    sync_to_github: true
    comment_prefix: "🤖"
    notification:
      on_error: true
      on_complete: true
      on_reevaluation: true
      
  # 异常处理
  exception:
    retry_count: 3
    auto_pause_on_error: true
    user_intervention_keywords:
      - "暂停"
      - "PAUSE"
      - "方向错了"
      - "重新设计"
```

### 10.2 Issue 标签体系

```
stage:exploration      # 探索阶段
stage:refinement       # 提炼阶段
stage:design          # 设计阶段
stage:implementation  # 实现阶段
stage:review          # 审查阶段
stage:re-evaluation   # 重新评估阶段
stage:done           # 完成

action:pause         # 暂停任务
action:reevaluate    # 重新评估
action:cancel        # 取消任务
action:needs-input   # 需要用户输入
action:priority-high # 高优先级
action:priority-low  # 低优先级

tech-debt           # 技术债务
severity:critical   # 严重程度: 严重
severity:high       # 严重程度: 高
severity:medium     # 严重程度: 中
severity:low        # 严重程度: 低
```

---

## 11. 核心改进总结

1. ✅ **双向通信**: Issue Comments 作为命令总线，用户通过评论干预
2. ✅ **回退机制**: Re-evaluation 阶段处理重大问题
3. ✅ **多层 Review**: Agent Review 100% 自动，User Review 可选
4. ✅ **纯 Agent 协作**: OpenClaw 编排，OpenCode 执行（含记忆）
5. ✅ **全自动为主**: 用户仅负责关键决策和最终审查
6. ✅ **异常处理**: OpenCode 无法继续时通知用户干预
7. ✅ **质量债务**: 独立流程，不阻塞功能交付

### 关键成功因素

- **Issue 是真相源**: 所有状态通过 Labels，所有通信通过 Comments
- **阶段清晰**: 7 个明确阶段，流转条件明确
- **自动化优先**: 95% 自动化，5% 人工在关键点
- **可追溯**: 所有决策记录在 GitHub，历史可查
- **灵活回退**: Re-evaluation 允许从错误中恢复

---

**创建时间**: 2026-03-04

---

*本文档记录了完整的 OpenClaw + OpenCode + OpenSpec 工作流设计，包含双向通信、回退机制、多层 Review 和全自动流程。*
