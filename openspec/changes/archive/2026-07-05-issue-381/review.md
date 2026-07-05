# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Api/WorkflowRoutes.cs`, `packages/server/src/Mohist.Server/Api/WorkflowSessionRoutes.cs`
  Evidence: The new `EnsureWorkflowRunExistsAsync` guard (`WorkflowRoutes.cs:129-134`) is now used before `/variables/effective` and `/sessions` reads (`WorkflowRoutes.cs:31-46`, `WorkflowSessionRoutes.cs:11-16`). That changes existing workflow-scoped data paths from storage-keyed reads into reads that require a `WorkflowRuns` row. Two existing regression tests now fail: `PathContractRegressionSpecs.WorkflowEffectiveVariableKeyPath_ReturnsValueOrJsonNull` expects OK after storing variables under `/workflow-profile/variables`, but gets NotFound at line 331; `WorkflowSessionSpecs.GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder` gets 404 from `/api/workflow-runs/{workflowRunId}/sessions` at line 141. [disallowed:public-contract-change]
  SuggestedAction: Reconcile the new unknown-run requirement with the existing storage-keyed subresource contracts. Either preserve the old variables/sessions behavior with a cheaper resource-specific existence rule, or intentionally migrate the public contract and update the affected specs and callers in the same change.
  Verification: `dotnet test "packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj" -p:SkipWebBuild=true --filter "FullyQualifiedName=Mohist.Server.Tests.Specs.Api.PathContractRegressionSpecs.WorkflowEffectiveVariableKeyPath_ReturnsValueOrJsonNull"` fails with Expected OK / Actual NotFound. `dotnet test "packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj" -p:SkipWebBuild=true --filter "FullyQualifiedName=Mohist.Server.Tests.Specs.Workflow.Grain.WorkflowSessionSpecs.GivenRunnerReportsAcpSessionEvents_WhenSessionIsQueried_ThenEventsAreSavedInSessionOrder"` fails with 404 Not Found.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.Reads.cs`, `packages/cli/Mohist.Cli/MohistCliApi.cs`
  Evidence: `mo workflow status <runId> -o json` calls the same bare detail endpoint as `show` with only a different table shape (`MohistCliCommands.Workflow.Reads.cs:95-100`). `PrintEnvelopeAsync` ignores table shapes in JSON mode and prints the raw response data (`MohistCliApi.cs:751-760`). As a result, JSON status emits the full show DTO, including `issueRef` and metadata, instead of the compact status view required by the issue and `workflow-run-reads/spec.md:43-51`. Current tests only assert compactness for table output (`CliWorkflowReads.cs:290-341`). [disallowed:product-behavior-change]
  SuggestedAction: Add a compact status projection for JSON mode as well as table mode, then test `mo workflow status <runId> -o json` against `show -o json` to prove it omits show-only fields.
  Verification: Add a CLI test for `workflow status <runId> -o json` asserting the output is parseable JSON and does not include `issueRef` or show-only metadata.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs`, `packages/server/src/Mohist.Server/Api/WorkflowRoutes.WorkflowControl.cs`, `packages/cli/Mohist.Cli/MohistCliApi.cs`
  Evidence: The new run-scoped control endpoints return `ApiResults.Ok()` with no data (`WorkflowRoutes.WorkflowControl.cs:21-23`, `32-33`, `46-47`, etc.). In JSON mode, `PrintResponseAsync` prints `OK` when the success envelope has `data: null` (`MohistCliApi.cs:1020-1021`), so `mo workflow approve <runId> -o json` can emit non-JSON text against the real server. The current CLI test masks this by faking a non-null data payload (`CliWorkflowControlSpecs.cs:383-392`). This violates the acceptance criterion that control commands honor shared `-o json` output conventions. [disallowed:public-contract-change]
  SuggestedAction: Return or synthesize a valid JSON success payload for control commands in JSON mode, and add a test using `{ success: true, data: null }` to match the real API response.
  Verification: Run `mo workflow approve <runId> -o json` against a successful real endpoint and parse stdout as JSON; it should not be the literal text `OK`.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs`, `docs/cli-reference.md`
  Evidence: `mo project workflow profile list --described` accepts `-o`, but the described branch never resolves or uses the selected output mode (`ProjectWorkflow.cs:41-69`) and always calls `PrintWorkflowProfilesDescribedAsync`, which renders human text (`MohistCliApi.cs:837-876`). The docs state that `profile list` supports `-o table|json` and `--described` (`docs/cli-reference.md:315`), and the relocation task requires the same `-o` output-format handling for the moved profile command. [disallowed:product-behavior-change]
  SuggestedAction: Make `--described -o json` emit JSON through the shared output path or narrow the documented contract and tests if JSON is intentionally unsupported for described output.
  Verification: Add a CLI test for `project workflow profile list --described -o json` asserting stdout is valid JSON and not the human `id - displayName` rendering.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/WorkflowViews.cs`, `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`, `docs/agent-subscriptions.md`
  Evidence: The global `GET /api/workflow-runs/{workflowRunId}` response returns `issueRef` with only `number` and `title` (`WorkflowViews.cs:138-140`; `IssueQuerier.cs:125-130`). Issue numbers are project-scoped, while this endpoint is not project-scoped. A consumer that holds only `workflowRunId` cannot safely run follow-up issue commands in a multi-project environment without knowing the project, despite the acceptance goal that `show` provides enough associated issue context without another lookup. [disallowed:public-contract-change]
  SuggestedAction: Include a stable project identifier/ref in `WorkflowRunIssueRef`, update the CLI/docs examples, and add a test with two projects that have the same issue number to prove the returned ref is unambiguous.
  Verification: Seed two projects with issue `#1`, fetch a run detail for one, and verify the response contains enough information to address the correct issue from a script with no active project assumption.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `docs/agent-subscriptions.md`, `design/agent-subscriptions.md`
  Evidence: The product doc marks the `workflowRunId` prerequisite closed by `mo workflow show <runId>` (`docs/agent-subscriptions.md:9`), but the example response prompt still uses `{{issue}}`, `mo issue show {{issue}}`, and issue-scoped approve/reject commands directly (`docs/agent-subscriptions.md:71`), and the open variable list still names `{{issue}}` (`docs/agent-subscriptions.md:100`). The design doc says the handler renders `{{workflow_run_id}}` and explicitly does not provide `{{issue}}` (`design/agent-subscriptions.md:116-136`). This leaves product guidance inconsistent with the change that is supposed to close the dependency. [disallowed:documentation-contract]
  SuggestedAction: Rewrite the product example to start from `{{workflow_run_id}}`, call `mo workflow show <runId> -o json`, then use the associated issue info from that output.
  Verification: Re-read `docs/agent-subscriptions.md` and confirm no example relies on a handler-provided `{{issue}}` variable for workflow-run events.
  Status: open

- [ID: item-7]
  Severity: cleanup
  Scope: `specs/cli-workflow-list/spec.md`, `packages/cli/tests/Mohist.Cli.Tests/CliProjectWorkflowProfileSpecs.cs`
  Evidence: The active spec still says plain `mo project workflow profile list` displays each profile's display name, description, default marker, and multiline descriptions (`specs/cli-workflow-list/spec.md:3-18`), and says the profile-list endpoint returns `id`, `displayName`, `description`, and `isDefault` (`specs/cli-workflow-list/spec.md:31-43`). The implementation and tests now split plain listing through `/api/workflow-templates/system` with only template ids (`CliProjectWorkflowProfileSpecs.cs:187-243`) and put descriptions behind `--described` (`CliProjectWorkflowProfileSpecs.cs:113-141`). The checked-in spec no longer matches the product surface delivered by this change.
  SuggestedAction: Update `specs/cli-workflow-list/spec.md` to match the relocated command contract, including the plain vs `--described` split, or change the implementation to satisfy the existing spec.
  Verification: Compare the spec scenarios against `mo project workflow profile list` and `mo project workflow profile list --described` tests; they should describe the same endpoint and fields.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.Reads.cs`, `packages/server/src/Mohist.Server/Api/WorkflowEventRoutes.cs`
  Evidence: `mo workflow events --limit` accepts any `int?` and forwards zero or negative values directly (`MohistCliCommands.Workflow.Reads.cs:173-200`); the server forwards the value into event listing without validation (`WorkflowEventRoutes.cs:31-37`). The only CLI test covers `--limit 50` (`CliWorkflowReads.cs:465-480`). A non-positive limit does not represent a useful bound and can yield empty output or provider-specific query behavior.
  SuggestedAction: Add local CLI validation (and preferably server-side validation) requiring `--limit` to be positive, with tests for `0` and `-1` that assert no request is sent or a structured 400 is returned.
  Verification: Run new CLI/API tests for `mo workflow events <runId> --limit 0` and `--limit -1`.
  Status: open

## Follow-up Items

- [ID: item-9]
  Severity: follow-up
  Scope: `openspec/changes/issue-381/*`
  Evidence: Some workflow artifacts still preserve the older prerequisite wording `mo workflow get <runId>` even though the delivered command is `show` (`proposal.md:14`, `design.md:10`, `specs/workflow-run-reads/spec.md:35`). This is not a product deliverable by itself, but it can create traceability noise later.
  SuggestedAction: Replace the old wording with `mo workflow show <runId>` or mark it explicitly as historical issue language.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: `packages/cli/tests/Mohist.Cli.Tests/CliWorkflowControlSpecs.cs`
  Evidence: Dry-run coverage exists for approve, reject, and rerun variants (`CliWorkflowControlSpecs.cs:405-464`), but not for retry, resume, pause, and stop. The implementation appears mechanically consistent, so this is a coverage improvement rather than a demonstrated behavior bug.
  SuggestedAction: Add a theory for the remaining dry-run verbs asserting exit 0, no HTTP requests, and the expected `/api/workflow-runs/{id}/{verb}` path.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-11]
  Severity: info
  Scope: full test suite
  Evidence: `npm test` was run and did not complete within the 120s tool timeout after surfacing the two blocking server failures listed in item-1. Targeted changed-surface tests passed: CLI workflow/profile tests passed 78/78, and targeted server workflow-run/profile tests passed 67/67.
  SuggestedAction: Re-run `npm test` after fixing item-1; the suite must complete cleanly before this candidate can pass.
  Status: out-of-scope

<promise>FAIL</promise>
