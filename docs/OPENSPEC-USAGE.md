# OpenSpec Workflow Usage Guide

This guide explains how to use Mohist's OpenSpec workflow for structured issue handling with AI agents.

## Overview

The OpenSpec workflow introduces structured **Change** artifacts and **Ralph-style task execution** to Mohist:

```
plan → review → build → check → done
```

- **plan**: Generate Change artifacts (proposal, design, specs, prd.json) with self-review
- **review**: Human review and approval gate
- **build**: Ralph-style task loop execution
- **check**: Auto tests + human acceptance + Change archival

## Key Concepts

### Change

A **Change** is a structured artifact directory containing:

```
openspec/changes/{issue-number}-{slug}/
├── proposal.md      # Why this change, what problem it solves
├── design.md        # Technical design and decisions
├── specs/           # Detailed specifications per capability
│   ├── capability-a/spec.md
│   └── capability-b/spec.md
├── prd.json         # Task list (Ralph-style)
├── task-status.json # Execution state tracking
└── session-memories/ # Learnings from task execution
    └── T-001.json
```

### Ralph Loop

Ralph-style execution iterates through tasks in `prd.json`:

1. Select next pending task by order
2. Assemble full context (proposal + design + spec + learnings)
3. Execute with AI agent
4. Verify acceptance criteria
5. Store learning
6. Repeat until all tasks done

## Commands

### `mo propose <issue-number>`

Creates a new Change for an issue and starts the plan stage.

```bash
# Create Change for issue #42
mo propose 42

# Force recreate (overwrites existing)
mo propose 42 --force
```

The command:
1. Creates `openspec/changes/{issue-number}-{slug}/`
2. Launches AI agent to explore codebase
3. Agent generates proposal, design, specs
4. Agent performs self-review (up to 3 iterations)
5. If self-review passes, generates `prd.json`

### `mo issue resume <id>`

Resumes an issue from where it left off.

```bash
# Resume issue #42
mo issue resume 42

# Skip plan stage and go directly to review
# (after manual fixes to plan artifacts)
mo issue resume 42 --skip-to-review
```

### Workflow Stages

#### Plan Stage

Agent explores the issue and codebase, then generates:

- **proposal.md**: Motivation, goals, non-goals
- **design.md**: Architecture decisions, trade-offs
- **specs/**: Detailed requirements per capability
- **prd.json**: Task list derived from specs

Self-review happens within this stage (max 3 iterations).

#### Review Stage

Human reviews the Change artifacts:

1. Read proposal, design, and specs
2. Edit directly if needed
3. Approve with `mo issue approve 42` to proceed to build

#### Build Stage

Ralph loop executes tasks from `prd.json`:

- Tasks run sequentially by `order` field
- Each task gets full context (proposal, design, spec, learnings)
- Failures are analyzed and retried with failure context
- Task status tracked in `task-status.json`

#### Check Stage

1. **Auto tests**: Runs `npm test` and `npm run lint`
2. **Human acceptance**: Review implementation
3. **Archival**: Change moved to `openspec/changes/archive/`

## Example Workflow

### 1. Start Server

```bash
mo server start
```

### 2. Create Change

```bash
mo propose 42
```

Agent explores the issue and generates artifacts.

### 3. Review Artifacts

```bash
# Check generated artifacts
cat openspec/changes/42-my-issue/proposal.md
cat openspec/changes/42-my-issue/prd.json

# If satisfied, approve
mo issue approve 42
```

### 4. Build Executes

Agent runs Ralph loop, executing each task in `prd.json`.

### 5. Check and Accept

```bash
# Tests run automatically
# Review results
mo issue show 42

# If implementation is correct, approve
mo issue approve 42
```

Change is archived automatically.

## Session Memories

Learnings from task execution are stored in:

```
openspec/changes/{change}/session-memories/{task-id}.json
```

Each file contains:

```json
{
  "task_id": "T-001",
  "timestamp": "2024-01-15T10:30:00Z",
  "insights": ["Constraint discovered: API rate limit"],
  "adjustments": ["Task T-002 should handle retries"],
  "success": true,
  "execution_summary": "Implemented auth endpoint"
}
```

These insights are passed to subsequent tasks.

## Task Status

Track execution state:

```bash
cat openspec/changes/42-my-issue/task-status.json
```

```json
{
  "current_task_index": 2,
  "tasks": [
    {"id": "T-001", "status": "completed", "attempts": 1},
    {"id": "T-002", "status": "completed", "attempts": 1},
    {"id": "T-003", "status": "in_progress", "attempts": 1}
  ]
}
```

## Recovery Scenarios

### Build Fails on Task T-003

1. Agent retries with failure context (up to 2 more times)
2. If still fails, pauses with `ask_user`
3. User fixes issues manually
4. User runs `mo issue resume 42 --skip-to-review`
5. Build resumes from T-003

### Plan Self-Review Fails

1. After 3 iterations without passing, plan stage fails
2. User manually edits artifacts
3. User runs `mo issue resume 42 --skip-to-review` to proceed

## Configuration

`.mohist/config.yaml`:

```yaml
specs:
  location: "project"      # "project" or ".mohist"
  project_path: "openspec"
  git_track: true
```

## File Locations

| Path | Purpose |
|------|---------|
| `openspec/changes/` | Active changes |
| `openspec/changes/archive/` | Completed changes |
| `.mohist/mohist.db` | SQLite database |
| `.mohist/logs/` | Server logs |

## Backward Compatibility

Issues without `prd.json` use traditional workflow:

```
draft → plan → build → check → done
```

OpenSpec workflow is opt-in via file existence (`prd.json`).