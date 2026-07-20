## Why

Action output is currently passed as a string and independently reparsed by `setVars`, task-output references, recovery, storage, and display code. This lets successful tasks expose no usable fields without an error, so Action output needs one structured contract from production through every consumer.

## What Changes

- Action success output becomes a JSON object or `null` throughout runner execution, runner-to-server reporting, `TaskRun` storage, and downstream consumption; no stage serializes output and later reparses it to recover structure.
- **BREAKING**: `core/process` success output changes from plain stdout text to `{ stdout, exitCode }`, so `setVars` and `${{ tasks.<id>.outputs.stdout }}` / `${{ tasks.<id>.outputs.exitCode }}` can address stable fields.
- Existing output fields and meanings for all other built-in Actions remain unchanged while their implementations return structured objects instead of serialized JSON strings.
- A successful Action result whose output is neither an object nor `null` fails the task with an actionable output-shape error instead of being wrapped, skipped, or silently treated as missing output.
- `setVars`, `${{ tasks.<id>.outputs.* }}`, and recovery `when: output.*` read the same structured output object. A missing `setVars` source path continues to fail the task and names the missing path.
- Task details continue to display successful output as structured JSON after the transport and API shapes become structured.
- Action inputs, manifest-based output declarations, and runtime output-field schema validation remain unchanged and are outside this change.

## Capabilities

- `action-output`: The end-to-end contract for Action success output: object-or-null production, reporting, persistence, task-detail presentation, `setVars` projection, `tasks.<id>.outputs.*` references, and recovery matching; includes the `{ stdout, exitCode }` contract for `core/process` and explicit task failure for invalid output shapes or missing projected fields.

## Impact

- **Runner** (`packages/runner/src/actions/`, `packages/runner/src/runtime/`, `packages/runner/src/core/types.ts`, `packages/runner/src/server/connection.ts`): Action result and work-result output types become structured; built-in Actions stop calling `JSON.stringify`; `core/process` emits named fields; recovery, dynamic artifact discovery, and `setVars` stop reparsing strings.
- **Server** (`packages/server/src/Mohist.Server/Runner/`, `packages/server/src/Mohist.Server/Workflow/`, `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`): runner report, internal report, and task status contracts carry JSON directly; `TaskRun.Output` receives the object without parse-and-wrap fallback; task-output dispatch projection consumes the stored object.
- **Web** (`packages/web/src/widgets/issue-workflow/`, `packages/web/src/entities/issue/`): task timeline/detail output types and rendering consume structured API output without JSON reparsing while preserving the existing presentation.
- **Profiles and consumers**: workflow definitions using `core/process` plain-text output must move to `output.stdout` or `tasks.<id>.outputs.stdout`; existing field paths for other built-in Actions remain valid.
- **Dependencies and persistence**: no new dependency and no output manifest/schema work. Persisted `TaskRun.Output` remains JSON-backed; no compatibility path or data migration is introduced for in-progress runs using the old runner report encoding.
