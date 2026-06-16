## Why

Mohist's approval gate currently treats "Send back" as a terminal rejection that fails the workflow, instead of a feedback loop that lets users request revisions. Users and agents need a structured feedback mechanism where requesting changes resumes work (not fails it), feedback is traceable in approval history, and agents receive feedback through a reliable CLI protocol rather than just inline prompt text.

## What Changes

- Rename user-facing "Send back"/"Reject" to "Request changes" (Approve + Request changes as the two approval actions)
- Introduce `ApprovalFeedback` as a first-class domain entity scoped to workflow run, stage, and approval gate
- Requesting changes creates an `ApprovalFeedback` record and resumes the stage as running work (not failed)
- Schedule an `apply-feedback` agent task when feedback is provided, with a minimal dispatch context containing feedback id, stage, summary, and a CLI read command
- Add `mo issue feedback list/show` CLI commands with stable JSON output for agent consumption
- Add a built-in `apply-feedback.prompt` that instructs agents to read feedback via CLI and apply it as required input
- Record feedback resolution in approval history, visible in the issue workflow approval surface
- After feedback is applied, rerun relevant checks before the stage requests approval again

## Capabilities

### New Capabilities

- `approval-feedback`: The `ApprovalFeedback` domain entity — creation on "Request changes", lifecycle (open/resolved), persistence scoped to workflow run/stage/gate, resolution summary, and visibility in approval history
- `approval-feedback-agent`: Agent dispatch context for feedback (`approvalFeedback` object in task input with id, stage, summary, CLI command), the built-in `apply-feedback.prompt`, and the flow from feedback creation to agent scheduling
- `approval-feedback-cli`: `mo issue feedback list <issue-number>` and `mo issue feedback show <issue-number>` commands with `--feedback <id>`, `--latest`, `--stage`, and `--output json` flags returning stable, compact JSON schemas

### Modified Capabilities

- `workflow-run`: The `stage-approval-rejection-feedback` requirement changes — requesting changes no longer fails the stage; instead it creates an `ApprovalFeedback`, resumes the stage as running, and schedules an `apply-feedback` task. Retryability semantics for approval rejection shift from "retry failed rejection" to "feedback loop iteration"
- `workflow-engine`: Approval rejection handling changes — rejected approval no longer means terminal stage failure; the engine schedules `apply-feedback` as a normal workflow task and reruns checks before re-requesting approval. The `Approval pending remains non-repairable` requirement extends to acknowledge feedback as a separate path from repair
- `http-api`: New endpoints for approval feedback CRUD (`POST /api/issues/:number/feedback`, `GET /api/issues/:number/feedback`, `GET /api/issues/:number/feedback/:id`). The approval reject endpoint changes semantics from failure to feedback creation
- `web-ui`: Approval card changes from Approve/Reject to Approve/Request changes. Approval history shows feedback-resolution trail (feedback requested, feedback task, resolution, checks rerun, next approval request)
- `cli-interface`: New `mo issue feedback` command group (list, show) with JSON output. The CLI must expose feedback for agent consumption through stable JSON schemas
- `workflow-definition`: Workflow YAML gains `approval.feedback.task` configuration defining what task to run when feedback arrives (defaults to built-in `apply-feedback`)

## Impact

- **Domain model**: New `ApprovalFeedback` entity in the data layer, persisted alongside WorkflowRun state
- **Workflow engine**: Stage runner must handle the new feedback task type and the revised approval rejection flow
- **API surface**: New feedback endpoints; existing approval endpoints change behavior
- **CLI surface**: New `mo issue feedback` commands with JSON output contract
- **Web UI**: Approval card redesign; approval history timeline must render feedback-resolution cycles
- **Agent prompts**: New built-in `apply-feedback.prompt`; Plan/Check stage dispatch must include `approvalFeedback` context when feedback is pending
- **Workflow YAML**: Default and project workflow profiles gain `approval.feedback` section
