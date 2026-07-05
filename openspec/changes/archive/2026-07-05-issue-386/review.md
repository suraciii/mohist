# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

Issue 386 acceptance criteria were verified against the post-build snapshot:

- `mo workflow get <runId>` is canonical and `show` is registered as the same command's alias in `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.Reads.cs`; `mo workflow status` is no longer registered from `packages/cli/Mohist.Cli/MohistCliCommands.Workflow.cs`.
- `mo project workflow profile enable|disable <profile-id>` are registered in `packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs` and POST `{ profileId }` to `/workflow-profile/enable` and `/workflow-profile/disable`.
- `mo issue rerun <number> --from-stage <stage>` routes to `/rerun-from-stage`, while `rerun-from-stage --stage` remains as the transitional peer command in `packages/cli/Mohist.Cli/MohistCliCommands.Issue.Lifecycle.cs`.
- `mo agent archive` and `mo label delete` are the canonical commands, with the requested transitional aliases in `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs` and `packages/cli/Mohist.Cli/MohistCliCommands.Label.cs`.
- `docs/cli-reference.md` removed the six issue-386 implementation-gap rows, and the issue comment records the alias strategy and inventory.

Verification: `npm run build` passed. The issue-scoped CLI coverage passed with `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --filter "FullyQualifiedName~CliWorkflowReads|FullyQualifiedName~CliProjectWorkflowProfileSpecs|FullyQualifiedName~CliIssueRerunFromStageSpecs|FullyQualifiedName~CliAgentCommandSpecs|FullyQualifiedName~CliLabelCatalogSpecs|FullyQualifiedName~CliReferenceDocsSpecs.CliReference_DocumentsWorkflowProfileToggleProfileIdArgument|FullyQualifiedName~CliReferenceDocsSpecs.CliReference_DocumentsCanonicalIssueRerunFromStageFlag"` (121 passed).

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: adjacent documentation for workflow reads
  Evidence: `docs/agent-subscriptions.md:9`, `design/agent-subscriptions.md:51`, `design/agent-subscriptions.md:62`, `design/agent-subscriptions.md:66`, and `design/cli.md:120` still describe `mo workflow show` as the primary lookup command. This does not break the candidate because `show` remains a transitional alias, but it keeps adjacent docs on the old name.
  SuggestedAction: In a documentation cleanup, rewrite those references to use `mo workflow get` and mention `show` only where transitional compatibility matters.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: full CLI test suite / `CliReferenceDocsSpecs`
  Evidence: `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj` fails 3 tests: `CliReference_IssueCommandExamplesUseCurrentCommentAndPrereqSurface` cannot find `mo issue comment add <number>`, `CliReference_OptionNotesDoNotOverstateOutputOrProjectFlags` cannot find the expected agent option-note wording, and `CliReference_DocumentsRealTopLevelCommandGroupsAndCriticalSubcommands` cannot find `mo server start`. These assertions were already present at the merge base and already mismatched the merge-base docs, so this is not introduced by issue 386.
  SuggestedAction: Fix or retire the stale doc assertions in a separate docs-test cleanup, then require the full CLI suite again as a green gate.
  Status: pre-existing

- [ID: item-3]
  Severity: warning
  Scope: `docs/cli-reference.md` vs project workflow implementation
  Evidence: `docs/cli-reference.md:91` through `docs/cli-reference.md:94` advertise `mo project workflow profile get|set|clear|preview`, but `packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs:18` through `packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs:24` register only `profile list|enable|disable`; the get/set/clear/preview commands live under the separate `config` subgroup at `packages/cli/Mohist.Cli/MohistCliCommands.ProjectWorkflow.cs:178`. This mismatch predates the enable/disable addition and is broader than issue 386.
  SuggestedAction: Open a separate CLI-surface issue to either move/alias `config` commands under `profile` or update the product spec and gap table to reflect the current split.
  Status: pre-existing

- [ID: item-4]
  Severity: warning
  Scope: `docs/cli-reference.md` vs agent read command
  Evidence: `docs/cli-reference.md:203` advertises `mo agent get <名或id>`, but `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:159` registers `show` and no `get` alias. This is unrelated to the archive/delete rename implemented by issue 386.
  SuggestedAction: Track as a separate verb-vocabulary cleanup for agent reads.
  Status: pre-existing

<promise>PASS</promise>
