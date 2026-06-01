## Why

Workflow tasks already declare required output files, but the issue page does not expose those requirements or provide an in-place way to inspect the current artifact content. Board cards also hide current-stage task progress, so users cannot judge whether active workflow work is just starting, blocked on internal orchestration, or nearly complete without opening each issue.

## What Changes

- Expose task required files from workflow task expectations in the issue workflow timeline and stage-state read models.
- Add a task artifact/read model for required files, including path, source, marker requirements when present, existence or availability status when known, and whether content can be fetched on demand.
- Let Issue Detail render required file entries on task rows and open their current worktree content through the existing scoped file-content API.
- Add compact current-stage task progress to issue list/board read models so board cards can show progress such as `3/7` without loading every issue timeline.
- Derive board progress server-side from user-facing current-stage task state, with failed tasks excluded from completed counts unless a later successful retry supersedes them.
- Classify tasks so user-facing work can be distinguished from orchestration/internal workflow work, and progress indicators do not count internal orchestration tasks as user task completion.
- Keep file content transient and loaded on demand; do not store artifact file contents in workflow domain state.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `workflow-run`: StageRun/TaskRun state and projections will preserve task expectation artifact metadata and task visibility/classification needed to distinguish user-facing work from orchestration work.
- `http-api`: Issue timeline, stage-state, and issue list responses will expose required file metadata and current-stage task progress derived from server-side read models.
- `web-ui`: Issue Detail will show task required files with on-demand content viewing, and board cards will render compact current-stage task progress while respecting task classification.
- `issue-review-surface`: Issue review affordances will include task-required artifact inspection from Issue Detail while keeping full changed-file review and decision surfaces separate.

## Impact

- Backend projection/read-model code for workflow timelines, stage-state, and issue list/board issue DTOs.
- Workflow task DTOs and API contracts for required file metadata, task classification, and stage progress summaries.
- Web API client types and query consumers for issue detail, workflow task rendering, file-content loading, and board card rendering.
- Issue Detail task UI, artifact/content viewer UI, and board issue card UI.
- Backend projection tests for task expectation files and progress counting, plus Web tests for required file rendering, file content loading, and board progress indicators.
