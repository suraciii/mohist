## Why

Mohist's real issue pipeline already starts at `backlog` and runs `plan -> build -> check -> integrate -> done`, but the codebase still carries deprecated `draft` and `explore` stage values in core enums, ordering, transitions, defaults, and UI helpers. This mismatch now blocks reliable stage-dependent work such as rewind/recovery logic in #176 and keeps misleading both agents and users about which stages actually participate in the pipeline.

## What Changes

- Remove deprecated `draft` and `explore` values from the shared Stage model used by the backend and frontend pipeline state.
- Redefine the canonical stage order as `backlog -> plan -> build -> check -> integrate -> done` so stage comparison, rewind, recovery, and resume logic operate on the same user-visible pipeline.
- Clean up stage transition rules so only real pipeline transitions remain, while preserving the existing recovery loop semantics that still need `check -> build` and, if required by current recovery behavior, `integrate -> build`.
- Update the built-in default workflow configuration to stop declaring `explore` as a workflow stage and align the default `workflow.yaml` stages with the pipeline that runners actually execute.
- Remove pipeline-specific conditionals, API checks, UI stage lists, and tests that still treat `draft` or `explore` as valid pipeline stages.
- Keep Explore mode, Explore sessions, Explore APIs, and Explore UI intact as pipeline-external capabilities rather than expressing them through `Stage.Explore`.
- **BREAKING**: Any internal code, tests, or integrations that still read or write `draft` or `explore` as legal pipeline stage enum values must switch to the backlog-first pipeline model.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `pipeline-model`
- `workflow-definition`
- `workflow-config`
- `web-ui`

## Impact

- **Shared stage model**: `packages/cli/src/types/index.ts` and any mirrored frontend stage types must remove `Draft` and `Explore`, add `Backlog` to the canonical order, and ensure stage validation only accepts the real pipeline stages.
- **Workflow engine and services**: stage legality checks in workflow orchestration, recovery, approval, start/resume, merge/finalization, and lifecycle helpers must stop branching on `Stage.Draft` or `Stage.Explore` and use backlog-first semantics instead.
- **Default workflow configuration**: `packages/cli/src/workflow/workflow-loader.ts` and related workflow-definition behavior must no longer advertise `explore` as a runnable workflow stage when no such stage runner exists.
- **HTTP/API behavior**: issue startability, approval routing, recovery guards, and any response payloads that expose stage values must align with `backlog -> plan -> build -> check -> integrate -> done`.
- **Web UI**: pipeline views, issue cards, session timelines, merge-state helpers, and other frontend stage-order displays must stop rendering `draft`/`explore` as pipeline stages while continuing to preserve dedicated Explore surfaces.
- **Tests and fixtures**: backend and frontend tests that currently assert draft/explore pipeline behavior or old stage ordering must be updated to the backlog-first model.
