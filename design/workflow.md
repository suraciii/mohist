# Workflow 设计

## 整体流程概览

```
用户提出想法
    ↓
ready-for-refinement    ← 创建 Issue，OpenClaw 与用户对话澄清需求
    ↓
ready-for-design        ← 用户说"可以设计了"
    ↓
ready-for-implement     ← 创建 PR #1（仅含 design.md）
    ↓
in-progress             ← Ralph Loop 在 PR 上逐步添加实现 commits
    ↓
ready-for-review        ← 完成实现，Agent Review 通过
    ↓
ready-for-merge         ← 用户审查通过
    ↓
done                    ← PR 合并，Issue 关闭
```

## 各阶段说明

### ready-for-refinement（准备提炼需求）

**触发**：用户提出初步想法  
**驱动动作**：OpenClaw 与用户对话澄清需求  
**输出**：Issue Body 包含完善的需求描述 + 任务清单

用户通过 Issue Comments 与 OpenClaw 对话，所有沟通记录自动同步到 Issue。

### ready-for-design（准备设计）

**触发**：用户说"可以设计了"  
**驱动动作**：OpenClaw 调用 `/opsx:new` 生成设计文档  
**输出**：`openspec/changes/{change}/` 目录包含 design.md、tasks.md、specs/

设计文档包含架构设计、API 规范、任务分解。

### ready-for-implement（准备实现）

**触发**：设计文档已生成  
**驱动动作**：创建 PR #1（初始仅包含 design.md），触发 Ralph CLI  
**输出**：PR #1 创建，标签变为 ready-for-implement

### in-progress（进行中）

**触发**：Ralph CLI 启动  
**驱动动作**：Ralph Loop 循环执行任务  
**过程**：
1. 读取 Issue Comments（检查中断信号）
2. 调用 OpenCode 执行 task
3. 质量门控检查（types → lint → tests）
4. 通过则提交 commit 到 PR #1
5. 失败则指数退避重试（处理 429 限流）

实时更新 Issue Comments 进度报告。

### ready-for-review（准备审查）

**触发**：所有 tasks 完成  
**驱动动作**：Agent Review（OpenCode `/review`），自动修复问题  
**输出**：PR #1 包含完整内容（design.md + 实现 commits）

用户在同一个 PR 中审查设计和实现。

### ready-for-merge（准备合并）

**触发**：用户审查通过  
**驱动动作**：OpenClaw 合并 PR #1

### done（完成）

**触发**：PR 合并  
**动作**：Issue 关闭，标签变为 done

## 中断与回退

### 暂停/恢复

- **暂停**：用户在 Issue 评论"暂停" → 标签变为 status:paused → 保存 checkpoint
- **恢复**：用户评论"继续" → 从 checkpoint 恢复 → 标签变回 in-progress

### 回退（re-evaluate）

**触发**：用户评论"重新设计"或"这不是想要的"

**流程**：
1. 暂停 Ralph Loop
2. 标签变为 re-evaluate
3. 用户选择：
   - **A. 重新设计** → 回到 ready-for-design
     - PR #1 保持打开
     - 添加新 commit 更新设计
     - 废弃旧的实现 commits
   - **B. 调整需求** → 回到 ready-for-refinement
     - 更新 Issue Body
     - 重新设计后再实现

**优势**：完整历史在同一 PR 中可见

## 失败处理

- **status:blocked**：需人工干预（如缺少 API Key），解决后评论"继续"
- **status:failed**：多次重试失败，需人工处理

## 单 PR 设计优势

1. **设计文档和实现代码在同一 PR**：用户可以一站式审查
2. **历史可追溯**：所有演进过程在 PR 的 commits 中可见
3. **回退时保持连续性**：不需要关闭 PR 重新创建
4. **减少 PR 数量**：一个 Issue 对应一个 PR，简化流程

## PR #1 的演进示例

```
Commit 1: "design: add architecture and specs"
Commit 2: "feat: implement user authentication"
Commit 3: "feat: implement article CRUD"
Commit 4: "fix: address review feedback"
...
```

用户审查时可以：
- 先看 design.md 确认设计是否合理
- 再看实现代码是否符合设计
- 所有历史在一个地方，可追溯
