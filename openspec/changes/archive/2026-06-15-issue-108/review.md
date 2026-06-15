# Review Report

## Result: PASS

## Repaired Items

<!-- No repairs applied. The candidate change is small, surgical, and well-scoped:

- The YAML change in mohist-default.workflow.yaml replaces `mohist/openspec-sync` with `mohist/acp-agent` and adds `session`, `prompt`, `agent` inputs while preserving the task id, title, and stage ordering.
- The new spec-sync.prompt follows the existing builtin pattern (YAML frontmatter, `mo issue show` header, `<artifact>` body, structured instructions).
- The two test fixture changes in WorkflowProjectionSpecs.cs align the test fixture with the YAML change (mohist/acp-agent => UserFacing classification).
- All targeted tests pass; the failures observed in the full test suite are pre-existing environmental issues unrelated to this change.

No formatting, typo, missing-guard, or import-cleanup issues were found that fall within the review's repair authority. The change is ready as-is. -->

## Blocking Items

<!-- No blocking items found. -->

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml:241-247
  Evidence: The new `integrate:spec-sync` task uses `mohist/acp-agent` with no `expect:` block. Without an `expect:` block, `verifyExpectations` in `packages/runner/src/actions/expectations.ts:13-32` returns `satisfied: true` (no files/markers to check). The task therefore succeeds if and only if the agent itself returns a successful status. A buggy or hallucinating agent that silently writes malformed content or skips writes would still report success. The plan/design specs (REQ-WFE-006) require post-merge validation to be mandatory, but the runner does not re-validate the merged spec on the server side; validation is entirely delegated to the agent.
  SuggestedAction: Consider adding an `expect.files` block listing at least one well-known main spec path (or a marker file the agent must produce) so the runner can independently verify the merge actually wrote output. This is consistent with how other `mohist/acp-agent` tasks (e.g. plan stage) declare `expect:` in the workflow YAML.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins/spec-sync.prompt
  Evidence: The prompt instructs the agent to read `openspec/specs/`, write to `openspec/specs/{capability}/spec.md`, and skip workspace writes. However, the agent's working directory (`workDir`) is the integration workspace, not the repository root where `openspec/specs/` lives. The agent relies on `openspec/specs/` being reachable from the workspace, which is only true if the workspace is a checkout of the repo. For consistency, the prompt could explicitly state the working-directory assumption (e.g., "The current working directory is the repository root or a checkout that includes `openspec/specs/`"). This is an agent-prompt documentation improvement, not a bug.
  SuggestedAction: Add a one-line assumption note in the prompt's `<output>` or `<task>` block clarifying that the working directory is the repository root containing `openspec/specs/`.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: packages/runner/src/actions/openspec.ts:39-48
  Evidence: The original `openspecSyncAction` is preserved in the registry (`packages/runner/src/actions/registry.ts:48`) for backward compatibility as planned in the design. The action now has no workflow caller. Per `self-review.md` item-2, this is left as-is for a future cleanup issue. No callers exist in the codebase: `grep -rn "openspec-sync\|openspecSyncAction" packages/ --include="*.ts" --include="*.cs"` returns only the registration, the unused `import` in `registry.ts:8`, and the function itself. The dead `import` in `registry.ts:8` is technically still used (the function is still registered), so there is no truly dead code today. After a future cleanup, both lines should be removed together.
  SuggestedAction: Track a follow-up issue to delete `openspecSyncAction` and its registration once confidence in the agent-driven path is established. No code change needed in this change.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins/spec-sync.prompt:13
  Evidence: The `<artifact id="spec-sync">` tag in the new prompt omits the `schema="mohist-default"` attribute used by the plan-stage artifacts (`proposal.prompt:13`, `specs.prompt:13`, `design.prompt:13`, `tasks.prompt:13`). This is consistent with other non-plan artifacts (`build.prompt:13`, `review.prompt:13`, `self-review.prompt:13`) which also omit `schema=`. The omission is intentional and correct: the spec-sync task does not write a durable workflow artifact. No fix needed.
  SuggestedAction: None. This is documented as the intentional pattern.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: pre-existing
  Scope: packages/server/tests/Mohist.Server.Tests
  Evidence: The full server test suite shows 513 failures on this branch. Verified against `master` (without the issue-108 changes) by `git stash --include-untracked` + retest: same baseline shows 513 failures, all of which originate from `EventPublishingIntegrationFixture.CreateClient` (`Mohist.Server.Tests/Specs/Events/EventPublishingIntegrationFixture.cs:38`) calling `WebApplicationFactory<Program>`, which fails to start (WebHost startup). Same 229/376 baseline failure rate when filtering by `FullyQualifiedName~Workflow` due to `BacklogFixture.InitializeAsync` EF migrations. None of these failures are caused by the issue-108 changes.
  SuggestedAction: Out of scope for this change. Tracked separately as a pre-existing WebApplicationFactory / EF migration environment issue. The change's targeted tests (`DefaultPrompts_LoadIssueDetailsThroughMohistCli`, `WorkflowStatusMapper_ProjectsTaskClassification`, `DeriveClassification_ForCoreAndMohistInternal_UsesOrchestration`, plus all 32 tests in `MohistDefaultWorkflowProfileSpecs` and all 29 tests in `WorkflowProjectionSpecs`/`TaskRequiredFilesSpecs`) all pass.
  Status: pre-existing

- [ID: item-6]
  Severity: pre-existing
  Scope: packages/runner
  Evidence: `npm test -w packages/runner` reports 39 failed / 173 passed / 212 total. Verified against `master` (without the issue-108 changes) by `git stash --include-untracked` + retest: same baseline failure. Failures are in `acp-agent.spec.ts` and related ACP runtime tests, with no relationship to the spec-sync change.
  SuggestedAction: Out of scope for this change. The runner's `openspec.ts:39-48` (`openspecSyncAction`) was deliberately preserved; it has no test coverage in the runner test suite, but it also has no callers and is not exercised by the issue-108 change.
  Status: pre-existing

<promise>PASS</promise>
