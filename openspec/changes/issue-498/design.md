## Context

`mo issue workflow` currently registers `status` and `timeline` reads, although WorkflowRun detail is already read through `mo run view` with a Run ID or `--issue` selector. The Issue subarea also implies that Workflow Profile selection lives there, while the actual selection options belong to `mo issue create/edit`.

`mo run` already centralizes target validation and resolution in `RunCommands`: it rejects missing or ambiguous targets locally, resolves an Issue to its bound Run, and reads `GET /api/workflow-runs/{id}`. That detail response contains the workflow status and stage timeline plus an `issueRef` for the associated Project and Issue. The change is CLI and documentation only; the server remains the authority for WorkflowRun state.

Stakeholders are CLI users and external Agents that discover commands through group and leaf help, and maintainers guarded by CLI command-tree and HTTP-contract tests.

## Goals / Non-Goals

**Goals:**

- Make `mo run` the single CLI surface for WorkflowRun status/detail and timeline reads.
- Add `mo run timeline` with the same Run ID or `--issue <number>` targeting contract as other Run-specific reads.
- Retire `mo issue workflow` completely and align help and `docs/cli-reference.md` with the resulting ownership boundary.
- Preserve Issue Workflow Profile selection on `mo issue create/edit`.

**Non-Goals:**

- Change WorkflowRun, Issue, or Workflow Profile state semantics.
- Add a server endpoint, alter API DTOs, or change runner or web behavior.
- Preserve the retired Issue commands as aliases or compatibility shims.
- Extend the timeline's information or introduce a new output format.

## Decisions

### Register timeline as a Run read

`RunCommands.RegisterReads` will register a `timeline` leaf beside `list`, `view`, and `watch`. It will use the existing Run ID argument, `--issue`, Project options, and target-shape validation.

The command will resolve the target through the existing Run resolver. For a Run ID, it will read the existing WorkflowRun detail resource; for `--issue`, it will first resolve the Issue's bound Run and then read that same resource. The timeline presentation will be derived from the returned workflow status and ordered stages, preserving the information currently exposed as the workflow timeline.

Alternative considered: keep an Issue-scoped timeline request behind a new `run timeline` wrapper. This retains the old resource ownership and requires separate Run-to-Issue routing, creating another path that can diverge from `run view`. Reusing the WorkflowRun detail read keeps Run reads on one authoritative response shape and requires no server change.

### Remove the Issue workflow command registration and implementation

`IssueCommands.Build` will no longer add `BuildWorkflow`, and the obsolete implementation file will be removed. Unknown-command handling will therefore reject the retired `issue workflow status` and `issue workflow timeline` forms with the standard local usage failure before HTTP work begins.

Alternative considered: retain the group as a deprecation alias. This conflicts with the CLI's single canonical path rule, keeps misleading help discoverable, and prolongs duplicated command-tree tests.

### Keep presentation and contract tests local to the CLI

The Run read test suite will cover timeline targeting by Run ID and Issue, including request ordering, missing/ambiguous targets, and the rendered timeline data. Existing tests for the retired Issue workflow leaves will be deleted or replaced with assertions that the commands are unknown and issue no HTTP request. Group-help tests will assert that `run` lists `timeline` and `issue` does not list `workflow`.

`docs/cli-reference.md` will add `timeline` to the Run command map and remove the issue-498 implementation-gap entry after the command tree matches the specification. The Workflow Profile guidance remains unchanged because it already names `issue create/edit` as the selection surface.

Alternative considered: add server integration coverage. No server route or behavior changes, so CLI tests using the existing recording HTTP boundary are the lowest useful layer for this command-surface change.

## Risks / Trade-offs

- [Timeline and detail output need different human-readable layouts] -> Keep the timeline projection in the CLI renderer while sourcing it from the shared WorkflowRun detail DTO; JSON or API contracts do not multiply.
- [Consumers still invoke retired Issue commands] -> Return the standard unknown-command usage error immediately; no alias hides the migration.
- [Documentation and help drift again] -> Lock the command tree and group-help text with CLI tests, and update the command map in the same change.

## Migration Plan

1. Add `mo run timeline` using the shared Run target resolver and existing WorkflowRun detail response.
2. Remove the Issue workflow registration, implementation, and obsolete command tests.
3. Update command-tree/help tests and the CLI reference command map and gap note.
4. Run the CLI test suite.

This is a CLI surface removal with no persisted state or server migration. Rollback restores the removed Issue workflow registration and tests; it does not require data repair or runner coordination.

## Open Questions

None. The existing WorkflowRun detail DTO provides the Run status, ordered stages, and Issue reference required for the CLI-only migration.
