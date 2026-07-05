# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-0]
  Severity: info
  Scope: review
  Evidence: No safe review-time repairs were applied. The unresolved findings involve behavior, public command guidance, specs, or workflow traceability.
  Verification: `git diff --check` passed with no output.
  Status: resolved

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: workflow read commands / run-scoped subresource endpoints
  Evidence: The read spec requires every unknown run id to surface a server error and exit non-zero (`openspec/changes/issue-381/specs/workflow-run-reads/spec.md:101-108`), but most new CLI read commands call endpoints that do not validate the run exists. `show -o yaml` calls `/api/workflow-runs/{id}/yaml` (`packages/cli/Mohist.Cli/MohistCliCommands.Workflow.Reads.cs:56-58`), while that endpoint only loads template YAML and does not check `GetStatusAsync` first (`packages/server/src/Mohist.Server/Api/WorkflowRoutes.cs:12-19`). For an unknown run, `WorkflowProfileManager.LoadTemplateAsync` can still resolve a default system profile because `ResolveRunContextAsync` returns `RunExists=false` without failing (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowProfileManager.cs:247-258`) and the resolver falls back to the first enabled system profile (`packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/EffectiveWorkflowProfileResolver.cs:38-44`). `variables` similarly returns effective variables without existence validation (`packages/server/src/Mohist.Server/Api/WorkflowRoutes.cs:22-37`), and `events` / `list-sessions` return OK lists directly (`packages/server/src/Mohist.Server/Api/WorkflowEventRoutes.cs:31-35`, `packages/server/src/Mohist.Server/Api/WorkflowSessionRoutes.cs:10-11`). CLI tests fake 404s for these paths (`packages/cli/tests/Mohist.Cli.Tests/CliWorkflowReads.cs:590-648`), so they do not match the real server behavior. [disallowed:behavior-change]
  SuggestedAction: Add run-existence validation to the run-scoped YAML, variables, events, and sessions list paths used by `mo workflow`, or make the CLI first fetch the bare detail endpoint before rendering those subresources. Add server integration tests proving unknown `workflowRunId` returns `not_found` for all five read commands' backing endpoints.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowRunDetailApiSpecs`, add equivalent unknown-run cases for YAML/variables/events/sessions, and run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `GET /api/workflow-runs/{workflowRunId}` issue correlation
  Evidence: The acceptance contract says `show` must include the associated issue number and title so consumers holding only a `workflowRunId` need no reverse lookup (`openspec/changes/issue-381/specs/workflow-run-reads/spec.md:33-41`; issue acceptance also requires closing the agent-subscriptions prerequisite). The implementation builds `issueRef` through `GetIssueRefForWorkflowRunAsync` (`packages/server/src/Mohist.Server/Api/WorkflowRoutes.Detail.cs:39-43`), but that method delegates to `GetIssueIdForWorkflowRunAsync`, which filters `IssueRow.Status == "inProgress"` (`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:92-101`, `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:122-125`). The new test codifies that an existing terminal issue row yields `issueRef: null` (`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Api/WorkflowRunDetailApiSpecs.cs:92-108`). That means completed/archived issues with a preserved run id lose the correlation context the new command was added to provide. [disallowed:product-behavior]
  SuggestedAction: Keep the in-progress-only lookup for completion-handler semantics if needed, but add a separate issue-ref lookup for the detail read model that returns the issue associated with the run regardless of terminal issue status, returning null only when the issue row/binding is actually missing.
  Verification: Add a server spec where a completed issue row still produces `issueRef.number` and `issueRef.title`, then rerun `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowRunDetailApiSpecs`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: bundled CLI skill guidance
  Evidence: This branch removes `mo workflow list` (`packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs:7-25`) and documents the migration to `mo project workflow profile list` (`docs/cli-reference.md:304-315`), but the CLI package still ships skill instructions that tell agents to run the removed path. `packages/cli/Mohist.Cli/skill-data/mohist-create-issue/SKILL.md:51-57`, `:89`, `:111`, and `:160` still reference `mo workflow list --described`; `packages/cli/Mohist.Cli/skill-data/mohist/SKILL.md:125-130` still lists `mo workflow list`. Agents following bundled guidance will fail after the migration. [disallowed:public-guidance-change]
  SuggestedAction: Update bundled skill-data references to `mo project workflow profile list --described` / `mo project workflow profile list`, including fallback wording and checklists, and add a regression test if skill-data command references are validated elsewhere.
  Verification: Search `packages/cli/Mohist.Cli/skill-data` for `mo workflow list` and rerun the CLI tests.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: canonical specs
  Evidence: The repository-level spec still requires the command this change removes: `specs/cli-workflow-list/spec.md:3-5` says the CLI SHALL provide `mo workflow list`, with scenarios at `:8`, `:14`, `:20`, `:26`, and `:49`. The implementation no longer registers that subcommand (`packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs:7-25`), and docs mark it unavailable (`docs/cli-reference.md:313`). This creates a direct spec conflict for integration/validation. [disallowed:spec-contract-change]
  SuggestedAction: Update, replace, or archive the canonical `cli-workflow-list` spec so the profile-list requirement lives under `mo project workflow profile list`, and make the issue-381 delta consistent with the canonical spec set.
  Verification: Run the repository's OpenSpec validation command once available. In this workspace, `openspec validate issue-381 --strict` could not run because `openspec` is not installed.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: workflow-run control server specs
  Evidence: T-001 requires coverage for each verb's grain call and structured error mapping (`openspec/changes/issue-381/tasks.json:10-17`), but several server specs only assert the guard did not reject rather than proving the intended grain method/effect occurred. `ActiveOnly_OnPendingRun_AdmittedByGuard_NotRejectedAsNotActive` checks only that approve/reject were not rejected as not-active (`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Api/WorkflowRunControlApiSpecs.cs:120-142`), and `Retry_OnActiveRun_AdmitAndInvokesRetryAsync` similarly only asserts not-not-found and not-not-active (`:144-159`). The class comment claims coverage for `stage_not_reached`, `active_work_in_range`, and `session_context_exhausted` (`:24-31`), but executable assertions cover `unknown_stage` for rerun-from-stage (`:177-192`) and do not exercise the other mappings.
  SuggestedAction: Strengthen tests to assert observable state/effects for approve, reject, retry, rerun, resume, pause, stop, and add server-side cases for `stage_not_reached`, `active_work_in_range`, and `session_context_exhausted` mapping parity.
  Verification: Rerun `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowRunControlApiSpecs`.
  Status: open

- [ID: item-6]
  Severity: cleanup
  Scope: workflow traceability artifacts
  Evidence: `openspec/changes/issue-381/tasks.json` still has `"passes": false` for every task, including completed implementation/docs tasks (`openspec/changes/issue-381/tasks.json:24`, `:46`, `:70`, `:95`, `:122`, `:146`), while `openspec/changes/issue-381/progress.txt` records successful validation and `openspec/changes/issue-381/self-review.md:3` reports PASS. The artifacts are review context, not product deliverables, but this contradiction creates traceability risk for check/integrate.
  SuggestedAction: Update task pass state or record why task pass flags intentionally remain false after Build.
  Verification: Re-read `tasks.json`, `progress.txt`, and `self-review.md` for consistent task status.
  Status: open

- [ID: item-7]
  Severity: minor
  Scope: CLI help for `mo workflow show -o yaml`
  Evidence: `mo workflow show` accepts `-o yaml` via a special case (`packages/cli/Mohist.Cli/MohistCliCommands.Workflow.Reads.cs:49-58`), and docs advertise that support (`docs/cli-reference.md:277-291`), but the shared option used by `show` still describes only `table, json` (`packages/cli/Mohist.Cli/MohistCliCommands.cs:61-65`). This makes `mo workflow show --help` under-document a required format. [disallowed:public-help-change]
  SuggestedAction: Provide a command-specific output option/help description for `show`, or extend the option helper so callers can declare supported formats without misleading other commands.
  Verification: Run `mo workflow show --help` or the CLI help tests and confirm YAML is shown only where supported.
  Status: open

## Follow-up Items

- [ID: item-8]
  Severity: follow-up
  Scope: design index wording
  Evidence: `design/README.md:19` still says the Agent uses `mo workflow get` to pull context, while the delivered command is `mo workflow show`. Other issue-381 artifacts explain the historical `get` prerequisite and the `show` reconciliation, so this is a small consistency issue rather than a separate product bug.
  SuggestedAction: Update the index wording to refer to `mo workflow show <runId>` or explicitly mark `get` as the historical prerequisite name.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: warning
  Scope: dependency security output during server test build
  Evidence: Running server tests triggered an npm audit summary reporting 9 vulnerabilities: 3 moderate, 3 high, and 3 critical. The reviewed change does not modify dependency manifests, so this is not attributed to issue-381, but it remains security debt.
  SuggestedAction: Triage with `npm audit` in the appropriate package context and update dependencies through a separate issue.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: verification environment
  Evidence: `openspec validate issue-381 --strict` could not run because `/bin/bash` reported `openspec: command not found`. The full server test project also exceeded the initial 120s timeout, but the focused new workflow API specs passed.
  SuggestedAction: Install/provide the OpenSpec CLI in the review environment and run the full validation before integration.
  Status: out-of-scope

## Verification Summary

- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 724/724.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowRunControlApiSpecs` passed: 27/27.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowRunDetailApiSpecs` passed: 6/6.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter FullyQualifiedName~WorkflowCliProfile` passed: 17/17.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore` exceeded the 120s timeout before completion.
- `git diff --check` passed with no output.

<promise>FAIL</promise>
