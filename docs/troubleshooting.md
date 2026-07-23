# 故障恢复

Issue 跑挂了怎么办？这份是速查手册。

## 先判断状态

```bash
mo issue show <number>
```

或 Web UI 看详情页。关注三个字段：

| 字段 | 含义 |
|---|---|
| `health` | `blocked` / `cancelled` / `done` |
| `status` | `in-progress` / `done` / `cancelled` |
| `blockedReason` | 如果 blocked，原因是什么 |

## 健康度对照表

各 health 状态的语义见 [工作流详解 · 健康度](the-workflow.md#健康度health)。本篇只关心每个状态对应的动作：

| Health | 你该做什么 |
|---|---|
| `active` | 等 |
| `paused` | Approve / Reject / Resume |
| `blocked` | 看下面"恢复动作" |
| `cancelled` | Reopen（如需要） |
| `done` | 验收 / 归档 |

## 恢复动作速查

| 场景 | 命令 | 说明 |
|---|---|---|
| AI 自检失败（check 没过） | `mo run retry --issue <n>` | 重新跑当前失败点 |
| Runner 崩了且自动恢复失败 | `mo run retry --issue <n>` | Runner 恢复后重试失败点 |
| 想完全重做当前阶段 | `mo run rerun --issue <n> --from-stage <stage>` | 丢弃目标阶段及之后产物重跑 |
| 当前阶段彻底卡死 | `mo run pause --issue <n>` | 暂停当前执行，后续用 resume 继续 |
| 不想继续了 | `mo run stop --issue <n> --yes` | 永久终止运行，不能恢复 |
| Workflow 已停止，工作已通过其它方式交付 | `mo issue done <n>` | 进入 Done，保留原 workflow 历史 |
| 完全放弃 | `mo issue close <n>` | 进 cancelled 终态 |

**所有恢复命令都保留 issue 历史**。除非 close + archive，否则状态和产物都不会丢。

**retry 会拿回完整的自动恢复预算**。审查-修复循环耗尽预算失败后，retry 让它从头再来一轮。

## 常见失败模式

### 1. Plan 阶段产不出 proposal.md

**症状**：Plan 阶段 blocked，`proposal.md` 不存在。

**可能原因**：
- opencode 没装好或路径不对
- AI 模型 API 没配 / 限速
- Issue body 太模糊，AI 反复犹豫

**排查**：

```bash
mo issue logs <n>      # 看具体错误
mo issue sessions <n>  # 看 AI 实际在想什么
```

**解决**：

- 确认 `opencode --help` 工作
- Web UI Settings → OpenCode 检查模型配置
- 改 issue body 更具体，retry

### 2. Build 阶段写不出代码

**症状**：Build task 反复失败。

**可能原因**：
- 项目代码库太大，AI 上下文不够
- 测试套件本身有问题，AI 跑不过
- 任务定义矛盾

**排查**：

```bash
mo issue sessions <n>   # 看 AI 的挣扎过程
```

**解决**：

- 改 tasks.json（删掉卡住的任务）
- Reject 这个 plan，让 AI 重新规划
- 拆 issue 成更小的（reject + 建 sub-issue）

### 3. Check 阶段 review 失败

**症状**：Check 的 AI review 给出 fail verdict。

**含义**：AI 自己 review 自己的代码发现问题。

**这其实是好事**。Workflow 会自动触发 re-build 修复（如果 profile 配了 convergence）。

**你的选择**：

- 等 convergence 自动修复（看收敛面板）
- Reject 让 AI 重新 build
- Approve 接受现状（review 的问题不致命）

### 4. Integrate 失败：merge conflict

**症状**：Integrate blocked，提示 conflict。

**原因**：Base branch 在 issue 跑的过程中被推进了（别的 issue 合并了、或你手动推了）。

**解决**：

```bash
mo issue rebase <n>     # 尝试自动 rebase
```

如果自动 rebase 也冲突：

1. 进 worktree：`cd <repo>/.mohist/worktrees/issue-<n>/`
2. 手动解决冲突
3. `git add` + `git rebase --continue`
4. 回到 Mohist：`mo run resume --issue <n>`

### 5. Runner 不可用

**症状**：Workflow 长时间等待，或 Issue 因 `runner-lost` 进入 blocked。

**原因**：Runner 没有运行、失去连接，或自动恢复没有成功。

**解决**：

```bash
# 确保 Runner 已经起来
mo server status
```

仍在等待的 Workflow 会自动继续。已经 blocked 的 Workflow 在 Runner 恢复后执行 Retry；已完成阶段和历史不会丢失。

### 6. AgentSession 卡死（无输出）

**症状**：Issue 显示 running，但长时间（> 10 分钟）无任何输出。

**排查**：

```bash
mo issue sessions <n>   # 看最后一行
mo server logs          # 看应用级日志（Mohist server 自身的 log tail）
```

**解决**：

```bash
mo run pause --issue <n>    # 暂停
mo run resume --issue <n>   # 从断点继续
# 或
mo run retry --issue <n>    # 重试失败点
```

### 7. Drift 警告

**症状**：Issue 详情页出现 "Base Drift Detected" 卡片。

**含义**：Base branch 在你 issue 跑的过程中推进了。当前还没失败，但 Integrate 时可能失败。

**Drift decision**：

| Decision | 含义 | 你做什么 |
|---|---|---|
| `needs-attention` | 必须处理 | 立刻 rebase |
| `defer` | 等到合适时机自动处理 | 等 |
| `suggest` | 建议你处理 | 看情况 |
| `enqueue` | 排队处理 | 等 |

**操作**：

```bash
mo issue rebase <n>     # 主动 rebase
```

或者忽略——某些 drift 会自动消化。

## 怎么知道出问题了

不用每天盯。Mohist 会在这些时机通知你：

- **看板顶部 Needs attention 条**（Web UI）
- **Issue card 上的 blocked pill**（红色）
- **Issue 详情页的红色错误框**

未来会有 push notification（roadmap）。当前需要主动查看。

## 预防性建议

### 1. Issue body 写清楚

> 70% 的失败本质是 issue body 模糊导致 plan 错。花 5 分钟写好 body，省 30 分钟救火。

参考 [Issue 管理](issues.md) 的 body 写法。

### 2. 小步快跑

不要一个 issue 干 5 件事。拆成 5 个 issue：
- 失败好恢复
- Plan 质量高
- 能并行

### 3. 别在 AI 跑的时候动 base branch

会导致 drift / conflict。

### 4. 监控 capacity

别一次启动 20 个 issue 超过 capacity。会排队但你看不到。

```bash
mo server status   # 看 capacity 使用
```

### 5. 定期清理 worktree

```bash
# 列出所有 worktree（在 repo 根目录）
git worktree list

# 清理已完成 issue 的 worktree
git worktree prune
```

### 6. 关注反复失败的模式

如果多个 issue 反复在同一类任务上 blocked，不要只逐个 retry。常见原因是：

- issue 输入不够清楚，Plan 经常跑偏
- 测试慢、不稳定，Check 经常误伤
- 模块边界混乱，Inline Agent 难以在短上下文里完成修改
- workflow profile 不适合当前任务类型

这类问题会拖慢整条生产线。优先修输入模板、测试、模块边界或 workflow profile，再继续扩大并发。

## 找不到原因？

```bash
# 完整诊断
mo issue logs <n>
mo issue events <n>
mo issue sessions <n>
mo server logs

# 还不行
# Web UI → Logs 页 → 找 error 级别日志
```

如果是 Mohist 本身的 bug，提 issue：

```
https://github.com/<your-org>/mohist/issues
```

附上：
- Issue number
- Health / status / blockedReason
- Logs 关键片段
- 复现步骤

---

对应源码：跨域；恢复逻辑见 `Issue/`、`Workflow/`（health / blocked 处理）。
