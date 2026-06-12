## Context

Mohist workflow tasks already produce durable planning, review, and integration files, but the product currently exposes many of those outputs as mutable workspace paths. That works for the latest content, but it loses user-visible history when a later task run rewrites the same path. Check repair loops make the gap obvious: `ai-review.1` may write a failing `review.md`, `fix-review-findings` may repair the code, and `ai-review.2` may overwrite `review.md` with a passing report. Operators need both reports to understand why the workflow changed course.

The current backend is ASP.NET Core + Orleans, with workflow execution coordinated by `WorkflowGrain` and runner work performed from a separate TypeScript runner process. The runner owns access to the workspace files, so artifact capture must happen there. The server owns workflow state, durable persistence, event emission, API shape, and issue-scoped query behavior. Existing `with.expect.files` and `with.expect.markers` are action-level completion contracts for `mohist/acp-agent`; they must remain separate from workflow artifact capture metadata.

The design introduces `WorkflowArtifact` as an immutable, task-produced workflow history record. The term `Snapshot` is intentionally avoided in model, table, DTO, route, and UI naming.

## Goals / Non-Goals

**Goals:**

- Add task-level `artifacts.files` parsing to workflow definitions without merging it into action `with` input.
- Preserve `with.expect.files` and `with.expect.markers` for `mohist/acp-agent`, including marker expectations with `path` and `oneOf` accepted values.
- Capture declared and dynamic runner-produced artifacts through a two-step pending upload and result-binding flow.
- Persist immutable `WorkflowArtifact` records with filesystem-backed content outside `WorkflowRun` JSON.
- Expose latest-by-path, path history, task-run artifact, directory browsing, and immutable content API views.
- Show latest artifacts and per-task produced artifacts on the issue page, including repeated `review.md` versions from check loops.

**Non-Goals:**

- Do not change check pass/fail semantics; check actions still decide workflow verdicts.
- Do not infer recorded artifacts from `with.expect.files`.
- Do not store artifact content inside `WorkflowRun` domain JSON.
- Do not introduce `LatestWorkflowArtifact` as a domain model; latest is a query projection.
- Do not make recorded artifacts inputs to later workflow tasks in this version.
- Do not replace existing current-worktree file content lookup from issue #8.

## Decisions

### 1. Model `WorkflowArtifact` as a small domain fact plus infrastructure metadata in rows

`WorkflowArtifact` will represent the business fact that one workflow task run recorded one immutable output path at a timestamp. Its core domain fields are `artifactId`, `workflowRunId`, `taskRunId`, `path`, and `recordedAt`. The producer identity is `workflowRunId + taskRunId`; the stable business identity under that producer is `path`.

Persistence rows and DTOs may add `issueId` or issue number linkage for queries, `artifactStoragePath`, `contentType`, `contentHash`, `size`, `kind` (`file` or `directory`), and display metadata. These are not part of the core domain language because they describe storage, transport, or presentation.

Alternative considered: embed artifact metadata directly in `WorkflowRun.State` beside task runs. This would make timeline rendering simple, but it couples artifact storage details to workflow domain JSON and risks large state writes. The chosen design keeps content and storage details in artifact tables while allowing workflow history DTOs to join produced artifact summaries onto task runs.

### 2. Use pending uploads before visible artifact binding

The runner will upload artifact content to `POST /api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads` before reporting the task result. The server will store each upload as a hidden pending artifact upload and return an upload id. The result report will include `artifactUploadIds`, and `WorkflowGrain.ReportResultAsync` will validate and bind those uploads into visible `WorkflowArtifact` records before the task result becomes visible as completed.

This preserves the existing workflow execution boundary: the result report is the point where the server has workflow run id, work id, runner lease context, and the active task run. It also supports failed task runs with diagnostic artifacts when uploads are present.

Alternative considered: upload and bind directly in one runner request after task completion. That is simpler, but it makes it harder to preserve atomicity with result binding and harder to reject foreign upload ids consistently. Pending uploads give the server a clear hidden state and a single binding transaction.

### 3. Make upload idempotency keyed by work context and source path

Pending uploads will be idempotent by `workflowRunId + workId + taskRunId + path`. The server derives `taskRunId` from the active work context instead of accepting attempt numbers from the runner. A retry with the same content hash returns the existing pending upload. A retry with a different content hash returns conflict and does not replace content.

Alternative considered: key idempotency only by upload id generated client-side. That would avoid path collision logic, but it pushes identity responsibility to the runner and makes duplicate retries harder to reason about. Server-derived keys keep binding aligned with workflow lease state.

### 4. Store artifact content in generated filesystem locations

Artifact content will live under the configured Mohist data root, for example `~/.mohist/artifacts/workflows/{workflowRunId}/tasks/{taskRunId}/artifacts/{artifactId}/`. File artifacts store `metadata.json` and `content`. Directory artifacts store `metadata.json` and a `files/` tree. The original artifact `path` remains display and query metadata only; it is never used directly as a storage path segment.

Alternative considered: store content as blobs in SQLite. That would simplify local backup consistency, but it would make large files and directory artifacts heavier, increase DB churn, and conflict with the requirement to keep content in normal filesystem storage.

### 5. Treat directory artifacts as one collection with safe traversal

The runner will normalize declared artifact paths relative to its workspace, reject paths that escape the workspace, and capture directories without following symlinks by default. Directory capture will enforce file count and total size limits. The upload format should preserve directory content as a single artifact collection, either by multipart parts carrying relative file names or by a generated archive/internal manifest, while keeping top-level API and UI responses as one directory artifact.

Alternative considered: upload every contained file as a separate artifact. That would simplify content serving, but it floods latest and task views and violates the product shape. A collection artifact preserves the user's mental model while still allowing contained file browsing.

### 6. Keep `artifacts.files` separate from `with.expect`

Workflow YAML parsing will preserve task-level `artifacts.files` as workflow-owned metadata available to the runner. It will not merge the declaration into the action input payload. `with.expect.files` and `with.expect.markers` remain private action input for `mohist/acp-agent`, and no `WorkflowArtifact` is inferred from them.

`with.expect.markers` will support expectations with `path` and either a single accepted marker or `oneOf` accepted marker values. `mohist/acp-agent` may complete on `<promise>PASS</promise>` or `<promise>FAIL</promise>`; later checks remain responsible for verdict semantics.

Alternative considered: reuse `with.expect.files` as the artifact declaration surface. That is backward-looking and ambiguous because some expected files are only completion checks, while some artifacts may come from non-acp actions. Separate metadata makes capture explicit.

### 7. Query latest as a projection, not state

`GET /issues/{number}/workflow/artifacts` without filters will resolve the issue's current or recent workflow run and return the newest bound artifact per recorded path. `path=...&history=true` returns all versions in production order. `taskRunId=...` returns artifacts from one task run. `/{artifactId}/content` validates that the artifact belongs to the requested issue workflow context and serves recorded storage content, not current workspace content.

Alternative considered: maintain a `LatestWorkflowArtifact` table. That could speed reads, but it introduces another stateful model to keep consistent. The first version can derive latest from bound artifact records by path and recorded time, with indexes added for `workflowRunId`, `path`, `recordedAt`, and `taskRunId`.

### 8. Attach artifact summaries to workflow task history DTOs

The server will keep `WorkflowRun` content-free, but workflow history/read DTOs will include artifact summaries for each task run by joining artifact rows on `workflowRunId + taskRunId`. The web workflow mapper should stop returning empty task `artifacts` arrays when records exist.

Alternative considered: require the web UI to make one artifact query per task row. That keeps workflow DTOs smaller but creates noisy request patterns and risks flicker in the primary timeline. Joining summaries server-side makes task artifacts part of the workflow history surface without embedding content in domain state.

### 9. Emit `WorkflowArtifactRecorded` only after successful binding

The server will emit `WorkflowArtifactRecorded` after a pending upload becomes a visible `WorkflowArtifact`. Missing declared artifacts are not domain events; they fail the task through normal failure handling. Binding should be all-or-nothing for the report so users never see a partial artifact set from a rejected result.

Alternative considered: emit `WorkflowArtifactMissing` for missing declarations. That adds a separate failure vocabulary for a normal task failure case and would complicate workflow semantics. The chosen behavior keeps failure handling consistent.

## Risks / Trade-offs

- [Risk] Runner uploads succeed but result reporting never arrives -> Mitigation: keep uploads hidden as pending records and add TTL cleanup for unbound uploads and storage directories.
- [Risk] Binding artifacts before completion changes task failure behavior -> Mitigation: bind within `ReportResultAsync` before exposing completion, and return structured binding failures that follow existing task failure paths.
- [Risk] Directory artifacts can be large or unsafe -> Mitigation: normalize workspace-relative paths, reject escaping paths, avoid symlink traversal, and enforce file count plus total size limits.
- [Risk] Latest grouping by path can be ambiguous across workflow runs -> Mitigation: always scope latest/history/task queries to the issue's current or recent workflow run resolved by the issue workflow relation.
- [Risk] Artifact rows and filesystem content can drift -> Mitigation: write content into generated storage first, persist rows transactionally around binding metadata, and make content reads fail clearly if storage is missing.
- [Risk] UI may confuse recorded content with current workspace files -> Mitigation: label views as artifacts, open by artifact id, and distinguish recorded artifact content from current file review surfaces.

## Migration Plan

1. Add workflow definition parsing for task-level `artifacts.files` and update default workflow YAML to declare proposal, design, tasks, review, self-review, and spec directory outputs where appropriate.
2. Extend `mohist/acp-agent` expectation parsing to support marker `path` plus `oneOf` while preserving existing `with.expect.files` and marker behavior.
3. Add EF Core rows and migrations for pending artifact uploads and bound workflow artifacts, including indexes for latest, history, task-run filtering, and idempotent pending upload keys.
4. Add filesystem artifact storage services for file and directory content, including safe generated storage paths, metadata files, directory traversal limits, and content retrieval.
5. Add the internal runner upload endpoint and extend task result DTOs/API handlers to accept `artifactUploadIds`.
6. Update the TypeScript runner to collect declared and dynamic artifacts, validate paths relative to the workspace, upload multipart artifacts before reporting results, and fail normal task execution when required declared artifacts cannot be captured or uploaded.
7. Update `WorkflowGrain.ReportResultAsync` binding to validate upload ownership, bind all uploads atomically, record `WorkflowArtifact` rows, emit `WorkflowArtifactRecorded`, and attach artifact summaries to task history read models.
8. Add issue-scoped artifact query/content endpoints and directory browsing responses.
9. Update web API clients and Issue Detail UI to render latest artifacts, per-task artifact history, immutable content opening, and directory collection browsing.
10. Add backend and web tests covering YAML parsing, acp-agent expectations, upload/binding, dynamic artifacts, latest/history/task filters, filesystem safety, directory rendering, and check-loop review preservation.

Rollback is primarily feature rollback: disable new artifact declarations in workflow YAML and stop the runner from uploading `artifactUploadIds`. Existing bound artifacts can remain read-only because they do not affect workflow execution or later task inputs. If storage or binding causes operational issues, the result report path can reject artifact uploads with structured failures or ignore artifact ids only behind a temporary compatibility flag, while preserving the existing current-worktree file views.

## Open Questions

- What exact default file count and total size limits should directory capture enforce for local development versus larger repositories?
- Should directory upload use multipart parts with relative paths or a runner-created archive plus manifest for simpler server persistence?
- What TTL should pending uploads use, and should cleanup run as a hosted service, Orleans reminder, or startup maintenance task?
- Should artifact content endpoints support raw download only, inline text rendering metadata, or both for the first UI version?
- How should dynamic action-produced artifacts be declared by actions in the runner contract beyond statically declared `artifacts.files`?
