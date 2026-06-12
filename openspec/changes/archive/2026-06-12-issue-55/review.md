# Review Report

## Result: PASS

The previous review's blocking item-1 is fixed. The
`WorkflowArtifactBindService` now accepts an optional
`JsonElement? variables` parameter and renders each declared
`path` against the same variables the runner used, so the
runner-rendered upload path and the workflow-definition declared
path agree on the comparison key. The `WorkflowGrain`
computes the resolved variables at bind time via the new
`ResolveBindVariablesAsync` helper (mirroring the dispatch-time
variable resolution at `MakeDispatchAsync`, including
stage-scoped overlay) and passes them to the bind service. Two
new server tests cover the template-path bind surface:
`BindAsync_DeclaredPathWithTemplateVariable_RendersAndMatchesUploadedPath`
asserts the bind succeeds when the declared path is a template
and the upload is the rendered form, and
`BindAsync_DeclaredPathWithMissingTemplateVariable_ReturnsInformativeError`
asserts the bind returns a precise error naming the missing
variable. All 82 server artifact tests pass, all 207 runner
tests pass, all 37 web artifact tests pass.

I also repaired two small, local formatting defects during
review (see below).

## Repaired Items

- [ID: item-R1]
  Severity: info
  Scope: formatting
  Evidence: At
  `packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactBindService.cs:99-140`,
  the `if (declaredArtifacts is not null && !declaredArtifacts.IsEmpty)`
  block was indented 12 spaces, which visually placed it inside
  the preceding `if (foreignIds.Count > 0)` block at line 92-97.
  The braces were correct — the block is a sibling `if`, not
  nested — but the misleading indentation obscured the control
  flow. All 82 tests pass with the corrected 8-space indent.
  Verification: `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj`
  succeeds; `dotnet test ... --filter "FullyQualifiedName~WorkflowArtifact"`
  — 82 passed, 0 failed.
  Status: resolved

- [ID: item-R2]
  Severity: info
  Scope: typo
  Evidence: At
  `packages/runner/src/runtime/executor.ts:138` the comment read
  "a tolerated literal" (with a second 'e'). The correct
  English spelling is "a tolerated literal". The previous review's
  item-9 flagged this as an optional cleanup; this is the same
  small fix the previous pass left unrepaired.
  Verification: `npm test -w packages/runner` — 207 passed, 0 failed.
  Status: resolved

## Blocking Items

- None.

The previous review's blocking item-1 (the bind service's
declared-vs-uploaded path mismatch) is fixed. Both sides now
render the declared path against the same workflow variables, and
the comparison key agrees. I traced the fix end-to-end:

- The runner's `captureAndUploadArtifacts` (executor.ts:127-161)
  renders the entire `work.artifacts` object via
  `renderTemplate(work.artifacts, variables)` and passes the
  rendered object to `captureArtifacts` via the new
  `renderedArtifacts` field on `ArtifactCaptureInput`
  (artifact-capture.ts:48-56, 123). The capture layer reads the
  resolved workspace path and uploads with the rendered path.
  The runner tests `rendersTemplateVariablesInDeclaredArtifactPathsBeforeCapture`,
  `rendersTemplateVariablesInDeclaredDirectoryArtifactPathsBeforeCapture`,
  and `failsTaskThroughNormalFailureWhenDeclaredArtifactTemplateVariableIsMissing`
  cover this surface.

- The grain's `BindArtifactUploadsAsync`
  (WorkflowGrain.cs:772-806) calls the new
  `ResolveBindVariablesAsync` helper (WorkflowGrain.cs:808-838)
  which loads the workflow template and independent variables,
  patches them, and overlays the current stage's variables —
  the same resolution path as `MakeDispatchAsync` at lines
  556-577. The grain passes the resolved `JsonElement` to the
  bind service's new `variables` parameter.

- The bind service's `BindAsync`
  (WorkflowArtifactBindService.cs:99-140) iterates each
  declared file, renders the declared path through the injected
  `PromptTemplateEngine.Render(string, JsonElement)` with the
  same variables, and compares the rendered path against the
  uploaded paths. If the variable is missing, the bind service
  returns a precise error naming the missing variable. The
  uploaded path is the rendered form because the runner
  already substituted the template before upload.

- The new server tests
  `BindAsync_DeclaredPathWithTemplateVariable_RendersAndMatchesUploadedPath`
  and
  `BindAsync_DeclaredPathWithMissingTemplateVariable_ReturnsInformativeError`
  exercise the bind service end-to-end with template-substituted
  paths and assert both the success and error surfaces.

The acceptance criteria that the fix unblocks:

- "A task run records a `WorkflowArtifact` for each existing
  declared `artifacts.files` entry before its result is bound as
  completed" — the default workflow's `ai-review` task now
  binds successfully because the rendered declared path matches
  the rendered uploaded path.
- "Missing or failed upload for a required declared artifact
  causes the task run to fail through the normal task failure
  path" — preserved. The bind service still fails the task when
  a required declared artifact is missing; it now also reports
  a precise error when a declared path references an undefined
  variable.

I verified the full acceptance-criteria surface. The other
endpoints (latest-by-path, history, task-run filter, content
retrieval) were already correct in the previous pass and the
new tests do not regress them.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:748
  Evidence: `ProcessTaskResultAsync` overrides the bind service's
  precise error (e.g. "Declared artifact path '${{ openspecChangeDir
  }}/review.md' references undefined variable(s): 'openspecChangeDir'")
  with the hard-coded "Required declared artifacts were not
  uploaded or validated". Same as the previous review's item-9
  and item-2. The fix to item-1 makes this override more visibly
  wrong: the bind service now returns informative errors that the
  grain immediately discards.
  SuggestedAction: `events.AddRange(run.FailTask(new TaskResult("failed", bindResult.Error ?? "Required declared artifacts were not uploaded or validated")));`
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactUploadService.cs:218-223
  Evidence: For directory uploads, `request.Size` is the JSON envelope
  size and `writeResult.Size` is the sum of decoded contained file
  bytes. They differ by design, so the
  "declared size ... wrote ... bytes" warning fires on every
  directory upload. Same as the previous review's item-3 and
  item-10.
  SuggestedAction: Skip the size-mismatch warning when
  `kind == "directory"`, or compare against `writeResult.Size`
  only when the runner's declared size represents actual content
  bytes (file uploads only).
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactBindService.cs:90-97
  Evidence: The `foreignIds` check is dead code under the current
  where-clause. Same as the previous review's item-4. The
  upstream where-clause filters `pendingUploads` by
  `artifactUploadIds.Contains(p.UploadId)`, so every matched id
  came from `artifactUploadIds` and `foreignIds` is always empty.
  SuggestedAction: Either remove the dead check, or make it the
  sole guard by removing the upstream count==0 short-circuit
  and keeping the foreignIds check as the only failure path for
  invalid upload ids. Cosmetic.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactUploadService.cs (TTL cleanup)
  Evidence: The pending upload row's `ExpiresAt` is indexed
  (`MohistDbContextModelSnapshot.cs:713-714`) but no hosted
  service, Orleans reminder, or startup job walks that index.
  Same as the previous review's item-5.
  SuggestedAction: Add a hosted background service that deletes
  pending uploads past their TTL and the corresponding storage
  directories, plus a test that seeds an expired row and asserts
  it disappears.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactUploadService.cs:403-407
  Evidence: `IsUniqueConstraintViolation` matches the inner exception
  message for "UNIQUE" or "constraint". Same as the previous
  review's item-6; SQLite-only today.
  SuggestedAction: Inspect the `DbUpdateException` via
  `dbContext.ChangeTracker` or use
  `Database.IsUniqueConstraintViolation` when EF Core supports
  it across providers.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: packages/web/src/widgets/issue-workflow/ui/LatestArtifactsPanel.tsx
  Evidence: The latest-artifacts panel renders a flat list without
  a visual "this is the newest review.md" highlight or a
  "produced by ai-review.2" caption. Same as the previous review's
  item-7.
  SuggestedAction: Add a small producing-task-run caption to the
  review.md row (or whichever path is the most-recently rewritten)
  so users can tell at a glance which round is the latest. Add a
  web test asserting the caption.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: packages/runner/src/runtime/artifact-capture.ts:264-289
  Evidence: `resolveArtifactPath` checks both the realpathed and
  non-realpathed forms. Defensive but verbose. Same as the
  previous review's item-8.
  SuggestedAction: Optional cleanup.
  Status: pre-existing

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueWorkflowProductLoopSpecs.cs,
        packages/server/tests/Mohist.Server.Tests/Specs/Foundation/WorkflowVariableSpecs.cs,
        packages/web/src/pages/epics/ui/EpicListPage.test.tsx,
        packages/web/src/widgets/app-shell/ui/Header.test.tsx,
        packages/web/tests/SessionPage.test.tsx
  Evidence: Six server tests in
  `IssueWorkflowProductLoopSpecs` / `WorkflowVariableSpecs` and
  seven web tests in `EpicListPage`, `Header`, and `SessionPage`
  fail with "Runner has no work" or DOM-mismatch errors.
  Confirmed pre-existing on `master`. Not introduced by this
  change. Same as the previous review's item-11.
  SuggestedAction: Track the flakiness separately.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Workflow/Services/WorkflowYamlSerializer.cs:142-152
  Evidence: `IsVerdictMarker` calls `ToUpperInvariant` then
  `OrdinalIgnoreCase` `Contains` checks — the no-op redundancy
  noted in the previous review (item-12). Behavior is correct.
  SuggestedAction: Optional cleanup.
  Status: pre-existing

- [ID: item-11]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Artifacts/WorkflowArtifactUploadService.cs:328-332
  Evidence: `ReadDirectoryEnvelopeAsync` reads the entire envelope
  content into memory via `ms.ToArray()` before validating. The
  runner contract limits the envelope to 64MB
  (`maxDirectoryTotalSize`), so this is bounded in practice, but
  a misbehaving runner could exhaust server memory before the
  storage service's directory limits kick in. Pre-existing
  concern not introduced by this change. Same as the previous
  review's item-13.
  SuggestedAction: Add a hard cap on the envelope byte length
  before the `CopyToAsync` and reject with a structured error.
  Status: pre-existing

<promise>PASS</promise>
