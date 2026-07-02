# Review Report

## Result: PASS

## Repaired Items

_No repairs were needed. The self-review (self-review.md) resolved the only spec inconsistency where the core-partial helper enumeration omitted `ValidateOutput` and `ResolveProjectId` — the repaired spec now correctly lists all six helpers (`specs/cli-module-structure/spec.md:5`, `specs/cli-module-structure/spec.md:10`)._

## Blocking Items

_None. All acceptance criteria pass with concrete evidence._

### Acceptance Criteria Verification

| Criterion | Evidence |
|-----------|----------|
| `IssueCommands` 拆为多个 `partial` 分文件 | 11 Issue.* files: Issue.cs (core) + 10 cluster partials (`ls packages/cli/Mohist.Cli/MohistCliCommands.Issue*.cs` → CrudReads, CrudWrites, Lifecycle, Session, Workflow, WorkflowConfigSet, Feedback, Prereq, Comment, Template) |
| 核心文件收敛为 `Build()` + 共享 helper | `MohistCliCommands.Issue.cs:8-87` — `Build()` (l.8-43) + `NumberArg` (l.45), `ProjectIssuesPath` (l.47), `IsOptionProvided` (l.54), `ValidateOutput` (l.61), `ResolveProjectId` (l.72), `IssueTemplatesPath` (l.81). No subcommand build methods. |
| 24x output-mode 校验收拢为 `ValidateOutput` | Grep count across all Issue.* partials: 24 call sites to `ValidateOutput(api,`, 0 inline `ValidateOutputMode`/`OutputModeResult` references outside the core helper. |
| 31x project-id 解析收拢为 `ResolveProjectId` | Grep count across all Issue.* partials: 31 call sites to `ResolveProjectId(api,`, 0 inline `ResolveProjectIdAsync` references outside the core helper. |
| 命令名/别名逐字节不变 | Verified by 231/231 passing spec tests covering all subcommands, aliases (`ls`, `coder-sessions`), HTTP paths, PATCH field-omission semantics, `@file` expansion, output formats, exit codes. |
| 全部 issue spec 通过 | `dotnet test --filter 'FullyQualifiedName~CliIssue'` → **231 passed, 0 failed**. Covers CliIssueCommandSpecs, CliIssueWorkflowConfigSpecs, CliIssueSessionSpecs, CliIssueLabelSpecs, CliIssueTemplateCommandSpecs, CliIssueUpdatePatchBodySpecs, CliIssuePrereqSpecs, CliIssueCommentAndFeedbackSpecs, CliIssueRerunFromStageSpecs, CliIssueRejectAndStopSpecs, CliIssueExecutionConfigFlagsSpecs, CliIssueWorkflowProfileSpecs. |
| 各分文件脱离 cli 包复杂度前列 | `scc` top 5: MohistCliApi.cs (112), ProjectWorkflow.cs (98), ScheduledTaskInstaller.cs (88), Agent.cs (75), DeliveryFailureGuidance.cs (75). First Issue.* entry: **CrudWrites.cs at position 6 (complexity 68)**. |
| `ParseLabelsFromIssue` / `PrintCreateGuidance` → `private` | `ParseLabelsFromIssue` at `MohistCliCommands.Issue.CrudWrites.cs:379` (`private`), called only from `LoadCurrentLabelsAsync` (same file l.370). `PrintCreateGuidance` at `CrudWrites.cs:398` (`private`), called only from `BuildCreate` (same file l.147). Repo-wide grep confirms zero external callers. |
| `dotnet build` (TreatWarningsAsErrors) | **0 Warning(s), 0 Error(s)**. |
| `IssueCommands.Build()` public shape unchanged | Only caller: `MohistCliCommands.Build` → `Program.cs`. Signature unchanged. |

## Follow-up Items

- [ID: item-F1]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.CrudWrites.cs`
  Evidence: CrudWrites.cs sits at scc complexity 68 (position 6), one slot below the top-5 cutoff. BuildCreate (~150 lines of option declaration + body assembly) and BuildUpdate (~160 lines of option declaration + PATCH payload assembly) each carry significant option-declaration verbosity that could drive the file back into the top 5 with future additions.
  SuggestedAction: Consider extracting shared option-declaration helpers (e.g. for `--body`/`--body-file`/`--body-stdin`, `--stage-models`/`--stage-model-variants`, draft flags) that BuildCreate and BuildUpdate both need, following the same "cross-cutting helper" pattern established by `ValidateOutput`/`ResolveProjectId`. This is not a defect in the current change.
  Status: follow-up

- [ID: item-F2]
  Severity: follow-up
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Workflow.cs`
  Evidence: `BuildWorkflowConfigClear` at ~220 lines (l.185-349) is the most complex single method in the Workflow partial (nesting depth 4, handling three optional operations with interleaved error handling). It accounts for most of Workflow.cs's complexity.
  SuggestedAction: Consider extracting the var-clear, template-clear, and prompt-clear operations into private helper methods within the same partial, following the "verbatim extract method" pattern that preserves behavior. Not required for this issue.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-P1]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Lifecycle.cs:241`
  Evidence: `BuildArchive` uses `Uri.EscapeDataString(number!)` directly, while all other commands that escape issue numbers use `MohistCliCommands.Escape(number!)`. `Uri.EscapeDataString` and `MohistCliCommands.Escape` may have different encoding behaviors (e.g. handling of unreserved characters). This inconsistency existed in the pre-refactor monolith and was preserved verbatim.
  SuggestedAction: Unify on `MohistCliCommands.Escape` for consistency, in a separate refactor PR.
  Status: pre-existing

- [ID: item-P2]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Lifecycle.cs:184-246`
  Evidence: `BuildArchive` validates `--output` before resolving project-id (l.212-213), unlike most other commands that resolve project-id first. While this is harmless (ValidateOutput has no side effects beyond error writing), it is inconsistent with the prevailing pattern across the other 23+ sites. Preserved from the pre-refactor monolith.
  SuggestedAction: Optionally reorder for consistency in a separate cleanup. Low priority — no behavioral impact.
  Status: pre-existing

- [ID: item-P3]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Lifecycle.cs:7-32, 248-272`
  Evidence: `BuildAction` (9 verbs: start/approve/close/reopen/retry/rerun/force-stop/resume/unarchive) and `BuildGetSub` (4 verbs: logs/events/diff/commits) do not support the `--output` flag and do not call `ValidateOutput`. Acknowledged as pre-existing test debt in the issue's Non-Goals ("不补 BuildAction/BuildGetSub 工厂下属若干动词既存的测试缺口").
  SuggestedAction: Add `--output` support to these verbs in a separate feature PR. Out of scope for this refactor.
  Status: out-of-scope

- [ID: item-P4]
  Severity: info
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Session.cs:260-263`
  Evidence: `BuildSessionFollowup` uses a `BodyInputResolver.ResolveAsync` overload with `SourceFlags`, while all other body-resolution sites use the simpler overload. This was already the case in the pre-refactor monolith — the verbatim move preserved it.
  SuggestedAction: Evaluate whether `SourceFlags` is still needed or if the simpler overload would suffice. Out of scope.
  Status: pre-existing

<promise>PASS</promise>
