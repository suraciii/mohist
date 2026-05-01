## Why

OpenSpec change 归档存在三个问题：归档时机过晚（merge 后才触发）、归档路径不统一（`openspec/archive/` vs `openspec/changes/archive/`、无日期前缀）、以及一个从未注册的 zombie `archive_change` tool。这导致 issue 已完成但 openspec 仍留在 `changes/` 目录，需要手动干预才能清理。

## What Changes

- 将 openspec 归档时机从 merge 完成后移至 check stage 所有 checks 通过后、用户 approve 前
- 统一归档路径为 `openspec/changes/archive/YYYY-MM-DD-<name>/`，废弃 `openspec/archive/`
- 归档目录添加日期前缀（`YYYY-MM-DD-`），同名冲突时自动添加 `-v2`, `-v3` 后缀
- 归档时不同步 delta spec 到 `openspec/specs/`（架构记忆通过 talks/ + design/ 人工维护）
- 从 issue archive 流程中移除 openspec 归档逻辑，避免重复移动
- 删除 `src/tools/archive-change.ts` 中的 `archive_change` tool（zombie code，从未在任何 agent 中注册）

## Capabilities

### New Capabilities

_None_

### Modified Capabilities

- `change-artifacts` — 归档路径格式从 `<name>` 改为 `YYYY-MM-DD-<name>`，添加同名冲突处理规则
- `workflow-definition` — check stage 归档时机从"approval 后"明确为"checks 通过后、approval 前"

## Impact

- `src/workflow/check-stage-runner.ts` — 新增归档触发逻辑
- `src/artifacts/change-artifacts-manager.ts` — 修改 archiveChange() 路径格式和冲突处理
- `src/tools/archive-change.ts` — 删除整个文件
- `src/services/issue-service.ts` — 移除 performCleanup() 中的 openspec 归档逻辑
