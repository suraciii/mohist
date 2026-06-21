## Context

Issue 214 (`Runner 详情与 active work 上下文`) adds single-runner observability on top of the list/health surface delivered by issue 213. Today:

- `GET /api/projects/{projectRef}/runners` returns a list of `RunnerStatusView` carrying identity, capabilities, and health, but its `ActiveWork` is **singular** and **mostly empty**: `RunnerStatusService.ProjectRunnerAsync` does `runtime?.ActiveWorkflowRunIds.FirstOrDefault()` and builds `new RunnerActiveWorkView(string.Empty, activeWork)` — so only the first workflow run id is exposed, with no work id, stage, title, or issue reference.
- `RunnerRuntimeState` (the grain-level runtime snapshot) only carries `IReadOnlyList<string> ActiveWorkflowRunIds`.
- The full work context already exists in `RunnerGrain._works` as `RunnerTrackedWork.Dispatch` — a `WorkDispatch` carrying `WorkId`, `WorkType`, `Stage`, `Title`, and `Issue` (`WorkIssueRef(ProjectId, IssueId, IssueNumber)`). It is populated at assign time and trimmed on report/loss.
- There is no single-runner detail endpoint, no Web detail page, and no `mo runner show` CLI command.

Constraint (from the issue's Non-Goals and Domain Model): the active-work context MUST come from data already present at dispatch time — no runner→server reporting, no protocol change, no historical persistence, no control actions.

Stakeholders: server (Orleans grains + HTTP API), Web (React), CLI (.NET). Epic 13 (Runner Management).

## Goals / Non-Goals

**Goals:**
- Expose each tracked active work's full context (workId, ownerKind/ownerId, workType, stage, title, issue ref) on both the list and a new single-runner detail surface.
- Surface **all** active works per runner (one per slot), not just the first.
- Add `GET /api/projects/{projectRef}/runners/{runnerId}` returning one runner's full detail.
- Add a Web runner detail page with issue deep-links, reachable from the list.
- Add a `mo runner show <runnerId>` CLI subcommand.

**Non-Goals:**
- No control actions (start/stop/drain/evict/assign).
- No persistence of historical execution records or statistics.
- No change to the register / heartbeat / poll / report wire protocol.
- No real-time log streaming.

## Decisions

### D1 — Source active-work context from `RunnerGrain._works`, not from workflow grains or runner reports

The grain already holds `RunnerTrackedWork.Dispatch` per active work. Expose that payload upward via the runtime snapshot and project it in `RunnerStatusService`.

**Alternatives considered:**
- *Query each `IWorkflowGrain` for stage/title/issue by `workflowRunId` in the projection.* Rejected: turns the list projection into N×M grain fan-out (M runners × N works each), and the workflow grain does not always carry per-work title/issue in a cheap queryable form.
- *Extend the runner heartbeat payload to report active works.* Rejected: violates the explicit non-goal of leaving the dispatch/heartbeat protocol unchanged, and is unnecessary because the server already has the data.

### D2 — Replace `RunnerRuntimeState.ActiveWorkflowRunIds` with a richer `ActiveWorks` list

Change `RunnerRuntimeState` from `(Status, LastHeartbeatAt, IReadOnlyList<string> ActiveWorkflowRunIds)` to `(Status, LastHeartbeatAt, IReadOnlyList<RunnerActiveWorkItem> ActiveWorks)`, where `RunnerActiveWorkItem` carries `WorkId`, `OwnerKind`, `OwnerId` (workflowRunId or agentJobId), `WorkType`, `Stage`, `Title`, `Issue?`. Update `RunnerGrain.GetRuntimeStateAsync` to project from `_works.Values`, and update the two in-tree readers (`RunnerStatusService.ProjectRunnerAsync` and `RunnerGrain.DeriveStatus`'s count derivation) in the same change.

**Trade-off:** `RunnerRuntimeState` is a `[GenerateSerializer]` Orleans record, so this is a grain-call wire-shape change. Per AGENTS.md the project is in active development with no version-compat requirement and a single in-tree caller set, so an in-place replacement is acceptable and preferable to parallel fields that invite drift.

**Alternatives considered:**
- *Add a second grain method `GetActiveWorksAsync()` and leave `GetRuntimeStateAsync` alone.* Rejected: duplicates the snapshot, forces `DeriveStatus` to keep reading the old method for the count, and the two calls would not be atomic against `[Reentrant]` grain turns.
- *Keep `ActiveWorkflowRunIds` and add `ActiveWorks` alongside.* Rejected: redundant; readers must reconcile two lists.

### D3 — `RunnerStatusView.ActiveWork` (singular) → `ActiveWorks` (list); add `Issue` on the item

Rename `RunnerActiveWorkView? ActiveWork` to `IReadOnlyList<RunnerActiveWorkView> ActiveWorks` (empty list, never null), and extend `RunnerActiveWorkView` with `OwnerKind`, `OwnerId`, and `Issue` (`{ projectId, issueId, issueNumber } | null`). Update the Web `RunnerStatusRow` / `RunnerActiveWork` types in lockstep.

**Trade-off:** breaking shape change for the list endpoint. The only consumers are the Web app and tests, both updated in the same change. Cleaner than retaining a singular `ActiveWork` alongside a plural list.

**Alternatives considered:**
- *Keep `ActiveWork` as the first item and add `ActiveWorks`.* Rejected: invites the old code path to drift back to "first only".

### D4 — Detail endpoint at `GET /api/projects/{projectRef}/runners/{runnerId}`, project-scoped

Extend the existing `/api/projects/{projectRef}/runners` route group with a `{runnerId}` segment. Reuse `ProjectResolutionEndpointFilter` and `RunnerStatusService` to resolve one runner by id within the resolved project's eligible set (global + project-scoped). Return 404 with a `runner_not_found` reason when the id is not in the eligible set, so unknown is distinguishable from idle (idle still returns 200 with an empty `ActiveWorks`).

**Alternatives considered:**
- *`/api/runners/{runnerId}` (global, unscoped).* Rejected: runner registries are per-project (`RunnerRegistryKeys.ForProject` + `Global`), and the list is already project-scoped; an unscoped detail would have to probe multiple registries and would not match the list's eligibility semantics.
- *`/api/runner/{runnerId}/status` under the existing runner-management group.* Rejected: that group (`/api/runner/{runnerId}`) carries runner→server traffic (register/heartbeat/poll/report); mixing a server→client read endpoint into it muddies the contract and the auth model.

### D5 — Add `show` to the existing top-level `mo runner` command group

Add `show <runnerId>` to the existing top-level `mo runner` group. Leave the existing service-lifecycle subcommands (install / start / stop / restart / status / logs / uninstall) untouched — those subcommands manage the **locally installed runner service lifecycle**, whereas `mo runner show` queries **runtime state against a remote server** via the new detail endpoint.

**Trade-off:** one command group now contains both service-lifecycle and observability verbs. Mitigated by clear help text and disjoint subcommand names.

**Alternatives considered:**
- *`mo server runner show <runnerId>`.* Rejected: this namespace does not exist in the current CLI and would be verbose.
- *`mo runners show` (plural).* Considered to mirror the HTTP resource `/runners`, but the rest of the CLI uses singular top-level nouns (`mo issue`, `mo epic`); keep `runner` singular for consistency.

Project resolution follows the CLI's standard rules (current project, `--project` override), matching `mo issue show`.

### D6 — Web detail page at `/:projectName/runners/:runnerId`, list rows become links

Add a project-scoped route for the runner detail page. The list's `RunnerRow` becomes a `<Link>` (or `<a>`) wrapping the existing summary content; no summary fields are removed. The detail page renders identity, capabilities, each active work as an independent row (stage, title, work type, work id), and health metrics.

**Alternatives considered:**
- *Global `/runners/:runnerId`.* Rejected: issue deep-links and routing are project-scoped elsewhere.
- *Drawer/modal instead of a page.* Rejected: the spec explicitly calls for a detail page reachable by navigation.

### D7 — Issue deep-link built on the Web from `issue.projectId` + `issue.issueNumber`

The server returns the `WorkIssueRef` verbatim (no URL). The Web constructs the issue-detail link using its existing route + `issue.projectId` + `issue.issueNumber`. When `issue` is absent, the row renders stage/title with no link (no placeholder).

**Alternatives considered:**
- *Server returns a pre-built URL.* Rejected: couples the server to Web routing conventions.

## Risks / Trade-offs

- **[RunnerRuntimeState wire-shape change]** → Acceptable in active dev with no version-compat requirement and a single in-tree caller set; mitigate by updating every caller in the same change and running `npm test` (server) plus the runner grain specs.
- **[RunnerStatusView.ActiveWork → ActiveWorks is a breaking API shape for the Web]** → Update Web types + components + tests in the same change; covered by `npm run test:run -w packages/web`.
- **[Detail endpoint still does a per-runner grain call]** → Strictly cheaper than the list endpoint (1 runner, not N). No new fan-out; same `ProjectRunnerAsync` path, just filtered by id.
- **[Active-work context can go stale if a runner silently disappears mid-work]** → Accepted: this is a projection of current runtime state, not a source of truth. The grain already clears `_works` on heartbeat timeout (`HandleTimeoutAsync`) and on report, so the projection tracks cleanup correctly.
- **[Two top-level `runner` nouns in the CLI]** → Mitigated by disjoint subcommand sets and help text; documented in D5.
- **[Multi-slot runners are rare in practice today (`DefaultMaxWorkflowSlots = 1`)]** → The list-shaped contract future-proofs the API/Web for when slots are raised; no extra cost for the single-work case.

## Migration Plan

Single change set, no phased rollout (active development, single deployment). Recommended implementation order to keep each layer's tests green:

1. **Server domain:** add `RunnerActiveWorkItem`; replace `RunnerRuntimeState.ActiveWorkflowRunIds` with `ActiveWorks`; update `RunnerGrain.GetRuntimeStateAsync` and `DeriveStatus`.
2. **Server projection + API:** extend `RunnerActiveWorkView` (add `OwnerKind`, `OwnerId`, `Issue`); switch `RunnerStatusView.ActiveWork` → `ActiveWorks`; update `RunnerStatusService.ProjectRunnerAsync`; add `GET /api/projects/{projectRef}/runners/{runnerId}`.
3. **Web:** update `entities/runner/model/types.ts`; add the detail page (route + query + component); make `RunnerRow` navigate to it; render issue links.
4. **CLI:** add the top-level `mo runner` group with `show <runnerId>` consuming the detail endpoint.
5. **Tests:** extend `RunnerStatusApiSpecs` / `RunnerStatusProjectionSpecs` for multi-work + detail + 404; add Web tests for the detail page and list navigation; add a CLI test for `show`.

No database migration, no backfill. **Rollback:** revert the commit; no persisted state to recover. The pre-change list behavior (singular `ActiveWork`) is restored by the revert.

## Open Questions

- **Detail for stale/offline-but-registered runners:** the spec's "idle runner returns detail" scenario implies registered-but-not-busy returns 200. By the same logic, a registered-but-stale/offline runner should also return 200 with its last-known identity, health (`stale`/`offline`), and an empty (or last-known) `ActiveWorks` — the grain clears `_works` on timeout, so offline runners will show an empty list. Confirm this is the desired UX during implementation; if not, gate the 404 vs 200 boundary explicitly.
- **`mo runner show` default project behavior:** assume standard project selection (current project, `--project` override) — confirm during CLI wiring that this matches `mo issue show`.
