## Why

WorkflowRun is the core-domain aggregate root (`design/domain-analysis.md`:
"WorkflowRun decides"), and `/api/workflow-runs/{id}/...` is its RESTful resource
surface — yet the CLI exposes almost none of it. Every workflow control action
and most workflow queries are reachable only as `mo issue <verb> <number>`, where
the issue number is a human-readable alias for the workflowRunId. Two consumers
cannot go through an issue number:

1. **Agent event-subscription handlers** receive a `workflowRunId` in the
   CloudEvent envelope and have no issue number. `design/agent-subscriptions.md`
   (decision 2/3) declares a `mo workflow` command suite a **hard prerequisite** —
   the handler renders only envelope fields, and the Agent must
   `mo workflow get <runId>` to pull context including the associated issue.
2. **Scripts / operators** that already hold a run id must today reverse-resolve
   to an issue number just to act on a run that is, by domain definition, the
   thing they are actually addressing.

At the same time the name `workflow` is overloaded: `mo workflow list` lists
WorkflowProfiles (project configuration), which blocks the natural home for
WorkflowRun commands and forces `docs/cli-reference.md` to keep patching the
distinction.

## What Changes

- **New `mo workflow <control> <runId>` commands** — `approve`, `reject`
  (reason required), `retry`, `rerun` (with `--from-stage <s>` variant),
  `resume`, `pause`, `stop` — that address a WorkflowRun directly by id and
  trigger the **same `IWorkflowGrain` methods** as the existing `mo issue`
  shortcuts. `rerun-from-stage` collapses into `rerun --from-stage` (one less
  command, one more flag).
- **New `mo workflow <read> <runId>` commands** — `show` (full resource via
  `-o table/json/yaml`; the template-definition YAML rides on `show -o yaml`,
  there is **no** separate `yaml` command), `status` (compact summary),
  `variables` (`[--stage <s>] [--key <path>]` — a true subresource),
  `events` (`[--limit <n>]` — associated resource), `list-sessions`
  (associated resource; list only).
- **BREAKING (command path)**: `mo workflow list` (WorkflowProfile) sinks to
  `mo project workflow profile list`. The top-level `mo workflow` group owns
  WorkflowRun. The old path is documented as migrated in
  `docs/cli-reference.md`.
- **New `design/cli.md`** records two decisions: (a) naming ownership —
  `workflow` = WorkflowRun, profile lives under `project`; (b) the principle
  that **output format never creates a command**, and that output-format /
  sub-resource / associated-resource are three distinct categories that must
  not be mixed (no `mo workflow yaml`).
- **Out of scope here**: single-session sub-actions
  (show/transcript/compact/reset/followup) get **no** workflowRunId entry —
  they stay issue-scoped, deferred to a later issue. Task-injection
  (`AddTask`/`AddTasks`) is adjudicated against the conventions Tier rules and
  the conclusion recorded (see Impact).

Non-goals: refactoring `mo issue workflow`; converging the full verb
vocabulary; converging `mo issue sessions` naming; touching
`mo project workflow template/config`.

## Capabilities

- `workflow-run-control`: Direct workflowRunId entry points for the state-changing
  workflow actions, behaving identically to the existing `mo issue` shortcuts —
  same grain method, same active/failed-state guards — so a run can be controlled
  without resolving an issue number.
- `workflow-run-reads`: Direct workflowRunId entry points for read views of a
  WorkflowRun — full resource (`show`), compact status, effective variables
  (subresource), events (associated), sessions list (associated) — governed by
  the output-format-vs-subresource-vs-associated-resource distinction, including
  the `show -o yaml` contract that returns associated-issue context.
- `workflow-profile-relocation`: Sinking `mo workflow list` (WorkflowProfile) to
  `mo project workflow profile list` so the top-level `mo workflow` group can own
  WorkflowRun; recording the naming-ownership and output-format principles in
  `design/cli.md`; migrating `docs/cli-reference.md`.

## Impact

- **CLI (`packages/cli/Mohist.Cli/`)**: `MohistCliCommands.Workflow.cs` is
  rewritten from a single `list` subcommand into the WorkflowRun command group
  (8 control + 5 read commands); the existing profile-list logic (91 lines)
  moves into `MohistCliCommands.ProjectWorkflow.cs` as a new `profile`
  subgroup alongside the existing `template`/`config`; new table render shapes
  in `TableRenderer.*.cs`; top-level `Program.cs` wiring for `workflow`
  unchanged in name, changed in meaning.
- **Server (`packages/server/src/Mohist.Server/Api/`)** — **key tension
  design.md must rule on**: the `/api/workflow-runs/{id}` surface today covers
  `yaml`, `variables/effective`, `events`, `sessions`, `workflow-profile/variables`,
  `tasks` — but it has **no** bare GET (for `show`/`status`) and **no** control
  POSTs. Control actions exist only under
  `/api/projects/{projectRef}/issues/{number}/...`
  (`IssueRoutes.WorkflowControl.cs` → `ResolveWorkflowControlAsync` →
  `IWorkflowGrain.<method>`). The issue's Non-Goal ("don't change server
  endpoints — surface is complete") is therefore in tension with the
  acceptance criteria for `show`/`status` and the 8 control commands. At least
  the read side needs new surface: `design/agent-subscriptions.md` already
  calls for a new workflow detail read model carrying the associated issue
  (number + title). design.md decides whether control is delivered via new
  `/api/workflow-runs/{id}/<action>` endpoints (cleanest "direct addressing") or
  via CLI-side runId→issue resolution against existing issue endpoints.
- **Docs**: new `design/cli.md`; `docs/cli-reference.md` updated for the new
  `mo workflow` surface and the `mo workflow list` →
  `mo project workflow profile list` migration; `design/agent-subscriptions.md`
  prerequisite (mo workflow suite) marked satisfied by this change.
- **Conventions / Tier**: task-injection (`AddTask`/`AddTasks`, already
  `POST /api/workflow-runs/{id}/tasks[/batch]`) is a state-changing entry
  point, so `design/conventions.md` Tier 2 says it **must** have a `mo`
  command; the issue leaves it open. design.md records the conclusion
  (tentatively in-scope as `mo workflow add-task`, or explicitly deferred with
  rationale).
- **Tests** (`packages/cli/tests/Mohist.Cli.Tests/`): new command tests for
  control + read + profile relocation, per `design/testing.md` — faked HTTP,
  no real external dependencies, no wall clock.
- **Risk**: medium — touches the CLI public contract and adds one command-path
  migration; new commands are additive, existing `mo issue` shortcuts remain.
  No schema migration, no irreversible actions; profile relocation is a path
  change that needs a release/changelog note.
