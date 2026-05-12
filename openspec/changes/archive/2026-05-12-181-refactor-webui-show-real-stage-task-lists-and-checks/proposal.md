## Why

Issue Detail currently exposes `stage-state` rows as if they were the stage's real task list, so users can see placeholder tasks that never actually run mixed with real workflow tasks that already completed. This became urgent in #180 because Plan can look half-pending even when all real artifacts are done and the stage is only waiting for approval, which breaks the page's role as a trustworthy workflow cockpit.

## What Changes

- Redefine the user-visible stage task list so every item represents a real workflow execution unit for that stage, not a static placeholder or internal implementation artifact.
- Update stage-state projection and `GET /api/issues/:number/stage-state` semantics to return one ordered task list and one separate check list per stage.
- Remove non-executable legacy placeholder tasks from Plan stage presentation and instead surface the real Plan artifact tasks: proposal, specs, design, implementation tasks, and self-review.
- Make Build stage task presentation follow the real `tasks.json` task list, with repair tasks added to the same list only when they actually occur.
- Make Check and Integrate stage presentation follow real review, repair, re-review, rebase, conflict-resolution, merge, and final-health tasks instead of static templates.
- Preserve why a task appeared by attaching reason/caused-by metadata to the task entry rather than splitting user-visible tasks into planned versus dynamic categories.
- Keep checks as a distinct read-only list and keep task/check evidence such as attempts, session details, stdout/stderr excerpts, transcripts, artifacts, and results in task or check detail views rather than promoting them to top-level tasks.
- Align `PipelineView` and `TaskProgressPanel` on the same stage task list so the two Issue Detail surfaces cannot disagree about current workflow progress.
- Add regression coverage for mixed placeholder-plus-real Plan data and for runtime-added repair tasks with explanatory metadata.

## Capabilities

### New Capabilities

<!-- Leave empty if none. -->

### Modified Capabilities

- `pipeline-model`
- `http-api`
- `web-ui`

## Impact

- Backend read model: `packages/cli/src/services/stage-state-service.ts` will need to stop treating seeded placeholder rows as user-visible truth and instead project a stage's real task list, check list, approval state, and task origin metadata.
- Workflow/task producers: Plan, Build, Check, Integrate, repair, and rebase paths that write stage task state will need to provide enough metadata for the stage-state projection to explain runtime-added tasks without inventing a second task model.
- HTTP API: `packages/cli/src/api/issues.ts` `GET /api/issues/:number/stage-state` response semantics will change from exposing raw stage-state rows to exposing the canonical user-visible workflow stage view.
- Frontend: `packages/cli/web/src/components/PipelineView.tsx`, `TaskProgressPanel.tsx`, related hooks, and shared types will need to consume the refined stage task/check model and render task reasons/evidence without mixing checks or session activity into the task list.
- Tests: existing stage-state consistency coverage will expand to cover Plan placeholder filtering, real artifact task rendering, runtime repair/rebase task visibility, and consistency between Issue Detail task surfaces.
- Dependencies and external integrations: no new external dependencies are required; this is a semantic and rendering correction across existing workflow, API, and Web UI systems.
