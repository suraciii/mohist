## Why

API 层（`api/*.ts`）直接调用 StateManager 做 CRUD，绕过了已有的 Service 层，导致调用路径混乱。同时 CLI 层有三个完全相同的 `apiClient()` 拷贝。这些技术债不影响功能，但会增加后续 M2 交互能力的维护成本——API handler 越来越臃肿，修改时需要在 StateManager 和 Service 之间来回跳跃。

## What Changes

- 提取 CLI 三份重复的 `apiClient()` 到公共模块 `cli/api-client.ts`
- 统一 API 层调用路径：所有 CRUD 操作通过 Service 层，不再直接调用 StateManager 的 CRUD 方法
- StateManager 保留 repo getter 和 config 管理职责，移除与 Service 重叠的 CRUD 方法
- `api/issues.ts` 中 start/approve 端点的 CRUD 调用改为 Service 层（编排逻辑暂不移动）
- 从 backlog 移除已解决的 B-005（git merge 泄漏）

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `http-api`: API 层统一通过 Service 层操作，不再直接调用 StateManager CRUD。StateManager 缩减为 repo 工厂 + config 管理。
- `cli-interface`: CLI 命令共享 `apiClient()` 实现，消除三份重复代码。

## Impact

- `cli/commands/issue.ts`: 移除本地 apiClient，改为 import 公共模块
- `cli/commands/quick.ts`: 同上
- `cli/commands/project.ts`: 同上
- `api/issues.ts`: stateManager CRUD 调用改为 issueService；start/approve 的编排逻辑下沉
- `api/projects.ts`: stateManager CRUD 调用改为 projectService
- `api/labels.ts`: 保持 stateManager（当前无 LabelService，且逻辑简单不值得新建）
- `api/status.ts`: 保持 stateManager（跨域聚合查询，合理使用）
- `server/state-manager.ts`: 移除与 Service 重叠的 CRUD 方法，保留 repo getter 和 config 管理
- `services/issue-service.ts`: 增加 createComment、getCommentsByIssue 等方法
- `services/project-service.ts`: 确认已存在，可能需要补齐方法
