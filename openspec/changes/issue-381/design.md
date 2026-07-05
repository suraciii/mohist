## Context

WorkflowRun is the core-domain aggregate root (`design/domain-analysis.md:22-29`,
"WorkflowRun decides"), keyed by `workflowRunId` (`design/conventions.md:79`,
`ResourceKey = /workflow-runs/{workflowRunId}`). Two consumers cannot reach it
through the issue-number alias that every existing workflow command requires:

1. **Agent event-subscription handlers** receive a `workflowRunId` in the
   CloudEvent envelope and have no issue number. `design/agent-subscriptions.md:51`
   declares `mo workflow get <runId>` — returning run detail **including the
   associated issue (number + title)** — a hard prerequisite, and names the
   broader `mo workflow` suite as the "待建" follow-up (`agent-subscriptions.md:86,88`).
2. **Scripts / operators** that already hold a run id must today reverse-resolve
   to an issue number just to act on the run that is, by definition, the thing
   they are addressing.

Meanwhile the name `workflow` is overloaded: `mo workflow list` lists
WorkflowProfiles (project configuration), which blocks the natural home for
WorkflowRun commands and forces `docs/cli-reference.md:261` to keep patching
the distinction.

### Current state of the surfaces

**CLI** (`packages/cli/Mohist.Cli/`): `WorkflowCommands.Build`
(`MohistCliCommands.Workflow.cs:8`) returns a single `list` subcommand (91
lines) that hits `/api/workflow-profiles` (`--described`) or
`/api/workflow-templates/system` (plain). `mo project workflow`
(`MohistCliCommands.ProjectWorkflow.cs:9`) already hosts peer `template`
(`:564-575`) and `config` (`:17-25`) subgroups — the natural home for a
relocated `profile` subgroup. All `mo issue` workflow shortcuts
(approve/reject/retry/rerun/rerun-from-stage/resume/force-stop/stop) live in
`MohistCliCommands.Issue.Lifecycle.cs` and POST to
`/api/projects/{projectId}/issues/{number}/{verb}`. Shared output/error
plumbing is centralized in `MohistCliApi.Print*WithOutputAsync`
(`MohistCliApi.cs:727-768`); table shapes dispatch through `TableRenderer`
(`TableRenderer.cs:35`).

**Server** (`packages/server/src/Mohist.Server/`): the
`/api/workflow-runs/{workflowRunId}` surface (`WorkflowRoutes.cs:10-113`)
covers only subresources today — `yaml`, `variables/effective[/{keyPath}]`,
`tasks`, `tasks/batch`, `workflow-profile[/variables]`, plus `events`
(`WorkflowEventRoutes.cs:31`) and `sessions[/{name}]`
(`WorkflowSessionRoutes.cs:10-17`). There is **no bare `GET`** for a
show/status read model, and **no control POSTs** — all 8 control verbs live
issue-scoped under `/api/projects/{projectRef}/issues/{number}/...`
(`IssueRoutes.WorkflowControl.cs`), funneled through
`ResolveWorkflowControlAsync` (`:204-227`) which maps
`issue → workflowRunId → IWorkflowGrain.<method>` behind a private
`WorkflowControlAction` guard (`ActiveOnly` vs `RetryOrRerun`, `:255-259`).

### The central tension

The issue's stated Non-Goal — "don't change server endpoints — surface is
complete" — is in direct tension with the acceptance criteria for `show`,
`status`, and the 8 control commands: **the workflow-run-scoped surface those
commands need does not exist**. There is no bare GET, no control POSTs, and
no read model joining a run to its associated issue (`WorkflowStatusView` is
explicitly guaranteed not to carry issue fields — enforced by
`tests/.../Workflow/Grain/StatusSpecs.cs:129`). This design rules on that
tension.

## Goals / Non-Goals

**Goals:**

- Deliver `mo workflow <control> <runId>` for all 8 state-changing verbs,
  addressing runs directly by id and invoking the **same `IWorkflowGrain`
  methods + state guards** as the issue shortcuts — no CLI-side
  reverse-resolution.
- Deliver `mo workflow <read> <runId>`: `show` (full resource, `-o yaml`
  carries the template definition, response includes associated issue number +
  title), `status` (compact projection), `variables` (subresource),
  `events`/`list-sessions` (associated resources).
- Relocate `mo workflow list` (WorkflowProfile) → `mo project workflow profile
  list` so `mo workflow` owns a single concept (WorkflowRun).
- Create `design/cli.md` recording (a) naming ownership and (b) the
  output-format-vs-subresource-vs-associated-resource principle.
- Update `docs/cli-reference.md`; mark the old path migrated.
- Close the `agent-subscriptions.md` "前置依赖 mo workflow 命令套件" gap.

**Non-Goals:**

- Single-session sub-actions (`show`/`transcript`/`compact`/`reset`/`followup`)
  get **no** workflowRunId entry — stay issue-scoped, deferred.
- Refactor `mo issue workflow` or converge the full verb vocabulary.
- Touch `mo project workflow template/config`.
- Redesign task injection's server-side guard model (see Decision 5).

## Decisions

### Decision 1 — Control commands backed by NEW workflow-run-scoped POST endpoints (not CLI-side reverse-resolution)

**Choice:** Add new `POST /api/workflow-runs/{workflowRunId}/<action>` endpoints
for the 8 control verbs, mirroring the issue-scoped endpoints' grain calls and
state guards. The CLI calls these directly — it does **not** reverse-resolve
`runId → issue` to hit the existing issue routes.

**Rationale:**

- **Matches the domain.** WorkflowRun is the aggregate root; the action's true
  subject is the run. `conventions.md:100` explicitly says routes that still
  receive an issue number should resolve to `issueId` at the boundary — the
  boundary here is the run.
- **Avoids the anti-pattern this issue exists to fix.** Forcing the CLI to do
  `runId → issue → issue-endpoint` would embed the very reverse-resolution the
  issue removes from consumers, just hidden inside the CLI. It would also
  require the issue to be `in_progress` (the issue-scoped guard at
  `IssueRoutes.WorkflowControl.cs:215-216`), which can legitimately diverge
  from run controllability.
- **Single guard implementation.** The `IsWorkflowControllableForAction`
  logic (`IssueRoutes.WorkflowControl.cs:229-236`) and the
  `WorkflowControlAction` enum are extracted to a shared helper keyed by
  `workflowRunId`, so the new endpoints and the issue endpoints share one
  referee.

**Alternatives considered:**

- **(Rejected) CLI-side resolution against existing issue endpoints.** Smaller
  server footprint, but violates the "no reverse-resolution" product contract,
  couples run-controllability to issue-row status, and makes the CLI
  non-trivially stateful. Rejected on those grounds.
- **(Rejected) Expose `IWorkflowGrain` calls with no HTTP layer.** Would break
  the CLI↔Server boundary (`architecture.md:45,250`) and the
  execution-fact/state-referee separation (`architecture.md:101-108`).

**Endpoint shape:** `POST /api/workflow-runs/{workflowRunId}/{approve|reject|
retry|rerun|rerun-from-stage|resume|pause|stop}` with the same request bodies
the issue routes use (`{}` for most, `{ message }` for reject, `{ stage }` for
rerun-from-stage) and the same response/error-code mapping
(`unknown_stage`/`stage_not_reached`/`active_work_in_range`/`session_context_exhausted`).
Verb mapping to grain methods: `approve`→`ApproveAsync`, `reject`→`RequestChangesAsync`,
`retry`→`RetryAsync`, `rerun`→`RerunAsync`, `rerun-from-stage`→`RerunFromStageAsync`,
`resume`→`ResumeAsync`, `pause`→`PauseAsync`, `stop`→`StopAsync` (matches
`IWorkflowGrain.cs:11-19`).

**Naming note:** The server keeps the `rerun` / `rerun-from-stage` split as two
endpoints (minimal divergence from the issue-scoped pattern). The
`rerun --from-stage` collapse is a **CLI-layer** affordance only (Decision 4).

### Decision 2 — New `GET /api/workflow-runs/{workflowRunId}` returns a detail read model that joins the associated issue

**Choice:** Add one bare GET returning a new `WorkflowRunDetailDto` that wraps
`WorkflowStatusView` (`WorkflowViews.cs:13-22`) and bolts on an **issue ref**
(number + title), reverse-resolved via the existing indexed lookup
`IssueQuerier.GetIssueIdForWorkflowRunAsync` (`IssueQuerier.cs:92-102`).

**Rationale:**

- The `agent-subscriptions.md:51,82` hard prerequisite requires the read model
  to carry the associated issue so a handler holding only a `workflowRunId`
  needs zero follow-up lookups.
- `WorkflowStatusView` is today **guaranteed** not to carry issue fields
  (`StatusSpecs.cs:129`). Rather than weaken that invariant, the new DTO
  composes the view and adds the issue ref alongside — preserving the existing
  domain boundary while satisfying the read contract.
- Precedent: `IssueWorkflowStatus` (`IIssueGrain.cs:27-36`) already joins issue
  fields + workflow view as a grain-internal DTO;
  `WorkflowActiveWorkView`/`WorkflowFeedbackRecord` already carry
  `IssueId`/`IssueNumber` (`agent-subscriptions.md:84`). The join pattern is
  established; this surfaces it over HTTP.

**`-o yaml` contract:** `show -o yaml` renders the workflow template-definition
YAML. The CLI fetches `GET .../yaml` (`WorkflowRoutes.cs:12`, already exists)
and renders it; no new server endpoint is needed for the YAML rendering, and
**no `mo workflow yaml` command is created** (Decision 3).

### Decision 3 — `status` is a CLI-side compact projection of the `show` response; no second endpoint

**Choice:** Both `show` and `status` hit the single `GET /api/workflow-runs/{id}`
from Decision 2. `status` renders a compact table (current stage, run status,
stage progress at a glance); `show` renders the full resource. The server
exposes one read endpoint.

**Rationale:**

- Minimizes server surface (one GET, not two).
- The spec requires `show` to be "a strict superset of the status view" —
  trivially true when both render the same payload.
- The workflow state machine is more complex than a single-phase workload, so a
  dedicated compact **view** stays valuable — but a view is a rendering concern,
  not a resource concern, so it lives in the CLI (`TableRenderer`).

**Alternatives considered:**

- **(Rejected) Separate `GET .../status` endpoint.** Fragments the resource and
  duplicates server query logic for a rendering-only difference. Rejected.

### Decision 4 — `rerun --from-stage` is a CLI-layer collapse; server keeps two endpoints

**Choice:** `mo workflow rerun <runId>` POSTs to `.../rerun`; `mo workflow rerun
<runId> --from-stage <s>` POSTs to `.../rerun-from-stage`. The user sees one
command with a flag; the server sees two endpoints (mirroring the existing
issue-scoped split). A blank/whitespace `--from-stage` is rejected locally with
no request, matching `BuildRerunFromStage` validation
(`Issue.Lifecycle.cs:103-107`).

**Rationale:** Follows `design/cli.md`'s "compound prefers flag" guidance (one
less command, one more flag) without forcing a server-side endpoint collapse
that would diverge from the established issue-scoped pattern.

### Decision 5 — Task injection (`mo workflow add-task`) is **deferred** with rationale; Tier 2 acknowledged

**Choice:** Do **not** add `mo workflow add-task` / `add-tasks` in this change.
Record the Tier adjudication: `AddTask`/`AddTasks` are state-changing and
technically Tier 2 mandatory (`conventions.md:155,162-164`), but are **deferred
to a follow-up issue**.

**Rationale for deferral:**

- **Unguarded today.** `POST /api/workflow-runs/{id}/tasks[/batch]`
  (`WorkflowRoutes.cs:39-79`) key the grain directly with **no**
  `IsWorkflowControllableForAction` check — only `id`/`title` validation. Wiring
  a `mo` command onto an unguarded state-changing endpoint would amplify an
  existing gap; the guard design belongs in the same unit that adds the command.
- **Different conceptual group.** The 8 control verbs are lifecycle-referee
  actions on the run (admit/reject/retry/stop). Task injection is a
  stage-internal work-protocol operation (the runner/agent contract), not a
  run-lifecycle action. Bundling it would muddy the "control group = state
  referee" coherence this change establishes.
- **Not on the critical path.** The `agent-subscriptions.md` prerequisite
  requires reads + control, not task injection. Deferral does not block any
  named consumer.
- **Open question logged** (see Open Questions): whether the task-injection
  endpoints should inherit the `WorkflowControlAction` guard before getting a
  CLI command.

**Alternative considered:** Ship `mo workflow add-task` now, accepting the
guard gap. Rejected — exposing an unguarded state mutation through a
first-class CLI command without a guard decision is the wrong default.

### Decision 6 — Profile relocation is a mechanical move; the old path is not aliased

**Choice:** The 91-line profile-listing logic in `WorkflowCommands.BuildList`
(`MohistCliCommands.Workflow.cs:15-90`) moves verbatim into a new
`BuildProfile` subgroup under `ProjectWorkflowCommands` (peer to `template` and
`config`, added at `MohistCliCommands.ProjectWorkflow.cs:11-12`). Same flags
(`--described`, `--project`/`--project-id`, `-o`), same degraded-fallback and
conflict behavior, same `TableShape` members (none new). The old `mo workflow
list` path is **removed**, not aliased.

**Rationale:**

- Aliasing would perpetuate the exact overload this change eliminates. The spec
  requires `mo workflow` to **not** expose a profile `list` subcommand
  (`workflow-profile-relocation/spec.md:9`).
- This is the **only** command-path break in the change; it is documented in the
  release/changelog and in `docs/cli-reference.md` as a migration
  (`workflow-profile-relocation/spec.md:69-82`).

**Rollback:** if the break proves disruptive post-release, a thin compat shim
command can be added later without re-doing the relocation — but it is not
shipped now.

### Decision 7 — `design/cli.md` records the two durable principles

**Choice:** Create `design/cli.md` stating (a) **naming ownership** — `mo
workflow` denotes WorkflowRun (core-domain aggregate root); WorkflowProfile
lives under `mo project workflow profile` because a profile is project-owned
configuration, and sub-resources hang under the parent that owns them; and (b)
**output format never creates a command**, with output-format / subresource /
associated-resource as three non-mixable categories, citing `show -o yaml` as
the canonical example. Add an index entry under `design/README.md`'s "支撑主题"
section.

**Rationale:** These two principles are load-bearing for *future* CLI work
(every later `mo <resource>` design will be asked "why isn't this an output
format?" / "where does this sub-resource live?"). Recording them once, durably,
is cheaper than re-litigating per command.

### Decision 8 — CLI construction follows the existing shared-helper pattern; one new table shape

**Choice:** New commands are built with the existing option helpers
(`MohistCliCommands.OutputOption()`, `StageOption()`, etc.) and call
`api.PrintWithOutputAsync` / `api.PrintPostWithOutputAsync`
(`MohistCliApi.cs:727,733`). Path helper
`WorkflowRunPath(runId, suffix)` → `/api/workflow-runs/{Escape(runId)}{suffix}`
mirrors `ProjectIssuesPath` (`Issue.cs:47-52`). Add one `TableShape` member
(`WorkflowRunDetail`) for `show`/`status`; `events`/`list-sessions`/`variables`
reuse or lightly extend existing shapes. `reject` reuses the
`Issue.Lifecycle.cs:34-78` `--message` validation; `rerun --from-stage` reuses
the `:80-115` `--stage` validation. No new shared infrastructure is needed.

## Risks / Trade-offs

- **[Server surface growth]** The change adds 8 control POST endpoints + 1 bare
  GET, contradicting the issue's literal Non-Goal. -> **Mitigation:** the
  endpoints are additive (no existing route changes), share one extracted guard
  helper with the issue routes, and are the only way to honor the
  "direct-addressing by workflowRunId" product contract. The Non-Goal is
  re-scoped here as: "don't alter the *existing* `/api/workflow-runs/{id}`
  subresource endpoints" — which holds.

- **[Guard-logic duplication]** Extracting `IsWorkflowControllableForAction`
  risks behavior drift between the issue-scoped and run-scoped paths.
  -> **Mitigation:** extract to a single shared method keyed by
  `workflowRunId`; both endpoint families call it. Add a spec asserting the two
  paths admit/reject the same runs for the same reasons.

- **[Associated-issue join freshness]** Reverse-resolving the issue from a run
  reads the issue read-model; a run whose issue row is transiently missing
  yields a run detail with a null issue ref. -> **Mitigation:** `show` renders
  issue fields as absent (not an error) when the join misses; the run identity
  and status remain authoritative. Documented in the read-model contract.

- **[Profile-relocation break]** Any script calling `mo workflow list` breaks.
  -> **Mitigation:** this is the only path break; called out in changelog and
  `docs/cli-reference.md`; the old command was 91 lines and lightly used
  (profile listing is an admin/config operation, not a high-frequency script
  target). No alias shipped (Decision 6) to avoid perpetuating the overload.

- **[Task-injection guard gap persists]** Deferring `mo workflow add-task`
  leaves an unguarded state-changing HTTP endpoint reachable by direct HTTP.
  -> **Mitigation:** the endpoint already exists and is already reachable today;
  this change neither widens nor narrows that exposure. The gap is logged as an
  Open Question for the follow-up issue.

- **[`status` vs `show` payload cost]** Both hit the same GET; `status` fetches
  the full detail even though it renders a subset. -> **Mitigation:** the detail
  model is small (status + stage progress + issue ref); the template-definition
  YAML is fetched lazily only for `-o yaml`. Bandwidth is not a concern at the
  expected call frequency. Revisit if telemetry shows otherwise.

## Migration Plan

**Deploy order (all in one release; no schema migration, no irreversible
action):**

1. **Server:** add `GET /api/workflow-runs/{id}` (detail read model + issue
   join) and the 8 `POST /api/workflow-runs/{id}/<action>` control endpoints;
   extract the shared `WorkflowControlAction` guard helper. Ship first so the
   CLI has something to call.
2. **CLI:** relocate profile listing to `mo project workflow profile list`;
   rewrite `WorkflowCommands.Build` into the WorkflowRun command group
   (8 control + 5 read); add the `WorkflowRunDetail` table shape.
3. **Docs:** create `design/cli.md`; update `docs/cli-reference.md` (new
   surface + migration note for the old `mo workflow list` path); mark the
   `agent-subscriptions.md` prerequisite satisfied.
4. **Tests:** CLI specs via `RecordingHttpHandler`/`CliTestHarness.CreateSync`
   under `packages/cli/tests/Mohist.Cli.Tests/` (control, reads, profile
   relocation); server specs for the new endpoints under
   `Specs/Workflow/Api/`. No real net/process/time per `design/testing.md`.
5. **Release note:** call out the single command-path break
   (`mo workflow list` → `mo project workflow profile list`) and the new
   `mo workflow <control|read> <runId>` surface.

**Rollback:** revert CLI + server commits; the additive endpoints and the
relocated path leave no persistent state. No data migration to undo.

## Open Questions

- **Task-injection guard model.** Should `POST .../tasks[/batch]` inherit the
  `WorkflowControlAction` guard (e.g. `ActiveOnly`) before a `mo workflow
  add-task` command ships? Decide in the follow-up issue (Decision 5).
- **`list-sessions` → single-session entry points.** Whether single-session
  sub-actions (`show`/`transcript`/`compact`/`reset`/`followup`) need a
  workflowRunId-direct entry is explicitly deferred — not presumed here.
- **Detail read-model enrichment.** Whether `show` should eventually also carry
  `WorkflowActiveWorkView`/`WorkflowFeedbackRecord` (currently out of scope);
  the MVP carries status + stage progress + approval state + issue ref only.
