## Context

Mohist's workflow approval gate (`plan` and `check` stages) currently offers two actions: **Approve** (advances the stage) and **Reject** (marks stage/workflow as `Failed` with `FailureReason.ApprovalRejected`). Rejection is terminal: the workflow stops, and the user must `Rerun` the stage, which creates a fresh `StageRun` with `Attempt+1`, losing the connection between the feedback and the work that addresses it.

The system already has infrastructure for:
- **Runtime task scheduling**: `AddRuntimeTask()` / `InsertRuntimeTasksAfter()` in `WorkflowRun.Stage.cs` can append tasks into any running stage
- **Check repair loops**: Failed checks schedule `repairTask` + optional `verifyTask`, re-run checks, and only request approval when all pass
- **Template variable expansion**: `PromptTemplateEngine` resolves `${{ }}` expressions from a 3-layer `VariableBundle` (template → project → issue → dispatch injection)
- **Built-in prompts**: `.prompt` files under `Prompts/builtins/` with YAML frontmatter, loaded by `FilePromptLoader`
- **Web UI approval card**: `InlineApproval` in `WorkflowView.tsx` with Approve/Send back buttons and `ReviewSummary`
- **CLI**: `mo issue approve` / `mo issue reject --reason` via `MohistCliCommands.Issue.cs`

This design replaces the terminal Reject with a feedback loop: "Request changes" creates an `ApprovalFeedback` record, resumes the stage, schedules `apply-feedback` as a normal agent task, and re-enters approval after checks pass.

## Goals / Non-Goals

**Goals:**
1. Replace terminal Reject with a "Request changes" feedback loop that resumes the stage as running work
2. Introduce `ApprovalFeedback` as a first-class entity persisted within WorkflowRun state
3. Schedule `apply-feedback` as a normal agent-session workflow task with dispatch context
4. Provide `mo issue feedback list/show` CLI commands with stable JSON output
5. Add a built-in `apply-feedback.prompt` that instructs agents to read feedback via CLI
6. Expose feedback history in the Web UI approval timeline
7. Rerun checks after feedback is applied before re-requesting approval

**Non-Goals:**
- Auto-approve policy
- Mobile push approval
- Merging feedback into the generic comment model as primary storage
- Feedback taxonomy (category, severity, source) without concrete product behavior
- General workflow YAML redesign beyond `approval.feedback`

## Decisions

### D1: Store `ApprovalFeedback` inside `WorkflowRun` state JSON

**Choice**: Add `List<ApprovalFeedback> Feedback` to `WorkflowRun` class, serialized as part of the existing JSON-in-column persistence via `IWorkflowRunStore`.

**Alternatives considered**:
- *Separate DB table with joins*: Over-engineering for feedback that is inherently scoped to a single run. Adds schema migration complexity and eventual consistency concerns between the aggregate and feedback rows.
- *New Orleans grain*: Unnecessary actor for records that are always read/written within the WorkflowGrain boundary.

**Rationale**: `WorkflowRun` is already a JSON-serialized document aggregate. Feedback is always queried in the context of a specific run and issue. Adding it to the aggregate keeps reads/writes transactional within the grain's save boundary and simplifies the API projection.

### D2: Replace `Reject()` with `RequestChanges()` on WorkflowRun aggregate

**Choice**: Add a new `RequestChanges(string body)` method to `WorkflowRun.Approval.cs` that creates `ApprovalFeedback`, schedules `apply-feedback` task via `AddRuntimeTask()`, clears approval status, and resumes the stage. Change the HTTP endpoint from `/reject` to new semantics while keeping the old endpoint route stable during migration.

**Alternatives considered**:
- *Keep Reject but add new endpoint*: Confusing — two paths that diverge in behavior but share a name.
- *Change Reject in-place*: Breaking change for any external integrations that rely on the current reject semantics.

**Rationale**: The `/reject` endpoint route stays but its behavior changes from terminal failure to feedback loop. The WorkflowGrain surface mirrors this. The old `Reject()` method on the domain model is replaced by `RequestChanges()` to make the semantic shift explicit at the aggregate level.

### D3: Schedule `apply-feedback` as a normal `AddRuntimeTask()` in the current stage

**Choice**: When `RequestChanges()` is called, append an `apply-feedback` task to the current `StageRun.Tasks` via `AddRuntimeTask(task, invalidateChecks: true)`. This follows the exact same pattern as `rebase-branch` scheduling (`WorkflowRun.Work.cs:54`) and check repair task scheduling (`WorkflowRun.Stage.cs:155`).

**Rationale**: No new dispatch paths. `NextWork()` already returns the first pending task, so `apply-feedback` will be dispatched naturally. The `invalidateChecks: true` flag ensures prior check evidence and approval status are cleared, so checks rerun after the feedback task completes.

### D4: Inject `approvalFeedback` into the VariableBundle at dispatch time

**Choice**: In `WorkflowGrain.MakeDispatchAsync()`, when dispatching an `apply-feedback` task (detected by `work.Use == "mohist/acp-agent"` and the task id matches the feedback task), inject an `approvalFeedback` object into the variables JSON before template expansion. This mirrors how `issue`, `project`, `stage`, and `openspecChangeDir` are injected.

**Alternatives considered**:
- *Pre-expand the feedback body into the prompt at dispatch time*: Would inline large feedback text into every dispatch payload, bloating the dispatch JSON.
- *Add a dedicated `ApprovalFeedback` field to `WorkDispatch`*: Over-engineering — the existing `Variables` JSON is already the injection point for template expansion.

**Rationale**: The `approvalFeedback` object (id, stage, summary, command) is small and stable. The full feedback body stays in the stored `ApprovalFeedback` record, readable via CLI. This is the same pattern used for `issue.number` and `project.id`.

### D5: Add `approval.feedback` to workflow YAML as a root-level section

**Choice**: Add an `approval` section at the workflow root level with a `feedback.task` subsection:

```yaml
approval:
  feedback:
    task:
      id: apply-feedback
      title: Apply approval feedback
      uses: mohist/acp-agent
      with:
        session: ${{ stage.name }}
        prompt: ${{ prompts.apply-feedback }}
```

Add an `ApprovalConfig` / `FeedbackTaskConfig` record to the `WorkflowDefinition` C# model.

**Alternatives considered**:
- *Per-stage feedback config*: Each stage could override feedback behavior. Unnecessary for v1 — the issue body explicitly says "Keep YAML small."
- *Hardcode the task without YAML*: Prevents custom workflows from changing the feedback agent or prompt.

**Rationale**: The YAML section is minimal (only task identity), consistent with how other tasks are defined, and doesn't define feedback schema (runtime state). Default workflows that don't specify `approval.feedback` fall back to the built-in `apply-feedback` task.

### D6: CLI commands follow the existing `mo issue` command group pattern

**Choice**: Add `mo issue feedback list <number>` and `mo issue feedback show <number>` commands to `MohistCliCommands.Issue.cs` as a new subcommand, calling `GET /api/projects/{projectRef}/issues/{number}/feedback` and `GET /api/projects/{projectRef}/issues/{number}/feedback/{feedbackId}`.

**Rationale**: Consistent with the existing CLI structure. Commands are thin clients — all logic is in the server API. JSON output follows the stable schema defined in the spec.

### D7: Web UI approval card replaces "Send back" with "Request changes"

**Choice**: Modify `InlineApproval` in `WorkflowView.tsx` to show "Approve" and "Request changes" as the two primary actions. "Request changes" opens a text input for feedback body. The approval history section renders feedback cycles from the API response.

**Rationale**: The current card already has a "Send back for fixes" action that takes a message. Renaming it to "Request changes" is a UI label change. The feedback history rendering is new but follows the existing timeline pattern used for stage transitions and check results.

## Risks / Trade-offs

**[Risk] WorkflowRun JSON size grows with multiple feedback cycles** → Mitigation: Feedback records are intentionally compact (~6 fields). A typical workflow has 1-3 feedback cycles. JSON serialization overhead is negligible compared to existing task/check state.

**[Risk] Race between concurrent approval actions** → Mitigation: The `WorkflowGrain` uses Orleans virtual actor semantics (single-threaded grain execution). Approve and RequestChanges are separate grain methods that validate the stage is awaiting approval before proceeding. No two approval actions can execute simultaneously.

**[Risk] Old reject clients calling the `/reject` endpoint after behavioral change** → Mitigation: The `/reject` route stays but now creates feedback instead of terminal failure. The CLI `mo issue reject` command is removed (replaced by the new feedback path). External integrations that previously interpreted WorkflowRun `Failed + ApprovalRejected` will instead see the stage as `Running` with a pending feedback task. This is intentional — the old behavior was the bug.

**[Risk] Feedback body could be large** → Mitigation: The dispatch context only includes a short summary. The full body is read by the agent via CLI. Stored as part of the JSON aggregate, but feedback body size is bounded by typical user input (not machine-generated).

## Migration Plan

1. **Add data model**: Add `ApprovalFeedback` record and `List<ApprovalFeedback> Feedback` to `WorkflowRun` with default empty list. Existing serialized WorkflowRun JSONs will deserialize with an empty list — backward compatible.
2. **Add `WorkflowRun.RequestChanges()` method**: New aggregate method, no existing behavior changed yet.
3. **Add YAML config**: `ApprovalConfig` to `WorkflowDefinition`. Existing YAML files that omit `approval` section get the built-in default.
4. **Create `apply-feedback.prompt`**: New built-in prompt file, no existing prompts modified.
5. **Add HTTP endpoints**: `POST /feedback`, `GET /feedback`, `GET /feedback/:id` on issue routes. Keep `/reject` route but change its behavior to call `RequestChanges()` instead of `Reject()`.
6. **Add CLI commands**: `mo issue feedback list/show` — additive, no existing commands removed yet.
7. **Update Web UI**: Change approval card labels and add history rendering. Additive UI changes behind the same approval gate.
8. **Rollback**: The `Reject()` method can remain in the codebase as a fallback if `RequestChanges()` encounters issues. Web UI is a single component change. CLI can fall back to JSON output of the new commands.

## Open Questions

- **Should the feedback task use a new agent session or reuse the stage session?** The proposal specifies `session: ${{ stage.name }}` (same session as existing stage work). Reusing the session gives the agent context of what was planned/reviewed. However, if the stage session is large, a fresh session may be more efficient. Start with same-session and measure.
- **Should feedback resolution be validated (e.g., requires certain artifacts)?** The proposal says the agent writes a "concise resolution summary" — not a structured artifact. For v1, the resolution is free-form text in `resolutionSummary`. If validation needs emerge (e.g., must reference changed files), add as a follow-up.
- **Should we expose a separate `mo issue request-changes` CLI command?** The proposal focuses on `mo issue feedback` for reading feedback. The "request changes" action is primarily a Web UI flow. A CLI command for requesting changes could be added later if CLI-driven review workflows become common.
