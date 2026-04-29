## MODIFIED Requirements

### Requirement: Pipeline 由有序 Stage 组成

Pipeline SHALL 由有序 Stage 组成：Plan → Build → Review → Done。Stage 之间串行执行，不可跳过或乱序。Review 阶段完成后需 mergeBack 成功才能进入 Done。

#### Scenario: Issue 进入 pipeline

- **WHEN** Issue 被启动（`mo issue start <id>`）
- **THEN** Issue stage 从 `draft` 变为 `plan`
- **AND** Plan stage 开始执行

#### Scenario: Stage 顺序推进

- **WHEN** Plan stage 完成
- **THEN** Issue stage 变为 `build`
- **WHEN** Build stage 完成
- **THEN** Issue stage 变为 `review`
- **WHEN** Review stage 审批通过且 mergeBack 成功
- **THEN** Issue stage 变为 `done`

#### Scenario: Review 审批通过但 mergeBack 失败

- **WHEN** Review stage 审批通过
- **AND** mergeBack 执行失败
- **THEN** Issue stage SHALL NOT 变为 `done`
- **AND** Issue SHALL 回退到 Build 阶段进行冲突解决（如可自动解决）
- **OR** Issue SHALL 标记为 `mergeState = Blocked`（如重试次数耗尽）
