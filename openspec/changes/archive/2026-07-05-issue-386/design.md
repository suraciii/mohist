## Context

The CLI command surface (`packages/cli/Mohist.Cli/`) has drifted from the product
spec (`docs/cli-reference.md`) in six small, independent spots. Each drift is
local and low-risk; together they keep `mo --help` and the cheat-sheet out of sync
with the documented verb vocabulary (`get`/`archive`/`delete`) and the
"variants are flags" principle (`design/cli.md`). One item (profile
enable/disable) is a `design/conventions.md` Tier 2 gap: a state-changing server
capability with no CLI entry.

Current state of each spot (verified against source):

- `mo workflow show` is the single-resource read; spec verb is `get`
  (`MohistCliCommands.Workflow.Reads.cs:26`). `status` is a compact-projection
  sibling of the same GET (`...:72`).
- `mo project workflow profile` registers only `list`
  (`MohistCliCommands.ProjectWorkflow.cs:21`); server already exposes
  `POST /workflow-profile/{enable,disable}` — Tier 2 gap.
- `mo issue rerun` is a generic `BuildAction("rerun")` with no `--from-stage`,
  while `mo issue rerun-from-stage --stage` is a peer command
  (`MohistCliCommands.Issue.Lifecycle.cs:80`). The workflow side already
  converged to `mo workflow rerun --from-stage` (`MohistCliCommands.Workflow.cs:170`).
- `mo agent delete` is named `delete` but archives (`...cs:315`, handler prints
  "archived", server is `ArchiveAsync`).
- `mo label remove` is canonical with `rm` aliased (`...cs:204`); spec verb is
  `delete`.

All changes are CLI-only. No server, domain, or grain change. Constraints:
`System.CommandLine` is the parser library; `design/testing.md` forbids real
external dependencies and wall clocks in CLI specs.

## Goals / Non-Goals

**Goals:**

- Align the six command-surface spots with the product spec in one pass.
- Make canonical names match behavior (`get`, `archive`, `delete`) and fold the
  one lifecycle variant into a flag (`rerun --from-stage`).
- Close the Tier 2 gap: every state-changing server entry has a `mo` command.
- Keep scripts written against the just-landed #381 surface working via
  transitional aliases where the change is a rename.

**Non-Goals:**

- Repo double-track merge (#383), install/update entry merge, bare-verb
  rehoming (`status`/`logs`/`use`/`notify`/`info`) — each its own issue.
- Any new domain semantics — every change aligns implementation to existing spec.
- Server-side changes — endpoints already exist or are unchanged.

## Decisions

### D1 — Alias strategy is uniform per *change type*, not per command

Renames keep the old name as a transitional alias; the one *redundant* command is
removed, not aliased. This split is the deciding rule and is recorded once here
rather than re-litigated per item:

- **Renames** (`show`→`get`, agent `delete`→`archive`, label `remove`→`delete`):
  implemented as a single `System.CommandLine.Command` whose constructor takes the
  new canonical name, with `cmd.Aliases.Add("<old>")` for the old name. The handler
  is shared by construction, so the alias CANNOT diverge in behavior, output, or
  exit code. This is the existing pattern (`rm` on label remove, `ls` on list
  commands, `repo` on repository) — no new mechanism.
- **Redundant command** (`workflow status`): hard-delete `BuildStatus` and its
  registration; drop the now-dead `WorkflowRunStatus` table-shape reference. Not
  aliased, because `status` is not a rename of `get` — its compact projection is a
  strict subset of the same GET that `get`'s default table already renders.
  Aliasing it would advertise a redundant verb the spec has retired.

  - *Alternative considered:* keep `status` as an alias printing a "use `get`"
    hint. Rejected — a hint-cmd is neither a true alias (different output) nor a
    clean removal, and it would leave the retired verb discoverable in
    `--help` indefinitely.

### D2 — `mo issue rerun --from-stage` mirrors `mo workflow rerun --from-stage`

Replace the generic `BuildAction("rerun")` registration
(`MohistCliCommands.Issue.cs:21`) with a dedicated `BuildRerun` that mirrors
`WorkflowCommands.BuildRerun` (`MohistCliCommands.Workflow.cs:170`): same
`--from-stage` option, same `fromStageProvided` (presence) check, same routing —
`/rerun` with empty body when absent, `/rerun-from-stage` with `{ stage }` when
present. This keeps the issue-scoped and run-scoped surfaces symmetric, matching
the #381 precedent and the spec scenario for empty `--from-stage` failing locally.

### D3 — `rerun-from-stage` stays a *separate command*, not a name-alias

The standalone `rerun-from-stage` is retained as a transitional alias **in the
product sense**, but it is NOT registered via `cmd.Aliases.Add("rerun-from-stage")`
on the `rerun` command. Reason: the old surface uses `--stage` while the new
surface uses `--from-stage`; a `System.CommandLine` name-alias shares one option
set, so it could not honor both flag names. Instead `BuildRerunFromStage`
(`MohistCliCommands.Issue.Lifecycle.cs:80`) is kept as a peer registration with
its `--stage` flag, already POSTing the same `{ stage }` to the same
`/rerun-from-stage` endpoint that `rerun --from-stage` hits. Both paths converge
on identical wire behavior, satisfying the spec's "alias behaves identically"
scenario without a deprecated-flag pollution on the canonical command.

- *Alternative considered:* unify on one command carrying both `--from-stage` and
  a hidden deprecated `--stage`. Rejected — it bloats the canonical command with a
  deprecated option and complicates the parser configuration for a transitional
  affordance.

### D4 — profile `enable`/`disable` are additive callers of existing endpoints

New `BuildProfileEnable` / `BuildProfileDisable` are added to the `profile`
subgroup (`MohistCliCommands.ProjectWorkflow.cs:18`, which today only registers
`list`). Each takes a required positional `<profileId>`, resolves the project via
the shared `--project` / `--project-id` pair (active project when neither given),
and POSTs `{ profileId }` to the existing
`/api/projects/{projectId}/workflow-profile/{enable,disable}` endpoints. Missing
profile id fails locally before any request. Server guard errors
(`unknown_workflow_profile`, `last_enabled_workflow_profile`) surface verbatim
through the standard `PrintPostAsync` error path — no CLI-side translation, so the
spec's "surface the server error" scenarios hold without bespoke handling.

### D5 — `workflow get` is a rename of `BuildShow`, not a new command

`BuildShow` (`MohistCliCommands.Workflow.Reads.cs:26`) becomes the `get` command
with `show` aliased. Its `-o yaml` template-definition contract (GET `.../yaml`)
is unchanged — only the command name and alias list change. The class-level
comment block describing the read commands is updated so `show` is described as
the alias, not the primary.

### D6 — Tests extend the existing spec files, no new harness

Per `design/testing.md` and the proposal's Impact section, each item gets CLI
specs in its existing file: `CliWorkflowReads.cs` (get+alias, status gone),
`CliProjectWorkflowProfileSpecs.cs` (enable/disable + both guard errors),
`CliIssueRerunFromStageSpecs.cs` (extend for `rerun --from-stage`), and the
agent/label spec files for the name flips. HTTP is faked via the existing
`RecordingHttpHandler`; no real network, no wall clock. Alias-behavior parity is
asserted by running the same scenario under both names and diffing requests.

## Risks / Trade-offs

- **[BREAKING: `mo workflow status` removed]** → Mitigated by its recency (new in
  #381, narrow usage surface) and by the spec recording the removal explicitly.
  Rollforward only; no transition hint emitted (would re-advertise a retired verb).
- **[Transitional aliases never get retired]** → Each rename's alias is a
  deliberate debt. Mitigation: aliases are name-only and behavior-identical, so
  they can be deleted in a later cleanup issue without re-checking semantics. The
  alias decision (D1) is recorded here and in an issue comment per the acceptance
  criteria so a future cleanup has a single inventory.
- **[`rerun-from-stage` retained as a peer command blurs "one command per
  action"]** → Accepted trade-off for the flag-name incompatibility (D3). The
  peer command is marked transitional in its description so `--help` flags it.
- **[Profile enable/disable surface server errors verbatim]** → If the server
  later changes its error codes, CLI tests asserting the code string would break.
  Mitigation: tests assert the documented codes only; any server-side code change
  is itself a coordinated contract change.

## Migration Plan

Single-shot, CLI-only — no coordinated deploy:

1. Apply the six edits in `packages/cli/Mohist.Cli/` (D1–D5).
2. Extend the five CLI spec files (D6); run `dotnet test` in
   `packages/cli/tests/Mohist.Cli.Tests/`.
3. Update `docs/cli-reference.md` implementation-gap table: remove the six rows.
4. Record the alias-strategy decision (D1) in an issue comment per acceptance
   criteria.

**Rollback:** revert the CLI commit; no server or data migration to undo. The
transitional aliases mean external scripts are unaffected on the forward path and
unaffected by a rollback (the old canonical names return).

## Open Questions

- None blocking. The alias-strategy call (renames alias, redundant removes) is
  settled here per the acceptance criteria's "decide and record" clause; the
  per-item outcomes follow deterministically from it.
