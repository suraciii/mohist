# Epic 状态反映现实（issue-392）

> 本文的产品决策仍有效。membership 的写入权威与事务实现已由
> [`issue-owns-epic-membership.md`](issue-owns-epic-membership.md) 取代：Issue 在自己的
> 事务中提交 `EpicNumber?`，Epic 通过持久事件重算并收敛状态，不再与 membership row
> 共用事务。

## 背景

Epic 有两个终态：`done`（正常完成）与 `closed`（放弃）。之前 `LinkIssueAsync` 对终态 epic 走"归档式 link"（只记录关联、不改变状态），导致 epic 在有新 open issue 加入后状态与现实脱节。

## 决策

### 1. done 自动唤醒

link 到 `done` epic 不再是纯归档操作。若 linked issue 是 open（非终态），epic 自动从 `done` 转回 `running`；若 linked issue 已终态，保持 `done`。

理由：

- `done` 语义必须为"当前没有未完成工作"。一旦又有 open issue 进来，状态须立即反映。
- 唤醒后直接到 `running`（不是 `idle`），因为新 link 的 open issue 本身就是 active work。
- 终态 issue 不引入新工作，不触发唤醒——保留"往 done epic 追加历史记录"的最小能力。

### 2. closed 拒绝 link

`closed` 是真正的终态（被放弃的里程碑）。任何 issue 都不能再 link 到 `closed` epic；调用方须先 `Reopen`。

理由：`done` 与 `closed` 须有语义区分——`closed` 不能被新 issue 唤醒，否则两者行为一致。`Reopen` 是退出 `closed` 的唯一显式通道。

### 3. 历史数据不自动修复

已处于 `done` 且已 link open issue 的历史 epic（如 #40）保持原状。operator 可通过 `unlink + relink` 或 `reopen + start` 手动恢复。避免对既有数据做静默大规模状态重写。

## 实现要点

- 新增领域迁移 `WakeFromDone`（`done` → `running`），确认 linked issue 为 open 后调用。
- `closed` link 拒绝以 `EpicClosedCannotLinkException` 在领域层抛出，API 映射为 `409 EPIC_CLOSED_CANNOT_LINK`。
- Issue 先在自己的事务中提交归属；`IssueEpicChanged` 触发 Epic 重算。Epic 唤醒保存
  失败时由事件重投恢复，不回滚已经提交的 Issue 归属。
- 单条与批量 link 均遵循同一规则；批量下 closed 整批拒绝，done + open 在首个 open item 处唤醒一次。
