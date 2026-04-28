# Tasks Artifact

Create the tasks.json file that defines implementation tasks for autonomous execution.

## Guidelines

- Each task should be completable in ONE agent iteration
- Tasks are smallest independently testable units that deliver value
- Order tasks by priority (lower number = higher priority)
- Earlier tasks MUST NOT depend on later tasks (dependencies must be on lower-numbered tasks)
- ALL tasks must have verifiable acceptance criteria that prove the capability works

## Task Structure

```json
{
  "id": "T-001",
  "title": "Short task title",
  "spec": "specs/<capability>/spec.md#<REQ-ID>",
  "description": "What to implement (2-3 sentences)",
  "acceptanceCriteria": ["verifiable criterion", "Typecheck passes"],
  "priority": 1,
  "passes": false,
  "notes": ""
}
```

## Fields

- `id`: Task identifier (T-001, T-002, etc.)
- `title`: Short task title
- `spec`: Optional reference to a spec requirement (e.g., `specs/auth/spec.md#REQ-001`)
- `description`: What to implement (2-3 sentences)
- `acceptanceCriteria`: Array of verification criteria
- `priority`: Execution order (1, 2, 3, ...)
- `passes`: Boolean indicating completion (starts as `false`)
- `notes`: Optional implementation notes

## Mohist-Specific Fields

In addition to the standard fields above, mohist tasks include:

- `mode`: `"AFK"` or `"HITL"`
  - `AFK`: Fully autonomous — the agent completes without human interaction
  - `HITL`: Human-in-the-loop — the agent may need human input during execution
  - Default to `AFK` unless the task inherently requires human judgment

- `type`: Task type, one of:
  - `WRITE`: Create new code or features
  - `TEST`: Write or update tests
  - `MIGRATE`: Database or data migrations
  - `CONFIG`: Configuration, build tooling, CI changes
  - `REVIEW`: Code review or audit tasks

- `output`: Expected output artifact (e.g., file path or description of what the task produces)

- `dependsOn`: Array of task IDs this task depends on (must reference lower-priority tasks)

## Granularity

A well-sized task:
- Can be completed in a single agent session (typically 5-30 minutes)
- Produces a coherent, testable unit of work
- Has clear start and end conditions
- Can be verified by its acceptance criteria alone

Avoid:
- Tasks that are too large (multiple unrelated concerns)
- Tasks that are too small (single line changes that don't deliver value independently)
- Tasks with vague acceptance criteria

## Dependency Analysis

You MUST perform explicit dependency analysis when generating tasks. Follow these steps:

1. **Pairwise analysis**: After drafting the task list, examine every pair of tasks (T-A, T-B) and determine whether T-B requires output from T-A to be implemented correctly. If yes, add T-A to T-B's `dependsOn`.

2. **Input/output tracing**: For each task, identify what it consumes (imports, calls, data) and what it produces (exports, files, database state). If T-B consumes something T-A produces, T-B MUST depend on T-A.

3. **Every non-first task MUST have at least one `dependsOn` entry**: The only exception is the very first task (lowest priority number). All other tasks should depend on at least one earlier task. If you cannot find a dependency, reconsider whether the task ordering is correct.

4. **Validate the graph**: Verify that:
   - The dependency graph is a DAG (no cycles)
   - Every `dependsOn` references a task with a strictly lower `priority` number
   - Every referenced task ID exists in the task list

## Dependency Ordering

- Use `dependsOn` to declare explicit dependencies between tasks
- The dependency graph MUST be a DAG (no cycles)
- Every `dependsOn` MUST reference a task with a strictly lower priority number (no forward dependencies)
- Tasks without dependencies should have `dependsOn: []`
- Prefer linear or tree-shaped dependency graphs over diamond patterns

## Acceptance Criteria Semantics

- With `spec`: Scenarios from the referenced requirement are primary acceptance criteria. `acceptanceCriteria` serves as additional/supplementary checks (e.g., "Tests pass", "Typecheck passes").
- Without `spec`: `acceptanceCriteria` represents ALL verification items.
- Each criterion should verify a capability works, not just that code exists.

## Example

```json
{
  "project": "my-project",
  "description": "Add user authentication",
  "tasks": [
    {
      "id": "T-001",
      "title": "Create auth schema and database migration",
      "spec": "specs/user-auth/spec.md#REQ-001",
      "description": "Create the user table schema and write the database migration script.",
      "acceptanceCriteria": [
        "Migration runs without errors",
        "User table has required columns",
        "Typecheck passes"
      ],
      "priority": 1,
      "mode": "AFK",
      "type": "MIGRATE",
      "output": "migrations/001_create_users.sql",
      "dependsOn": [],
      "passes": false,
      "notes": ""
    },
    {
      "id": "T-002",
      "title": "Implement login endpoint",
      "spec": "specs/user-auth/spec.md#REQ-002",
      "description": "Create the POST /auth/login endpoint with JWT token generation.",
      "acceptanceCriteria": [
        "Valid credentials return 200 with token",
        "Invalid credentials return 401",
        "Typecheck passes"
      ],
      "priority": 2,
      "mode": "AFK",
      "type": "WRITE",
      "output": "src/routes/auth.ts",
      "dependsOn": ["T-001"],
      "passes": false,
      "notes": ""
    }
  ]
}
```

Extract acceptance criteria from `specs/*/spec.md` SHALL requirements.

## Example: Multi-Level Dependency Chain

```json
{
  "project": "task-tracker",
  "description": "Add task dependency validation",
  "tasks": [
    {
      "id": "T-001",
      "title": "Add validateTaskDependencies function",
      "description": "Implement a pure function that validates the task dependency graph: reference existence, DAG check, and forward-dependency enforcement.",
      "acceptanceCriteria": [
        "Valid graph passes with no errors",
        "Unknown task ID in dependsOn fails validation",
        "Circular dependency is detected",
        "Forward dependency (higher-order) is rejected",
        "Typecheck passes"
      ],
      "priority": 1,
      "mode": "AFK",
      "type": "WRITE",
      "output": "src/services/task-validator.ts",
      "dependsOn": [],
      "passes": false,
      "notes": ""
    },
    {
      "id": "T-002",
      "title": "Add unit tests for dependency validation",
      "description": "Write tests covering all validation scenarios: valid graph, unknown references, cycles, forward dependencies, and empty dependsOn.",
      "acceptanceCriteria": [
        "All validation scenarios have corresponding test cases",
        "Tests pass",
        "Typecheck passes"
      ],
      "priority": 2,
      "mode": "AFK",
      "type": "TEST",
      "output": "tests/services/task-validator.test.ts",
      "dependsOn": ["T-001"],
      "passes": false,
      "notes": ""
    },
    {
      "id": "T-003",
      "title": "Integrate validation into task loading",
      "description": "Call validateTaskDependencies in the task loading path and return a failed result with descriptive errors on validation failure.",
      "acceptanceCriteria": [
        "Invalid tasks.json causes immediate failure with error details",
        "Valid tasks.json loads and executes normally",
        "Typecheck passes"
      ],
      "priority": 3,
      "mode": "AFK",
      "type": "WRITE",
      "output": "src/openspec/ralph-executor.ts",
      "dependsOn": ["T-001", "T-002"],
      "passes": false,
      "notes": ""
    }
  ]
}
```

Note how T-003 depends on both T-001 (the function it integrates) and T-002 (the tests that verify the function). This diamond pattern is correct when a task genuinely needs multiple earlier outputs.

## Output

Write the file to `{changeDir}/tasks.json`.
