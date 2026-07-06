# Epic 状态反映现实——issue-392 决策记录

本文记录 issue-392（Epic 状态反映现实：done 的 epic 加 issue 后自动唤醒）在 plan 阶段收敛的架构与产品决策。

## 背景

Epic 有两个终态：

- `done`：里程碑正常完成。
- `closed`：里程碑被放弃。

之前 `LinkIssueAsync` 对终态 epic 走"归档式 link"分支：只记录关联、不改变 epic 状态。这导致 epic #40 在第一批子 issue 完成后自动标为 `done`，后续又 link 进 #387、#388 两个 open issue 后，epic 仍显示 `done`，状态与现实脱节。

## 决策

### 1. 放弃"完结后纯归档 link"能力

**决策**：link 到 `done` epic 不再是纯归档操作。若 linked issue 是 open（非终态），epic 自动从 `done` 转回 `running`（"唤醒"）；若 linked issue 已终态，则保持 `done`。

**理由**：

- `done` 的语义必须是"当前没有未完成工作"。一旦又有 open issue 进来，状态必须立刻反映"又有活干了"，否则 `done` 失去可信度。
- 唤醒后 epic 直接回到 `running`（不是 `idle`），因为新 link 的 open issue 本身就是 active work，autopilot 会接管推进，无需用户手动 `start`/`resume`。
- 终态 issue 不会引入新工作，因此不触发唤醒，保留"往 done epic 追加历史记录"的最小能力。

**破坏面**：任何依赖"往 done epic 加 open issue 而状态不变"的用法都会破裂。这是预期内的、 intentional 的破坏——该用法本身反直觉。

### 2. `closed` epic 拒绝所有 link

**决策**：`closed` 是真正的终态（被放弃的里程碑）。任何 issue 都不能再 link 到 `closed` epic；调用方必须先 `Reopen`。

**理由**：

- `done` 与 `closed` 必须有语义区分。`done` 是"完成了但可能还有后续"；`closed` 是"放弃了"。
- 若 `closed` 也能被新 issue 唤醒，则 `closed` 与 `done` 行为一致，`closed` 失去区分意义。
- `Reopen` 是退出 `closed` 的唯一显式通道，保持状态机可解释性。

**破坏面**：之前可以往 `closed` epic 追加 issue 作为归档记录的用法被移除。调用方会收到 `409 EPIC_CLOSED_CANNOT_LINK`。

### 3. 历史 epic #40 处理策略

**决策**：不执行自动数据修复。已处于 `done` 且已 link open issue 的历史 epic（如 #40）保持原状；operator 可通过以下两种方式之一手动恢复：

1. `mo epic unlink <epic> <issue>` 后 `mo epic link <epic> <issue>`——relink 触发唤醒。
2. `mo epic reopen <epic>` 后 `mo epic start <epic>`。

**理由**：

- 唤醒只应在*新 link 事件*发生时触发，避免对既有数据做静默、大规模的状态重写。
- 两种方式都基于现有命令，无数据损坏风险；linked row 本身是正确的，只有 status 是 stale 的。

## 实现要点

- 新增领域迁移 `WakeFromDone`（`done` → `running`），由 grain 在确认 linked issue 为 open 后调用。
- `closed` link 拒绝以 `EpicClosedCannotLinkException` 形式在领域层抛出，API 映射为 `409 EPIC_CLOSED_CANNOT_LINK`。
- 唤醒与 active-membership 行插入在同一 `SaveChangesAsync` 事务内完成；失败则整体回滚，保持 `done`。
- 单条与批量 link 均遵循同一规则；批量下 closed 整批拒绝，done + open 在首个 open item 处唤醒一次。
- 不变量 2（有 open linked issue 时 `MarkDone`/auto-done 被拒）已有实现，本 issue 通过回归测试固化。

## 相关文件

- 领域：`packages/server/src/Mohist.Server/Epic/Domain/Epic.Transitions.cs`、`EpicLifecycleExceptions.cs`
- Grain：`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs`
- API：`packages/server/src/Mohist.Server/Api/EpicRoutes.cs`
- Spec 测试：`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicWakeUpSpecs.cs`、`EpicBatchMembershipSpecs.cs`、`EpicAutoDoneSpecs.cs`
- API 测试：`packages/server/tests/Mohist.Server.Tests/Specs/Epic/Api/EpicBatchMembershipApiSpecs.cs`
