## Review

Reviewed issue 495 against `proposal.md`, `design.md`, `tasks.json`, and `specs/workflow-task-recovery/spec.md`.

- The proposal identifies both observed sources of drift and keeps the public retry, rerun, and recovery semantics stable.
- The specification defines one retry target for status and execution, retains failed-workflow controls, and separates Server transport validation from Runner budget interpretation. Each requirement has testable scenarios.
- The design assigns retry-target selection to WorkflowRun and budget clamping to the Runner, preserves malformed continuation rejection, documents rejected alternatives, and requires no migration or protocol change.
- The task graph has two independently deliverable slices. Neither consumes the other, so both `dependsOn` arrays are correctly empty; their acceptance criteria include focused Server and Runner verification.

No material planning gaps found.

<promise>PASS</promise>
