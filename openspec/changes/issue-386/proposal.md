## Why

The CLI command surface drifts from the product spec (`docs/cli-reference.md`) in
a handful of small, independent spots: a read verb that should be `get` is `show`;
a redundant command duplicates what `get` already renders; a state-changing server
capability (profile enable/disable) has no CLI entry (violating `conventions.md`
Tier 2); one lifecycle variant is a standalone command where the spec wants a flag;
and two resources use the wrong verb for delete-vs-archive. Each drift is local and
low-risk, but together they keep the cheat-sheet and `mo --help` out of sync with
the documented contract. This issue converges them in one pass so the verb
vocabulary (`get`/`archive`/`delete`) and the "variants are flags" principle
(`design/cli.md`) actually hold across the command surface.

## What Changes

- **`mo workflow get <runId>` is the canonical read; `show` becomes an alias.**
  Today `show` is the command (`MohistCliCommands.Workflow.Reads.cs:26`) and `get`
  does not exist. The spec verb for "fetch one resource" is `get`. `show` is kept
  as a transitional alias so scripts written against the just-landed #381 surface
  keep working. The `show -o yaml` template-definition contract is unchanged.
- **`mo workflow status` is removed.** It renders a strict compact subset of the
  same GET as `get`; `get`'s default table output already is the summary view
  (`-o json`/`yaml` is the full resource). `status` is a redundant command, not a
  rename, so it is deleted (optionally with a one-line "use `mo workflow get`"
  transition hint). **BREAKING** for any caller of `mo workflow status`, mitigated
  by it being a brand-new command from #381 with a narrow usage surface.
- **New `mo project workflow profile enable <profileId>` / `disable <profileId>`.**
  The server already exposes `POST /workflow-profile/{enable,disable}`
  (`ProjectRoutes.cs:278,299`, body `{ profileId }`, last-enabled guard on
  disable) but the CLI has no entry — a Tier 2 gap, since toggling a profile's
  enabled state is state-changing. These hit the existing endpoints; no server
  change.
- **`mo issue rerun <num> --from-stage <stage>` replaces the standalone
  `rerun-from-stage` command.** Today `mo issue rerun` (plain, from start) and
  `mo issue rerun-from-stage --stage` (`Issue.Lifecycle.cs:80`) are two peer
  commands. The workflow side already converged to `mo workflow rerun --from-stage`
  in #381; the issue side was missed. `rerun` gains a `--from-stage` flag (routing
  to `.../rerun-from-stage` when present, `.../rerun` when absent), and
  `rerun-from-stage` becomes a transitional alias.
- **`mo agent archive` is the canonical name; `delete` becomes an alias.** The
  command's own description is "Archive an agent", its output says "archived", and
  the server method is `ArchiveAsync` — only the command name says `delete`
  (`Agent.cs:315`). Renamed to `archive`; `delete` kept as a transitional alias.
- **`mo label delete` is the canonical name; `remove`/`rm` become aliases.**
  Today `remove` is canonical with `rm` aliased (`Label.cs:204`). The spec verb
  for hard-delete is `delete`; the canonical/alias pair is flipped so `delete` is
  primary and both `remove` and `rm` are aliases (same behavior, no semantic
  change).
- **Alias-transition strategy is uniform:** every *rename* keeps the old name as a
  transitional alias (`show`, `delete` for agent, `remove`/`rm` for label,
  `rerun-from-stage`); the one *redundant* command (`status`) is removed, not
  aliased, because it duplicates `get`'s data rather than renaming it. The
  decision is recorded in an issue comment per the acceptance criteria.
- **`docs/cli-reference.md` implementation-gap table updated:** the rows for
  these six items are removed once landed.

## Capabilities

- `workflow-run-reads`: The `mo workflow` read surface — `get` is the canonical
  single-resource read (with `-o table|json|yaml`, default table = summary,
  `-o yaml` = template definition); `show` is a transitional alias of `get`; the
  redundant `status` command is gone (its compact view folded into `get`'s default
  table output). Covers items 1 and 2.
- `workflow-profile-toggle`: New `mo project workflow profile enable|disable
  <profileId>` CLI entry points for the existing `POST /workflow-profile/{enable,
  disable}` endpoints — including the required `profileId` argument, project
  scoping via `--project`/`--project-id`, and faithful surfacing of the
  `last_enabled_workflow_profile` / `unknown_workflow_profile` server errors.
  Covers item 3.
- `issue-rerun`: `mo issue rerun <num>` accepts `--from-stage <stage>` (routing to
  `.../rerun-from-stage` with the stage body when present, `.../rerun` when
  absent); the standalone `rerun-from-stage` command is a transitional alias of
  `rerun --from-stage`. Covers item 4.
- `agent-archive`: `mo agent archive <name-or-id>` is the canonical name for the
  existing archive-behavior; `delete` is a transitional alias with identical
  behavior (name-only flip, no semantic change). Covers item 5.
- `label-delete`: `mo label delete <key>` is the canonical name; `remove` and `rm`
  are aliases with identical behavior (canonical/alias flip, no semantic change).
  Covers item 6.

## Impact

- **CLI (`packages/cli/Mohist.Cli/`)** — all changes local:
  - `MohistCliCommands.Workflow.Reads.cs`: rename `BuildShow` command name to `get`
    with `show` alias; delete `BuildStatus` (and its `WorkflowRunStatus` shape
    reference); drop `BuildStatus` from the group in `MohistCliCommands.Workflow.cs`.
  - `MohistCliCommands.ProjectWorkflow.cs`: add `BuildProfileEnable` /
    `BuildProfileDisable` to the `profile` subgroup (today it only registers
    `list`), POSTing `{ profileId }` to the existing endpoints.
  - `MohistCliCommands.Issue.Lifecycle.cs`: replace generic `BuildAction("rerun")`
    registration with a dedicated `BuildRerun` carrying `--from-stage`
    (mirrors `WorkflowCommands.BuildRerun`); convert `BuildRerunFromStage` into an
    alias of `rerun --from-stage`.
  - `MohistCliCommands.Agent.cs`: rename `BuildDelete` command to `archive`, add
    `delete` alias.
  - `MohistCliCommands.Label.cs`: rename `BuildRemove` command to `delete`, add
    `remove` alias (keep existing `rm`).
- **Server**: no change. `POST /workflow-profile/{enable,disable}` and the rerun
  endpoints already exist; the CLI only gains callers.
- **Docs**: `docs/cli-reference.md` gap-table rows for these six items removed on
  completion; no change to the spec body (it already documents the target shape).
- **Tests** (`packages/cli/tests/Mohist.Cli.Tests/`): new/updated specs per
  `design/testing.md` — faked HTTP via `RecordingHttpHandler`, no real external
  dependencies, no wall clock. Affected files: `CliWorkflowReads.cs` (get+alias,
  status gone), `CliProjectWorkflowProfileSpecs.cs` (enable/disable),
  `CliIssueRerunFromStageSpecs.cs` (rename/extend for `rerun --from-stage`),
  `CliAgentCommandSpecs.cs` (archive+alias), `CliLabelCatalogSpecs.cs`
  (delete+aliases).
- **Risk**: low — name-only flips and one redundant-command removal; new
  profile-toggle commands are additive callers of an existing endpoint. Largest
  blast radius is `status` removal (BREAKING), softened by its recency (#381) and
  optional transition hint.
