# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The spec's "核心 partial 只保留 Build 与共享 helper" requirement paragraph (spec.md:5) and scenario THEN clause (spec.md:10) enumerated only the four pre-existing core helpers (`NumberArg`/`ProjectIssuesPath`/`IssueTemplatesPath`/`IsOptionProvided`), omitting the two NEW shared helpers `ValidateOutput` and `ResolveProjectId` that this change introduces into the core partial. This contradicted design D1's core-partial member table (design.md:39, which lists `ValidateOutput()` (new) + `ResolveProjectId()` (new)), design D3/D4 (which place both helpers in the core partial), and task T-008's acceptance criterion (tasks.json:153, which audits the core partial for all six helpers including the two new ones). Updated both spec locations to include `ValidateOutput` and `ResolveProjectId` in the core-partial helper enumeration.
  Verification: Re-read spec.md:5 and spec.md:10; the enumeration now reads `NumberArg`/`ProjectIssuesPath`/`IssueTemplatesPath`/`IsOptionProvided`/`ValidateOutput`/`ResolveProjectId`, matching design.md:39 and tasks.json:153. The second spec Requirement ("重复 CLI 惯法收拢为共享 helper") already defines these two helpers, so the cross-reference is now internally consistent. No product behavior or architecture changed.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: Design D2 carves `BuildWorkflowConfigSet` (228L, the file's largest method) into its own `Issue.WorkflowConfigSet.cs` partial, and task T-005 implements this. The spec's "子命令簇各自独立成 partial 分文件" scenario lists 8 clusters but does not explicitly mention that the Workflow config cluster spans two files (Workflow.cs + WorkflowConfigSet.cs). This is acceptable — the spec's invariant is "each cluster lives in independent partial file(s), never back-filled into core", which a two-file cluster satisfies — but noting it for traceability.
  SuggestedAction: Optionally add a one-line note in the spec scenario that the Workflow config cluster MAY split across `Issue.Workflow.cs` + `Issue.WorkflowConfigSet.cs` to keep the complexity-ranking acceptance achievable. Not required; the current spec wording does not prohibit it.
  Status: follow-up

<promise>PASS</promise>
