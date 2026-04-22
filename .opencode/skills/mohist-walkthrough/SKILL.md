---
name: mohist-walkthrough
description: Mohist 项目 E2E walkthrough 流程验证。走一遍完整的 mohist 工作流（build → server → create issue → start → monitor → approve → done），自动监控进度、发现问题、记录到 talks/ 目录。当需要验证 mohist 流程、测试工作流端到端、或走一遍 dev 流程时使用。触发词包括 "walkthrough"、"走流程"、"验证流程"、"e2e 测试"、"端到端测试"。
---

# Mohist E2E Walkthrough

走一遍完整的 mohist 工作流，验证流程能否走通。发现问题、分析原因、记录结果。

**只负责发现和记录问题，不做修复。**

## 原则

**优先通过 API 观测系统状态，而非直接访问内部存储。** CLI 命令和 HTTP API 是系统的公开接口，直接查数据库或文件系统是最后手段。当你发现自己频繁绕过 API 直接查 DB 时，说明系统缺乏足够的可观测性——这本身就是一个值得记录的问题。

**当无法有效观测系统内部状态、难以诊断问题原因时，记录"可观测性不足"作为发现的问题之一。** 诊断困难往往意味着系统需要在日志、API、状态报告等方面加强。

**相信 agent 的探索能力。** 以下流程提供方向和框架，具体的技术诊断由 agent 自行决定如何探索。不要预设问题类型，让异常自然浮现。

**每个问题都要追到根因。** 发现表面现象后，深入代码和日志定位原因。如果暂时无法定位，明确标记为"未定位"并记录已排除的方向。

## 流程

```
build → server → create issue → start issue → ──→ monitor loop ←──
                                                  │           │
                                              正常推进    异常检测
                                                  │           │
                                              approve ──→ analyze → 记录
                                                  │
                                              done → 总结
```

### 1. 准备

创建记录文件 `talks/<YYYY-MM-DD>-e2e-walkthrough.md`，包含进度和问题两个区域。

### 2. 按序执行

按 build → server → create issue → start issue 的顺序推进，每步记录结果。

### 3. 监控循环

start issue 后进入监控循环。定期检查 issue 状态，直到出现以下情况之一：

- 到达审批点 → 执行审批，继续监控下一阶段
- 状态变为 blocked/draft → pipeline 检测到失败，进入分析
- 状态长时间无变化且 agent 进程不在 → pipeline 卡死，进入分析

监控时关注：系统是否正常运行？状态是否在推进？产物是否在生成？agent 进程是否存活？

### 4. 问题分析

发现异常时，自行决定如何探索和诊断。可用的观测手段：

- CLI 命令 (`mo issue show`, `mo issue list`, `mo server status`)
- HTTP API (`/api/agent/status`, `/api/issues/:id`)
- 日志文件 (`~/.mohist/logs/`)
- Agent 进程状态
- Worktree 文件系统（产物是否生成）
- 源码阅读（理解 pipeline 行为）

当 API 提供的信息不足以诊断时，再考虑直接查数据库或读源码。

### 5. 审批与继续

到达审批点时，查看 issue 产物内容，执行审批，继续监控。

### 6. 总结

流程走完后，更新记录文件：
- 标记完成状态
- 汇总所有发现的问题
- 对每个问题记录现象、根因、建议
- 对可观测性不足的地方提出改进建议

## 记录文件格式

```markdown
# E2E Walkthrough: Mohist 完整流程验证

**日期**: YYYY-MM-DD
**目标**: 走一遍完整流程
**状态**: 进行中 | 已完成

---

## 进度记录

### Step N: <阶段名> ✅/❌/⚠️
- 结果和关键发现

---

## 发现的问题

### 问题 #N: <标题> [严重/中等/低]
- **现象**: ...
- **根因**: ...（或标记"未定位"）
- **证据**: ...
- **建议**: ...

## 可观测性改进建议
- （记录诊断过程中发现的信息缺口）
```
