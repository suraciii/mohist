# PRD.json Format Specification

This document defines the format of `prd.json` (Product Requirements Document) used in the OpenSpec workflow.

## Overview

The `prd.json` file contains the task breakdown for implementing a Change. It is generated during the **plan** stage after successful self-review.

## File Location

```
.mohist-specs/changes/{change-name}/prd.json
```

## Schema

```json
{
  "version": "1.0",
  "change_id": "42-add-user-authentication",
  "issue_reference": "#42",
  "generated_at": "2024-01-15T10:00:00Z",
  "tasks": [
    {
      "id": "T-001",
      "order": 1,
      "capability": "user-authentication",
      "requirement_ref": "REQ-001",
      "title": "实现登录页面 UI",
      "description": "创建包含邮箱/密码输入框的登录表单，包含前端验证",
      "acceptance_criteria": [
        "页面路径为 /login",
        "邮箱输入框有格式验证（正则）",
        "密码输入框有最小长度验证（8位）",
        "显示友好的错误提示",
        "响应式设计支持移动端"
      ],
      "dependencies": [],
      "estimated_effort": "small",
      "spec_file": "specs/user-authentication.md"
    },
    {
      "id": "T-002",
      "order": 2,
      "capability": "user-authentication",
      "requirement_ref": "REQ-002",
      "title": "实现登录 API",
      "description": "创建 POST /api/auth/login 端点，验证用户凭证并返回 JWT",
      "acceptance_criteria": [
        "API 路径为 POST /api/auth/login",
        "验证邮箱和密码",
        "验证失败返回 401 和错误信息",
        "验证成功返回 JWT token",
        "Token 包含用户 ID 和角色",
        "Token 有效期为 24 小时"
      ],
      "dependencies": ["T-001"],
      "estimated_effort": "medium",
      "spec_file": "specs/user-authentication.md"
    },
    {
      "id": "T-003",
      "order": 3,
      "capability": "user-authentication",
      "requirement_ref": "REQ-003",
      "title": "集成前端与 API",
      "description": "连接登录页面与后端 API，处理成功/失败状态",
      "acceptance_criteria": [
        "点击登录按钮调用 API",
        "成功时存储 JWT 并跳转到首页",
        "失败时显示错误信息",
        "显示加载状态防止重复提交"
      ],
      "dependencies": ["T-001", "T-002"],
      "estimated_effort": "small",
      "spec_file": "specs/user-authentication.md"
    }
  ],
  "metadata": {
    "total_tasks": 3,
    "capabilities_covered": ["user-authentication"],
    "session_memory_path": "./session-memories/",
    "task_status_path": "./task-status.json"
  }
}
```

## Field Definitions

### Root Level

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `version` | string | Yes | PRD format version (e.g., "1.0") |
| `change_id` | string | Yes | Unique identifier for the Change |
| `issue_reference` | string | Yes | Reference to the original issue (e.g., "#42") |
| `generated_at` | string (ISO8601) | Yes | Timestamp when PRD was generated |
| `tasks` | array | Yes | List of tasks to execute |
| `metadata` | object | Yes | Additional metadata |

### Task Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `id` | string | Yes | Unique task ID (e.g., "T-001") |
| `order` | number | Yes | Execution order (1-indexed) |
| `capability` | string | Yes | Associated capability name |
| `requirement_ref` | string | Yes | Reference to requirement ID in spec |
| `title` | string | Yes | Short task title |
| `description` | string | Yes | Detailed task description |
| `acceptance_criteria` | array of strings | Yes | List of AC that must be met |
| `dependencies` | array of strings | Yes | IDs of tasks that must complete first |
| `estimated_effort` | string | Yes | "small", "medium", or "large" |
| `spec_file` | string | Yes | Path to related spec file |

### Metadata Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `total_tasks` | number | Yes | Total number of tasks |
| `capabilities_covered` | array of strings | Yes | List of capabilities |
| `session_memory_path` | string | Yes | Relative path to session memories |
| `task_status_path` | string | Yes | Relative path to task status file |

## Task Status File

The `task-status.json` file tracks execution progress:

```json
{
  "current_task_index": 2,
  "total_tasks": 3,
  "tasks": [
    {
      "id": "T-001",
      "status": "completed",
      "attempts": 1,
      "completed_at": "2024-01-15T10:15:00Z"
    },
    {
      "id": "T-002",
      "status": "in_progress",
      "attempts": 1,
      "started_at": "2024-01-15T10:16:00Z"
    },
    {
      "id": "T-003",
      "status": "pending",
      "attempts": 0
    }
  ]
}
```

### Task Status Values

- `pending` - Not started yet
- `in_progress` - Currently being executed
- `completed` - Successfully finished
- `failed` - Failed after max retries
- `skipped` - Skipped by user choice

## Usage in Ralph Loop

The main-agent uses these files during build stage:

1. **Read prd.json** - Get task list and dependencies
2. **Read task-status.json** - Find current task index
3. **For current task**:
   - Load related spec file
   - Load previous session memories
   - Assemble prompt context
   - Execute via spawn_coder
4. **Update task-status.json** - Record result
5. **Store learning** - Save insights to session-memories/
6. **Repeat** until all tasks complete

## Example: Minimal PRD

```json
{
  "version": "1.0",
  "change_id": "1-fix-typo",
  "issue_reference": "#1",
  "generated_at": "2024-01-15T10:00:00Z",
  "tasks": [
    {
      "id": "T-001",
      "order": 1,
      "capability": "documentation",
      "requirement_ref": "REQ-001",
      "title": "Fix README typo",
      "description": "Fix the typo 'recieve' -> 'receive' in README.md",
      "acceptance_criteria": [
        "README.md no longer contains 'recieve'",
        "All instances are corrected to 'receive'"
      ],
      "dependencies": [],
      "estimated_effort": "small",
      "spec_file": "specs/documentation.md"
    }
  ],
  "metadata": {
    "total_tasks": 1,
    "capabilities_covered": ["documentation"],
    "session_memory_path": "./session-memories/",
    "task_status_path": "./task-status.json"
  }
}
```
