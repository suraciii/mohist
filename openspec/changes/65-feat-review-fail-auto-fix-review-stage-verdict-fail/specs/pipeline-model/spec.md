## MODIFIED Requirements

### Requirement: CHECK stage 失败后回到 PLAN

Pipeline SHALL 支持 CHECK stage 内部 auto-fix 循环和 PLAN → BUILD → CHECK 循环。当 CHECK stage 发现问题时，优先在 stage 内部自动修复；自动修复失败后回退到 BUILD stage 重试；第二次 CHECK stage 直接等待人工，不再自动修复。

#### Scenario: CHECK 内部 auto-fix 成功

- **WHEN** CHECK stage self-check round 产出 Verdict: FAIL
- **AND** auto-fix loop 在 max 2 次尝试内修复所有问题
- **THEN** review.md 更新为 Verdict: PASS
- **AND** Issue 进入 awaiting-user 等待确认

#### Scenario: CHECK 内部 auto-fix 失败，回退到 BUILD

- **WHEN** CHECK stage auto-fix loop 耗尽 2 次尝试仍未修复
- **THEN** Issue stage 从 `check` 变为 `build`
- **AND** checkpoint 标记 `no-auto-fix`
- **AND** BUILD stage 基于审查报告的 Fix Suggestions 执行修复

#### Scenario: CHECK 第二次进入跳过 auto-fix

- **WHEN** CHECK stage 带有 `no-auto-fix` checkpoint 进入
- **AND** self-check round 产出 Verdict: FAIL
- **THEN** 跳过 auto-fix loop，直接进入 awaiting-user
- **AND** 等待人工处理

#### Scenario: CHECK 通过完成 Issue

- **WHEN** CHECK stage 审查通过
- **THEN** Issue stage 从 `check` 变为 `done`
- **AND** Issue status 保持 `active`
