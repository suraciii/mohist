## Context

Workflow task definitions can declare required output files through `with.expect.files`, and those files are already part of task completion semantics. Today that metadata is not available in the issue-page read model: `WorkflowProjectionService` projects task status, duration, and messages, while the Web mapper fills task artifacts with an empty list. Users therefore cannot review expected artifacts such as `proposal.md`, `design.md`, `tasks.json`, `self-review.md`, or `review.md` from the task that produced or validated them.

The system already has an issue-scoped file content path, `GET /api/issues/{number}/workflow/file-content?path=...`, backed by runner workspace access. This change should reuse that path and keep file content transient rather than persisting content in `WorkflowRun`, `TaskRun`, or issue read models.

Board cards also receive `workflowStage` and `workflowStatus`, but not stage task progress. The board cannot show whether active workflow work is just starting or nearly complete without loading each issue detail timeline. Progress must be derived server-side for `GET /api/issues`, and it must ignore orchestration/internal tasks so internal workflow steps do not inflate user-visible progress.

Stakeholders are users reviewing issue workflow progress, users approving plan/check artifacts, and frontend/backend maintainers responsible for stable API contracts and board performance.

## Goals / Non-Goals

**Goals:**

- Preserve task expected-file metadata on `TaskRun` without storing file content.
- Expose required file metadata through timeline, stage-state, and WorkflowRun-backed task read models.
- Let Issue Detail render required files on task rows and open current file content on demand through the existing scoped file-content API.
- Add a server-derived `workflowStageProgress` summary to issue list items for compact board card progress.
- Classify tasks as user-facing or orchestration/internal, and count only user-facing tasks in board progress.
- Cover backend projection/progress behavior and web rendering/file-viewing behavior with tests using fakes where filesystem access is involved.

**Non-Goals:**

- Do not change task execution semantics or `expect.files` completion rules.
- Do not persist artifact file content in workflow domain state or issue read models.
- Do not move PASS/FAIL verdict checks into task requirements.
- Do not add a full workflow timeline to board cards.
- Do not redesign active task timeline styling or queued pre-lease workflow state.
- Do not replace the dedicated Files changed review surface; task artifacts are a contextual review affordance.

## Decisions

### 1. Model required files as task metadata, not generic stored content

Introduce a small required-file read model, for example `WorkflowTaskRequiredFile`, with `path`, `source`, optional marker requirements, content availability/status when known, and a boolean or equivalent signal that content can be requested on demand. `source` is `task-expect` for entries projected from `with.expect.files`.

`TaskRun` should retain the expected-file metadata materialized from the task definition. Runtime status/output updates should not overwrite it. Projections should copy the metadata into workflow task DTOs used by timeline and stage-state APIs.

Rationale: expected files are task requirements and belong with the task read model. File content is workspace state and should remain outside workflow domain state.

Alternatives considered:

- Store file content on `TaskRun`: rejected because content is mutable workspace state, may be large, and violates the no permanent artifact content requirement.
- Infer expected files only in the Web UI from workflow YAML: rejected because the board/detail client would duplicate server behavior and lose runtime-added task context.
- Use the existing `artifacts` array only: acceptable if the contract is clearly typed for required files, but a dedicated `requiredFiles` field is preferred because these are expectations, not produced binary/content artifacts.

### 2. Load artifact content only through the scoped file-content API

Issue Detail should render required-file entries from task DTOs. When the user opens an entry, the Web UI calls the existing issue-scoped file-content endpoint with the issue number and path, then shows the returned content in an in-place panel or expandable viewer.

Rationale: this reuses the existing project/issue scoping and avoids adding another filesystem access path. It also keeps timeline and board payloads small.

Alternatives considered:

- Embed file content in timeline or stage-state responses: rejected for payload size, staleness, and persistence concerns.
- Add a new artifact-content endpoint: rejected unless the existing endpoint cannot represent unavailable/missing file states; the existing route already matches the required scope.

### 3. Derive board progress in the backend issue list projection

Add `workflowStageProgress` to issue list DTOs. The summary should include at least `stage`, `completed`, and `total`, and may include `running`, `failed`, and `currentTaskTitle`. It should be omitted or empty when the issue has no meaningful active user-task progress, such as backlog/done/cancelled states, stages with no user-facing tasks, or approval/check-only waiting states.

Rationale: the board must render many cards without making one timeline request per card. Server-side derivation also centralizes task classification and retry/failure semantics.

Alternatives considered:

- Have the Web UI compute progress from detail timeline data: rejected because it does not scale for board lists and would require hidden per-card timeline fetches.
- Show only workflow stage/status: rejected because it does not satisfy the product need for progress such as `3/7`.

### 4. Add task classification at materialization/projection boundaries

`TaskRun` should expose a classification or visibility value that identifies user-facing work versus orchestration/internal work. Default workflow tasks that correspond to user-visible plan/check/build work should be user-facing. Runtime-added orchestration, repair, retry bookkeeping, rebase, and similar internal workflow tasks should be marked orchestration/internal. Progress summaries count only user-facing tasks.

Rationale: classification is domain knowledge and should not be inferred from title strings in the Web UI. Keeping it on task state or canonical projections makes board and detail behavior consistent.

Alternatives considered:

- Infer internal tasks by naming conventions: rejected as brittle and hard to test.
- Count all current-stage tasks: rejected because orchestration tasks would inflate progress and confuse users.
- Hide internal tasks entirely in detail: not chosen for this change because the issue only requires progress to distinguish them; changing detail visibility is a broader UX decision.

### 5. Define explicit progress counting semantics

For current-stage user-facing tasks, `completed` counts successful/completed tasks only. Failed tasks are exposed separately when available and are not counted as completed unless a later successful retry or replacement supersedes the failed attempt for the same user-facing work. Running tasks may be counted separately. `currentTaskTitle` should come from the active/running user-facing task when available, otherwise the next pending user-facing task.

Rationale: explicit semantics avoid misleading fractions and give the board enough data to show compact progress without interpreting raw task arrays.

Alternatives considered:

- Count failed tasks as completed because they are finished attempts: rejected because the user-visible work is not done.
- Omit failed/running counts entirely: possible for the first implementation, but including them in the server model allows better card states without API churn.

## Risks / Trade-offs

- [Risk] Existing tasks may not have expected-file metadata if they were created before this field exists -> Mitigation: projections should tolerate missing metadata and render no required-file entries for older task state.
- [Risk] Content availability can become stale between timeline fetch and user opening a file -> Mitigation: treat availability as advisory and make the file viewer handle missing/unavailable responses from the scoped file-content endpoint.
- [Risk] Task classification defaults could misclassify dynamic tasks -> Mitigation: use conservative defaults, add projection tests for known workflow defaults and internal task sources, and prefer not counting ambiguous orchestration sources in board progress.
- [Risk] Adding fields to API DTOs may affect clients with strict schemas -> Mitigation: add fields compatibly and keep existing fields unchanged.
- [Risk] Board progress can be misleading if retry replacement relationships are not explicit enough -> Mitigation: start with completed/failed status rules for current materialized tasks and add supersession handling where retry metadata exists.
- [Risk] File viewer tests could accidentally depend on real workspace files -> Mitigation: mock API responses and use fakes for file-content access in Web and backend tests.

## Migration Plan

1. Add domain/read-model types for required files and task classification with compatible defaults for existing task state.
2. Materialize `with.expect.files` into `TaskRun` metadata and preserve it across task status/output updates.
3. Update workflow projections and DTOs for timeline and stage-state responses to include required-file metadata without file content.
4. Add backend projection tests covering expected-file projection, metadata preservation, task classification, and progress counting.
5. Extend issue list read models and API responses with `workflowStageProgress` derived from current-stage user-facing tasks.
6. Update Web API types/mappers so task required files and board progress flow into Issue Detail and board cards.
7. Add Issue Detail UI for required-file entries and an on-demand in-place content viewer backed by the scoped file-content API.
8. Add compact board card progress rendering, hidden or de-emphasized when the API reports no meaningful progress.
9. Add Web tests with mocked task metadata, fake file-content responses, and board progress cases.

Rollback is low risk because the change is additive. If issues occur, hide the Web UI affordances and stop emitting or consuming `workflowStageProgress` while leaving existing workflow execution semantics untouched. Required-file metadata can remain on task state because it does not affect execution behavior.

## Open Questions

- Should the public task DTO use `requiredFiles` only, or should existing `artifacts` also be populated for compatibility with current Web naming?
- What exact marker requirement shape is already present in `with.expect.files`, and should the API preserve it verbatim or normalize it into a typed structure?
- Which dynamic Build tasks are definitively user-facing versus orchestration/internal, and where should their classification be assigned?
- Should `contentAvailable` be computed eagerly during projection, or should the API expose only `canFetchContent` and let the viewer discover missing/unavailable states on demand?
