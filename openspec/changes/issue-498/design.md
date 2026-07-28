## Context

`mo issue workflow` currently registers `status` and `timeline` reads, although WorkflowRun detail is already read through `mo run view` with a Run ID or `--issue` selector. The Issue subarea also implies that Workflow Profile selection lives there, while the actual selection options belong to `mo issue create/edit`.

`mo run` already centralizes target validation and resolution in `RunCommands`: it rejects missing or ambiguous targets locally, resolves an Issue to its bound Run, and reads `GET /api/workflow-runs/{id}`. That detail response contains the workflow status and ordered stages plus an `issueRef` for the associated Project and Issue. The legacy timeline leaf requests an unmapped Issue route, while the existing Run detail renderer already presents that stage progression. The change is CLI and documentation only; the server remains the authority for WorkflowRun state.

Stakeholders are CLI users and external Agents that discover commands through group and leaf help, and maintainers guarded by CLI command-tree and HTTP-contract tests.

## Goals / Non-Goals

**Goals:**

- Make `mo run view` the single CLI surface for WorkflowRun status, detail, and ordered stage progression.
- Retire `mo issue workflow` completely and align help and `docs/cli-reference.md` with the resulting ownership boundary.
- Preserve Issue Workflow Profile selection on `mo issue create/edit`.

**Non-Goals:**

- Change WorkflowRun, Issue, or Workflow Profile state semantics.
- Add a server endpoint, alter API DTOs, or change runner or web behavior.
- Preserve the retired Issue commands as aliases or compatibility shims.
- Add a `mo run timeline` command or any new timeline output format.

## Decisions

### Retire timeline instead of duplicating Run detail

`RunCommands.RegisterReads` will retain `list`, `view`, and `watch`; it will not register `timeline`. `mo run view` already resolves either a Run ID or `--issue` and renders the same ordered stages that a timeline command would need to display.

The legacy `issue workflow timeline` leaf is not migrated: it targets an unmapped server route and has no independent, working information or output contract to preserve. Removing it avoids moving a duplicate or nonfunctional read beneath `run`.

Alternative considered: add `run timeline` backed by WorkflowRun detail. This would create a second command for the same ordered stages as `run view`, violating the single canonical-path rule without supplying a distinct user need or output contract.

### Remove the Issue workflow command registration and implementation

`IssueCommands.Build` will no longer add `BuildWorkflow`, and the obsolete implementation file will be removed. Unknown-command handling will therefore reject the retired `issue workflow status` and `issue workflow timeline` forms with the standard local usage failure before HTTP work begins.

Alternative considered: retain the group as a deprecation alias. This conflicts with the CLI's single canonical path rule, keeps misleading help discoverable, and prolongs duplicated command-tree tests.

### Keep presentation and contract tests local to the CLI

The Run read test suite will retain coverage for `view` targeting by Run ID and Issue, including missing and ambiguous targets. Existing tests for the retired Issue workflow leaves will be deleted or replaced with assertions that the commands are unknown and issue no HTTP request; `run timeline` will receive the same unknown-command assertion. Group-help tests will assert that `run` lists `view` but not `timeline`, and `issue` does not list `workflow`.

`docs/cli-reference.md` will remove the issue-498 implementation-gap entry after the command tree matches the specification. The Run command map remains centered on `view`, and the Workflow Profile guidance remains unchanged because it already names `issue create/edit` as the selection surface.

Alternative considered: add server integration coverage. No server route or behavior changes, so CLI tests using the existing recording HTTP boundary are the lowest useful layer for this command-surface change.

## Risks / Trade-offs

- [A future distinct timeline need emerges] -> Establish a user-visible purpose, response shape, and output contract before adding another Run read; do not infer one from the existing stage table.
- [Consumers still invoke retired Issue commands] -> Return the standard unknown-command usage error immediately; no alias hides the migration.
- [Documentation and help drift again] -> Lock the command tree and group-help text with CLI tests, and update the command map in the same change.

## Migration Plan

1. Remove the Issue workflow registration, implementation, and obsolete command tests.
2. Add unknown-command coverage for the retired Issue leaves and for `run timeline`.
3. Update command-tree/help tests and remove the CLI reference gap note.
4. Run the CLI test suite.

This is a CLI surface removal with no persisted state or server migration. Rollback restores the removed Issue workflow registration and tests; it does not require data repair or runner coordination.

## Open Questions

None. The existing WorkflowRun detail DTO already provides the only stage-progression read required by this change.
