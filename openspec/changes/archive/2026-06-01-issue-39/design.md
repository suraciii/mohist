## Context

Mohist already has workflow definition infrastructure, YAML parsing/serialization, and a persisted issue workflow profile snapshot model. It also exposes active workflow YAML for a run, but it does not let users inspect or edit the issue's own workflow profile snapshot from the issue detail page. That leaves a gap between the global default workflow profile and the actual per-issue definition that will be used when an issue starts or when later stages initialize.

This change adds an issue-scoped YAML editing flow that operates on `IssueWorkflowProfile`, not on the global default profile. The main constraint is that editing must be safe for in-flight workflows: the active `WorkflowRunProfile` should be updated for future stage initialization, while already initialized `StageRun` tasks and checks must remain untouched. The primary stakeholders are users preparing an issue before start, users adjusting future workflow behavior mid-run, and the server/web code that must preserve workflow determinism.

## Goals / Non-Goals

**Goals:**
- Expose issue-scoped workflow profile YAML on the issue detail page.
- Allow editing and saving the YAML before workflow start, and also support active-run profile synchronization when a run already exists.
- Parse submitted YAML into a normalized `WorkflowDefinition`, return clear validation errors, and persist the normalized issue snapshot.
- Ensure future uninitialized stages read the updated definition while initialized stage work is preserved.
- Return normalized YAML and metadata that lets the UI refresh its baseline safely after save.

**Non-Goals:**
- Editing the project/global default workflow profile from the issue page.
- Regenerating existing `StageRun` tasks, checks, or approvals after YAML save.
- Redesigning workflow authoring UX beyond a single YAML editor.
- Changing the core workflow stage semantics or approval model.

## Decisions

### 1. Add dedicated issue-scoped read/write endpoints

Use `GET /api/issues/{number}/workflow/profile/yaml` and `PUT /api/issues/{number}/workflow/profile/yaml` under the existing `projectId` query scoping. The GET response should return the current normalized issue snapshot YAML plus metadata such as issue number, current workflow run id if any, and a stable profile update marker or timestamp derived from the updated issue state.

Rationale: the existing active-run YAML endpoint is run-oriented and does not model the issue snapshot lifecycle. A dedicated endpoint makes the distinction between issue profile and active run explicit and lets backlog issues use the same contract.

Alternatives considered:
- Reuse `/workflow/yaml`: rejected because it is centered on active run state and would blur issue snapshot vs run profile responsibilities.
- Add YAML into the existing issue detail payload: rejected because the editor needs a focused save contract and richer validation error responses.

### 2. Treat submitted YAML as a normalized snapshot, not raw text

The server should parse the incoming YAML with the existing workflow YAML parser, validate it as a `WorkflowDefinition`, and immediately serialize back to canonical YAML for persistence/readback. Persistence remains definition-based through `IssueWorkflowProfile`, not source-text-based.

Rationale: acceptance criteria require normalized YAML in responses and independence from later global edits. Persisting the normalized definition avoids formatting drift, guarantees deterministic future reads, and matches the existing profile snapshot model.

Alternatives considered:
- Store raw YAML alongside parsed JSON: rejected because it introduces dual sources of truth and makes future initialization behavior less predictable.
- Persist only text and parse on demand: rejected because invalid persisted text would move failures from save time to runtime.

### 3. Update `IssueWorkflowProfile` first, then synchronize active `WorkflowRunProfile`

Saving should update the issue's persisted `IssueWorkflowProfile` snapshot unconditionally after successful validation. If the issue also has an active workflow run id, the server should update the persisted `WorkflowRunProfile.Definition` to the same normalized definition in the same request flow.

Rationale: the issue profile is the source of truth for that issue outside runtime, while the run profile is the runtime copy used by future stage initialization. Keeping them synchronized on save satisfies both backlog and active-run scenarios without mutating global defaults.

Alternatives considered:
- Only update the issue snapshot and wait for future runtime reload: rejected because active runs need the updated definition before the next stage initializes.
- Only update the run profile during active runs: rejected because the issue snapshot would become stale and future reads from the issue page would be inconsistent.

### 4. Preserve initialized stage work by limiting the change surface to profile definitions

The save path must not call any stage reinitialization logic. It should only replace the stored definition snapshot on `IssueWorkflowProfile` and `WorkflowRunProfile`. `InitializeStage` should continue to read from the current run profile definition when a later stage is first initialized, so only not-yet-initialized stages observe the new YAML.

Rationale: this is the simplest way to satisfy the preservation requirement. Existing `StageRun` data already carries concrete initialized tasks and checks; avoiding regeneration prevents retroactive mutation of work already dispatched or approved.

Alternatives considered:
- Diff and patch existing `StageRun` objects: rejected because it is higher risk and can break task identity, logs, approvals, and runner expectations.
- Block editing once a workflow has started: rejected because the issue explicitly requires active run profile synchronization for future stages.

### 5. Return structured validation errors that distinguish YAML syntax from workflow-shape failures

The PUT endpoint should map parser failures into a validation response shape that the web editor can render inline without discarding local content. The response should distinguish malformed YAML from semantically invalid workflow definitions and include human-readable messages.

Rationale: the UI acceptance criteria require inline validation feedback and preserving unsaved content. Distinguishing syntax errors from definition-shape errors makes the editor actionable.

Alternatives considered:
- Return generic `400 Bad Request` text: rejected because the UI would have to infer failure type from unstructured strings.
- Reuse exception messages directly without shaping: rejected because internal exception text is brittle and not a stable API.

### 6. Keep the web UI state model local to the issue detail editor

The issue detail page should fetch the YAML snapshot separately, track `serverYaml` and `draftYaml`, derive dirty state by comparison, disable or show saving state during PUT, and on success replace both values with the normalized YAML from the response. Validation errors should clear on edit and remain visible after failed saves.

Rationale: this is a minimal UI change that matches the acceptance criteria and avoids coupling the editor to the broader issue detail query payload.

Alternatives considered:
- Make the editor fully optimistic: rejected because normalized YAML comes from the server and must become the new clean baseline.
- Put editor state into global shared stores: rejected because the editing scope is limited to one issue detail page.

## Risks / Trade-offs

- [Concurrent saves or issue refresh races] -> Return refresh-safe metadata and always treat the server's normalized YAML as the new baseline after a successful save.
- [Active-run synchronization could miss a stage if initialization and save race closely] -> Apply the profile update before any explicit stage reinitialization call, and cover the next-stage-init timing with integration/spec tests.
- [Validation messages may leak parser implementation details] -> Map parser/definition exceptions into stable user-facing error categories with sanitized messages.
- [Canonical serialization may reorder fields or formatting unexpectedly for users] -> Treat normalization as intentional product behavior and keep the serializer stable so repeated saves converge.
- [Backlog and active-run code paths can diverge over time] -> Route both through one application service or shared update method that validates once and writes both issue/run snapshots consistently.

## Migration Plan

1. Add the server endpoint pair and shared application logic for parse, normalize, persist, and optional active-run sync.
2. Extend issue/workflow query models with the response DTO needed by the editor.
3. Add the issue detail page editor, save UX, dirty tracking, and inline validation rendering.
4. Add tests for backlog edit, invalid YAML rejection, active run profile sync, and next-stage initialization preserving initialized work with fake runners/external systems.
5. Deploy without data migration. Existing issues already store workflow profile snapshots; this change only adds read/write access and active-run synchronization behavior.

Rollback: revert the new UI and endpoint handlers. Persisted custom issue snapshots remain valid because they reuse the existing `IssueWorkflowProfile` storage model; rollback mainly removes the editing surface rather than requiring state repair.

## Open Questions

- Which exact metadata field should be the UI's refresh-safe token: issue `updatedAt`, profile-specific timestamp, or both?
- Should editing be allowed after workflow start in the UI immediately, or should the first version visually emphasize backlog editing while still permitting active-run saves?
- Do we want field-level validation details in the error response now, or is categorized message-level feedback sufficient for this change?
