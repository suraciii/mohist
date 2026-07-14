# 工作流详解

Mohist 默认的工作流由 5 个阶段组成。理解每个阶段做什么、产出什么、什么时候停，你才知道审批和恢复动作在哪里发生。issue 的完整操作命令（创建、启动、审批、恢复等）见 [Issue 管理](issues.md)；自定义阶段、任务、审批策略见 [Workflow Profile](workflow-profiles.md)。

## 全景图

```
Draft ──start──▶ Plan ──approve──▶ Build ──auto──▶ Check ──approve──▶ Integrate ──▶ Done
                    │                                 │
                    │ reject                          │ reject
                    ▼                                 ▼
                  Plan                             Build
                  (redo)                           (redo)
```

每个阶段的产物都留在 `openspec/changes/<issue-number>-<slug>/` 下，作为后续判断和追溯的证据。

## Draft（草稿）

Issue 创建后的初始状态。这时：

- 没有启动 workflow
- Inline Agent 还没开始执行
- 可以编辑 title、body、labels、priority
- 可以加 prerequisites（"等 #N 完成再开始"）

操作：
```bash
mo issue start <number>   # 启动 workflow，进入 Plan
```

## Plan（规划）

Inline Agent 理解需求、规划怎么实现。这是**最重要的阶段**——规划错了后面全错。

### Plan 阶段做的事

按顺序产出 5 个 artifact：

| Artifact | 内容 |
|---|---|
| `proposal.md` | 对需求的理解、范围、动机、提议方案 |
| `specs/` | capability spec 的具体改动（用户故事级别） |
| `design.md` | 技术设计决策（如果有多种实现方式，写清楚选哪个、为什么） |
| `tasks.json` | 接下来 Build 阶段要执行的步骤清单（含验收条件） |
| `self-review.md` | Inline Agent 对 plan 的 self-review（"我考虑了 X、权衡了 Y、担心 Z"） |

### Plan 阶段通常 5-20 分钟

取决于：
- Issue body 的清晰度
- 项目代码库的复杂度
- AI 模型的速度

### Plan 完成后

Workflow 进入审批点，等待 approve / reject 决策：

```bash
mo issue approve <number>   # 通过 plan，进入 Build
mo issue reject <number>    # 打回，重新 plan
```

Workflow 不关心审批者是 owner、Mohist Agent 还是脚本。人工处理时，重点看 proposal.md 和 tasks.json；这是发现方向错误成本最低的位置。

## Build（实现）

Inline Agent 按 tasks.json 里的步骤写代码。

### Build 阶段做的事

- 在 issue 专属的 worktree 里工作（`mo/issue-<number>` 分支）
- 逐个执行 tasks.json 里的任务
- 每个任务完成后跑测试或 lint
- 失败的任务会自动重试或调整
- 每个任务一个 commit

### Build 完成后

**默认自动进入 Check**。如果你希望 Build 后也等待审批，要在 workflow profile 里把 build 的 `requiresApproval` 改为 `true`。

## Check（审查）

Inline Agent 复审 Build 的产出，相当于内部 code review。

### Check 阶段做的事

- 跑完整测试套件
- Inline Agent review 自己的 diff
- 产出 `review.md`（review 结论 + 发现的问题 + 建议修复）
- 如果发现问题，可能触发 re-build 修复

### Check 完成后

Workflow 进入审批点，等待 approve / reject 决策。

```bash
mo issue approve <number>   # 进入 Integrate
mo issue reject <number>    # 回到 Build 重做
```

人工处理时读 `review.md`。Inline Agent 的 review 通常会暴露 Build 阶段没注意到的问题。

## Integrate（合并）

把 `mo/issue-<number>` 分支合并回 base branch。

### Integrate 阶段做的事

- 检查 base branch 是否有 drift（被别人/别的 issue 推进了）
- 如果有 drift：尝试 rebase（可能产生冲突）
- 合并到 base branch
- 推送到远程（如果配置了）

### Integrate 失败

最常见的失败原因：

- Merge conflict（drift 太大，rebase 失败）
- 推送失败（权限/网络）

失败时 issue 进入 blocked 状态，看你介入。详见 [故障恢复](troubleshooting.md)。

## Done（完成）

Issue 完成的终态。这时：

- 代码已经在 base branch 上
- 所有产物已归档在 `openspec/changes/<number>-<slug>/`
- 你可以归档 issue（从看板移走）

```bash
mo issue archive <number>
```

## 状态机完整图

```
                start            approve           auto            approve
Draft ──────▶ Plan ──────▶ Build ──────▶ Check ──────▶ Integrate ──────▶ Done
                ▲              |                       |              |
                |              | reject                | reject       |
                |              ▼                       ▼              |
                └────────── Plan ◄─────────────────── Build           │
                              (redo)                   (redo)         │
                                                                     │
                                                                     ▼
                                                                  Archived
```

任意阶段失败 → blocked → retry / resume / rerun / force-stop。

## 健康度（Health）

除了 workflow stage，issue 还有 health 字段，表示运行健康：

| Health | 含义 |
|---|---|
| `active` | 正在运行，或正在等待系统自动继续 |
| `paused` | 暂停（手动 stop 或等待审批决策） |
| `blocked` | 卡住了，需要你介入 |
| `cancelled` | 你取消了，不会再跑 |
| `done` | 完成 |

Web UI 上每个 issue card 会用颜色点显示 health。

## 什么时候需要处理？

四个时机：

1. **Plan 完成** — 审批点需要 approve 或 reject
2. **Check 完成** — 审批点需要 approve 或 reject
3. **Issue blocked** — 看原因，retry/rerun/stop
4. **Runner 不可用且自动恢复失败** — 按页面给出的操作继续

这些动作可以由 owner、脚本或 Mohist Agent 发起。Workflow 只关心审批动作本身和结果。

## 自定义 Workflow

默认 workflow 不合口味？改它：

- 想让 Build 也等待审批？改 profile 的 `requiresApproval`
- 想跳过 Check？自定义 profile 去掉这个 stage
- 想加新的 stage（如 deploy）？扩展 profile yaml

详见 [Workflow Profile](workflow-profiles.md)。
