### Requirement: Definition contains workflow behavior only
A Workflow Definition document SHALL contain only `approval` and `stages` at its top level. Its stages SHALL express workflow behavior, including tasks, checks, approval requirements, locking, resources, and task recovery.

#### Scenario: Read a pure Definition document
- **WHEN** a Definition containing `approval` and `stages` is parsed and returned
- **THEN** its approval and stage behavior are preserved without adding Profile metadata or Variables

### Requirement: Definition excludes Profile metadata and unrelated top-level fields
A Definition MUST NOT accept or return `id`, `name`, `description`, `variables`, `defaults`, or top-level `artifacts`. These fields MUST NOT be represented by the Definition semantic model or emitted from a Definition document.

#### Scenario: Submit a Definition with a forbidden top-level field
- **WHEN** a direct Definition document includes `id`, `name`, `description`, `variables`, `defaults`, or top-level `artifacts`
- **THEN** the document is rejected as not being a valid Definition

### Requirement: Definition excludes embedded Variables
A Definition MUST NOT accept, return, or use Variables embedded in a stage. A task's `setVars` remains workflow behavior and writes to WorkflowRun Variables; it does not make Variables part of the Definition.

#### Scenario: Submit a stage with embedded Variables
- **WHEN** a Definition stage includes a `variables` field
- **THEN** the document is rejected as not being a valid Definition

#### Scenario: Execute a task that sets a variable
- **WHEN** a Definition task declares `setVars`
- **THEN** the task declaration is retained as workflow behavior and its resulting values are written to the WorkflowRun Variables resource
