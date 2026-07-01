## Context

Today every project sees the full system workflow catalog (`mohist/local`, `mohist/github-pr`) with no way to curate it. The `mohist-create-issue` agent and `mo workflow list --described` always offer the entire menu. The effective-profile resolver hardcodes `mohist/local` as an unconditional fallback (`EffectiveWorkflowProfileResolver.ResolveCore:49`), so there is no concept of "this project disabled a profile".

Concurrently, the Settings > Workflows tab shows inaccurate information: profile cards render a hardcoded stage chip set (`DEFAULT_WORKFLOW_STAGES` at `WorkflowProfilesSection.tsx:13`) instead of the profile's real `stages`; the Settings Search workflow descriptor registry is an empty array (`WorkflowProfilesSection.tsx:18`); and the project-default control uses a native `<select>` (`ProjectDefaultWorkflowControl.tsx:92`) instead of the project's base-ui `Select` primitive.

The relevant surfaces are:

- **Server**: `ProjectWorkflowProfile` row (1:1 with project), `ProjectWorkflowProfileManager` (system catalog + project profile writes), `IssueWorkflowProfileRegistry` (`ListDescribed`), `EffectiveWorkflowProfileResolver` (the single resolution cascade), issue create handler (`IssueRoutes.Crud.cs:37`).
- **Discovery endpoints**: `/api/workflow-templates/system` and `/api/workflow-profiles` (`SystemRoutes.cs:15-19`) are currently project-agnostic and return the full catalog.
- **CLI**: `mo workflow list --described` (`MohistCliCommands.Workflow.cs`) calls `/api/workflow-profiles` with no project context.
- **Web**: `WorkflowProfilesSection.tsx`, `ProjectDefaultWorkflowControl.tsx`, `entities/settings/api/queries.ts` + `client.ts`.
- **Skill**: `mohist-create-issue/SKILL.md` states "the default profile is always a safe choice because it is guaranteed to exist".

Constraints: no changes to workflow execution, runner, or the grain-level profile content contracts. The system profile set is small and bounded (currently 2). EF Core migrations are the schema-evolution mechanism.

## Goals / Non-Goals

**Goals:**
- Add a per-project disabled-profile blacklist to `ProjectWorkflowProfile` (default empty = all enabled).
- Make workflow discovery (HTTP endpoints, CLI, agent candidate list) project-scoped so disabled profiles never appear.
- Enforce the Option A invariant (≥1 enabled profile per project) at both the disable action and the issue-creation boundary.
- Change the resolution cascade to skip disabled profiles; `mohist/local` is no longer an unconditional fallback.
- Fix the bundled UI display issues (real stages, populated Settings Search, base-ui `Select`, base-ui `Switch` toggle).
- Update the `mohist-create-issue` skill wording to reflect the new fallback semantics.

**Non-Goals:**
- Per-issue enable/disable (project-level curation only).
- Editing project-level workflow variables in the UI.
- Bulk enable/disable operations.
- Changes to workflow execution or runner.

## Decisions

### D1: Store the blacklist as a JSON array column on `ProjectWorkflowProfile`

Add a `DisabledWorkflowProfileIds` property (JSON-serialized `List<string>`) to `ProjectWorkflowProfile` (`ProjectWorkflowProfileRow.cs`), mirroring the existing `Prompts` dictionary pattern (JSON conversion + value comparer in `MohistDbContext.OnModelCreating`). One EF Core migration adds the column with a default empty array.

**Alternatives considered:**
- *Separate junction table* (`ProjectDisabledProfiles`). Over-engineered for a small, bounded system profile set with no join/query needs beyond a single project row lookup. The existing `Prompts` column already establishes the JSON-on-row precedent.
- *Whitelist model* (explicit enabled set, default empty). Rejected — the default of "all enabled" is the current behavior, and a blacklist means new system profiles added in future releases are automatically available without a migration to backfill the whitelist.

### D2: Make the two global discovery endpoints accept project context via query parameter

Add an optional `?project=<ref>` query parameter to `/api/workflow-templates/system` and `/api/workflow-profiles` (`SystemRoutes.cs:15-19`). When present, resolve the project (reusing the existing `ProjectResolver`) and filter out profiles on the blacklist before returning. When absent, return the full unfiltered catalog (backward compat for any consumer that does not have a project context, e.g. a future system-admin view).

The detail endpoint `/api/workflow-templates/system/{id}` stays unfiltered — it serves a known id, and the filtering concern is on the *list* surface.

**Alternatives considered:**
- *Move discovery under `/api/projects/{projectRef}/workflow-profiles`*. Rejected because it would orphan the existing global route consumers (CLI `PrintWorkflowProfilesDescribedAsync`, web `getWorkflowProfiles`) and requires a parallel route tree. The query-param approach is additive and lets the CLI/Web pass the resolved project id with a one-line change.
- *Project-scoped route as the primary, global as deprecated*. Same disruption cost; the query-param approach achieves the filtering contract with less churn.

The `ProjectWorkflowProfileManager.ListSystemTemplatesAsync` and `IssueWorkflowProfileRegistry.ListDescribed` methods gain an overload that accepts the disabled set (or a `string projectId`) and filters. The static catalog source stays unchanged.

### D3: Resolution cascade returns `string?`; `null` signals "no enabled profile"

`EffectiveWorkflowProfileResolver.ResolveCore` (`EffectiveWorkflowProfileResolver.cs:40`) currently unconditionally returns `IssueWorkflowProfiles.LocalId` as the terminal fallback. Change it to:

1. Issue selection (if set **and** enabled).
2. Project default (if set **and** enabled).
3. First enabled system profile (ordered by the registry's default-first ordering).
4. `null` (no enabled profile).

Return type becomes `string?`. Callers split by context:

- **Read paths** (`IssueQuerier`, `IssueGrain.StartAsync`): treat `null` as `IssueWorkflowProfiles.LocalId` for display/dispatch safety — a persisted issue must always have a displayable profile id, and the create-path check (D4) prevents the "none enabled" state from ever producing a persisted issue.
- **Create path** (`IssueRoutes.Crud.cs`): resolve *before* persisting; if `null`, return 400 with an actionable error and do not call the grain.

The resolver gains a new parameter: the disabled set (or an `isDisabled`/`isEnabled` predicate). The existing `exists` predicate stays (it checks registry membership, which is orthogonal to the disabled blacklist). To keep the resolver's signature testable as a pure function, pass the disabled set as an `IReadOnlyCollection<string>`; the caller composes both predicates.

**Alternatives considered:**
- *A separate `ResolutionResult` record with discriminated states.* Adds a type and forces every caller to deconstruct. `string?` is sufficient — the only signal callers need is "resolved id or none".
- *Throwing when none enabled.* Rejected — read paths must never throw for a persisted issue's display.

### D4: Pre-flight check lives in the API create handler, not in the grain

Add the "≥1 enabled profile" check in `IssueRoutes.Crud.cs:37` (the create POST handler), *before* calling `issueGrain.CreateAsync`. The grain is the authoritative state owner, but the API layer is the action boundary and already performs other validations (label validation, profile existence, model metadata). This keeps the grain's `CreateAsync` unchanged and avoids a second DB round-trip inside the grain.

The check: load the project's disabled set, compute the enabled set, and if empty return `ApiResults.BadRequest("...enable a workflow first...", "no_enabled_workflow_profile")`.

If an explicit `WorkflowProfileId` is provided on create, additionally verify it is not on the disabled blacklist — rejecting with `unknown_workflow_profile` (reusing the existing error code so the CLI surfaces it consistently).

**Alternatives considered:**
- *Check in the grain.* Would require the grain to depend on the disabled-set read path and duplicate the validation the API already owns. The grain stays a pure state machine.
- *Check only at dispatch (StartAsync).* Too late — the issue is already persisted with a potentially-disabled effective profile.

### D5: Disable/enable write API as individual toggle endpoints under the project route group

Add two project-scoped endpoints in `ProjectRoutes.cs` (which already has the `ProjectResolutionEndpointFilter`):

- `POST /api/projects/{projectRef}/workflow-profile/disable` — body `{ profileId }`. Adds to the blacklist. Rejects if it would leave zero enabled profiles (400, `last_enabled_workflow_profile`).
- `POST /api/projects/{projectRef}/workflow-profile/enable` — body `{ profileId }`. Removes from the blacklist.

Individual toggle endpoints (vs. a full-list PUT) make the last-enabled invariant a simple pre-check: count enabled before disabling. The `ProjectWorkflowProfileManager` gains `SetProfileEnabledAsync(projectId, profileId, enabled)` which encapsulates the read-modify-write and the invariant check, throwing `InvalidOperationException` on violation.

**Alternatives considered:**
- *`PUT /workflow-profile/disabled-profiles` with the full list.* Atomic but the invariant check becomes "the submitted list must not equal the full system set", which is less intuitive and harder to make idempotent-safe against concurrent system-profile additions.
- *`PATCH` with add/remove operations.* Over-generalized for a binary toggle on a small set.

### D6: New base-ui `Switch` primitive in `shared/ui/components/`

Create `packages/web/src/shared/ui/components/switch.tsx` wrapping `@base-ui/react/switch` (the project already uses `@base-ui/react` ^1.5.0 for the `Select` primitive at `select.tsx`). The `Switch` carries an `aria-label` prop per the accessibility requirement. No `Switch` primitive exists today (confirmed: no `switch*.tsx` under `shared/ui/components/`).

### D7: `useWorkflowProfiles` becomes project-scoped

The web hook `useWorkflowProfiles` (`queries.ts:270`) and client function `getWorkflowProfiles` (`client.ts:210`) currently call the global `/workflow-templates/system` with no project context. Change them to accept the current project id (from `useProject()`) and pass it as the `?project=` query param (per D2). The query key gains the project id so toggling enable/disable invalidates the correct cache slice. `useEffectiveDefaultWorkflowProfile` (`queries.ts:330`) is updated to drop its hardcoded `mohist/local` fallback — if the filtered list is empty, it reports `source: 'none'`.

**Alternatives considered:**
- *Keep the hook project-agnostic and filter client-side.* Rejected — the filtering must be authoritative (the agent and CLI consume the same filtered data), so client-side filtering would diverge from the server contract.

### D8: `mo workflow list` gains `--project`/`--project-id` and resolves before calling the filtered endpoint

The workflow list command (`MohistCliCommands.Workflow.cs:15`) currently has no project option. Add `--project`/`--project-id` (reusing the shared option description already in `MohistCliCommands.cs:55-56`), resolve via `ResolveProjectIdAsync`, and pass the resolved id as the `?project=` query param to `/api/workflow-profiles` (described) and `/api/workflow-templates/system` (plain). When no project is resolved and `--described` is set, fall back to the unfiltered endpoint with a stderr note — but in practice the agent always runs with an active project, so this is a degraded-path safety net, not the primary flow.

## Risks / Trade-offs

- **[Discovery endpoint now depends on project resolution]** -> The `?project=` query param triggers a project lookup. If the project ref is invalid, return 404 (consistent with the project-scoped route group). When the param is absent, the endpoint still works (unfiltered), so the failure mode is graceful.
- **[Resolver return-type change from `string` to `string?` is a breaking signature]** -> `ResolveCore` is `public static` and referenced by `IssueGrain` and `IssueQuerier`. All call sites are internal to the server project; update them in the same change. Add a test for each caller's null-handling.
- **[Disabled project default produces a confusing display]** -> If the project default is on the blacklist and the issue has no explicit selection, the resolver skips it and falls to the first enabled profile. The read model will show the *effective* (enabled) profile, not the configured default — this is correct per the spec. The UI amber warning (D6 + `ProjectDefaultWorkflowControl`) surfaces the mismatch to the operator.
- **[Race: disable the last profile while an issue is being created]** -> The disable action and the create action are separate requests. The create-path pre-flight check (D4) reads the blacklist at request time, so the worst case is a create that succeeds between a disable-check and a disable-commit. This is acceptable: the invariant is "never persist an issue with zero enabled profiles", and the disable action's own invariant check prevents reaching zero. The create check is a second safety net, not a lock.
- **[Skill wording change is a content-only update]** -> The `mohist-create-issue` SKILL.md is bundled skill data; updating it has no runtime coupling. Low risk.
- **[EF migration on a single-user local system]** -> The migration adds one nullable-with-default column. Rollback is dropping the column. No data backfill needed (default empty array).

## Migration Plan

1. **Schema**: Add EF Core migration `AddDisabledWorkflowProfileIds` — new `DisabledWorkflowProfileIds` column (JSON text, default `"[]"`) on `ProjectWorkflowProfiles`. Apply on startup (the system auto-migrates).
2. **Server deploy**: Build with the new column, filtering logic, toggle endpoints, resolver change, and create-path check. Deploy via `mo update server`. Existing projects get an empty blacklist (all enabled) — behavior is identical to today.
3. **CLI deploy**: `mo update` ships the updated `mo workflow list` with `--project` resolution. The skill-data update for `mohist-create-issue` ships in the same CLI build.
4. **Web deploy**: `mo update` ships the UI changes (real stages, Switch, Select, Settings Search entries, project-scoped hook).
5. **Rollback**: Revert the deploy. The migration's `Down` drops the column; existing rows lose their blacklist (all enabled), which is the pre-change default. No data loss in workflow execution state.

No coordinated multi-service rollout is needed — server, CLI, and web are independently backward compatible at the API contract level (the `?project=` param is optional; omitting it returns the full catalog).

## Open Questions

- Should the `?project=` query param accept project *name* (like the route `{projectRef}`) or only the canonical id? **Tentative**: accept ref (name-or-id) and resolve via `ProjectResolver`, consistent with the route group. Resolve during implementation by checking `ProjectResolver`'s existing input contract.
- The detail endpoint `/api/workflow-templates/system/{id}` is unfiltered (D2). Should the web `ProfileDetail` view block navigation to a disabled profile's detail? **Tentative**: no — the detail view is informational; the card list is the curated surface. The Switch on the card already communicates disabled state.
