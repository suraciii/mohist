## Context

Issue 286 shipped the project-scoped inbox MVP: a server-side projection (`InboxProjectionHandler`, `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs`) maps four authoritative events to four notification kinds and inserts one `InboxItemRow` per event, unconditionally, into a SQLite table via the scoped `InboxStore`. Every project currently records all four kinds with no opt-out.

Issue 285 adds a project-scoped **subscription preference** that gates that projection per kind, while preserving the MVP default (all four on) so existing behavior is unchanged. See `proposal.md` for motivation and `specs/` for requirements.

Current state that constrains this design:

- The inbox is persisted as **EF Core / SQLite**, not Orleans storage. `InboxItemRow` (`packages/server/src/Mohist.Server/Infrastructure/Data/Inbox/InboxItemRow.cs`), `InboxStore`, `InboxQuerier` (both `IScopedService`), `MohistDbContext` config at `MohistDbContext.cs:543-573`.
- The projection is wired as a singleton `[Subscription]` handler. For each accepted event it opens a DI scope (`InboxProjectionHandler.cs:114`) and resolves `InboxStore` + `IStateStore<DomainIssue>` from it, then inserts at `InboxProjectionHandler.cs:149`. This scope is the natural seam for the new gate.
- The closest analog for project-scoped preference state is `ProjectWorkflowProfile`: a 1:1-with-Project EF table managed by a scoped service (`ProjectWorkflowProfileManager`, e.g. `GetVariablesAsync`/`SetVariablesAsync` at `ProjectWorkflowProfileManager.cs:232-249`) using the "load by projectId; null ⇒ default; else mutate; SaveChanges" shape.
- `NotificationKind` is a fixed four-value string set via the `NotificationKinds` constants class (`packages/server/src/Mohist.Server/Inbox/InboxModels.cs:8-20`) with `IsDefined(...)` validation; the same four strings are mirrored in the web (`packages/web/src/entities/inbox/model/types.ts`). The kind set is fixed by issue 285's Non-Goals (no label/stage rules, no per-user prefs).
- The web settings surface is tabbed (`packages/web/src/pages/settings/ui/SettingsPage.tsx`). `PreferencesSection` is **off-limits** — it carries an active non-goal guard (`PreferencesSection.test.tsx`) that fails the build if the word "notification" appears in it. A new tab is required.

Stakeholders: local-first operators (single project owner). No multi-tenant isolation concerns beyond the existing project scope enforced by `ProjectResolutionEndpointFilter`.

## Goals / Non-Goals

**Goals:**

- Add an `InboxSubscription` preference model, project-scoped, over exactly the four MVP notification kinds, keyed by `NotificationKind` (not CloudEvent type strings).
- Default to all-four-enabled for projects with no stored preferences (preserve MVP behavior, zero backfill).
- Gate the existing projection so a disabled kind produces no **future** inbox item; re-enabling resumes projection without backfill.
- Provide a project-scoped read + update HTTP API.
- Provide a small Web UI settings surface with product-facing labels that persists through the API.
- Leave existing inbox items, workflow execution, the runner, and the issue lifecycle untouched.

**Non-Goals:** (mirror the issue) per-user prefs; per-issue watch/unwatch; label/stage rules; global default UI; retroactive deletion/backfill; external notification channels; any change to realtime SignalR connection subscriptions.

## Decisions

### D1 — Persist as an EF Core table (1:1 with Project), not an Orleans grain

The proposal's Impact line says "Orleans grain/storage," but the codebase convention for both the inbox itself and for project-scoped preferences is EF Core via a scoped `IScopedService`. The projection hot path already opens a DI scope and resolves scoped EF services (`InboxProjectionHandler.cs:114-116`); introducing a grain there would cross the silo/grain boundary for no gain and diverge from the inbox domain's persistence model.

**Decision:** Add an `InboxSubscriptions` table (PK `ProjectId`, 1:1 with `Projects`) and a scoped `InboxSubscriptionStore : IScopedService` mirroring `ProjectWorkflowProfileManager`'s load-or-default / mutate / SaveChanges shape. The new store is auto-registered by `AddMohistConventionalServices()` (`MohistServiceRegistration.cs:51`) via the `IScopedService` marker, so no manual DI wiring is needed and the test fixture picks it up automatically.

- **Alternative considered:** `[PersistentState("inbox-subscription")] IPersistentState<...>` grain keyed by `projectId` (like `IssueCounterGrain`). Rejected: the projection already operates in a scoped EF context, the inbox domain is entirely EF today, and a grain would add an asynchronous hop on every mapped event for a 4-boolean preference. The proposal's wording is treated as describing "project-scoped durable state," which EF satisfies.

### D2 — Store shape: one row per project, four nullable bool columns

**Decision:** One row per project with four `bool` columns (`WorkflowFailedEnabled`, `ApprovalRequestedEnabled`, `IssueStartedEnabled`, `IssueCompletedEnabled`). "No row" means all-enabled (synthesized on read; no eager creation). Toggle reads/writes are direct column access; CHECK constraints are unnecessary because `bool` is self-validating.

- **Alternative A: JSON column `kind → bool`.** Rejected: the kind set is explicitly fixed by issue 285's Non-Goals, JSON parsing on every projected event is more expensive than a column read, and constraints are weaker.
- **Alternative B: per-(project, kind) rows.** Rejected: over-normalized for four fixed toggles; adds a join on the projection hot path.
- **Adding a fifth kind (future issue)** is an additive migration under all three options; (A) is not meaningfully cheaper because the projection's `switch` and the UI labels must change regardless.

### D3 — No eager row creation; "missing ⇒ all-enabled" everywhere

`ProjectWorkflowProfile` writes lazily (row created on first explicit update). `InboxSubscription` follows the same rule: project creation is **not** modified, reads synthesize all-enabled when the row is absent, and the projection treats absence as all-enabled.

**Rationale:** Zero backfill, zero migration of existing projects, MVP behavior preserved by construction, and `Durable subscription does not require an open connection` (spec) is automatically satisfied.

### D4 — Projection gate: one read in the existing scope, before insert

Modify `InboxProjectionHandler.ProjectAsync` only: after `resolved` and `kind` are known (~`InboxProjectionHandler.cs:140`) and before `inboxStore.InsertAsync` (`:149`), resolve `InboxSubscriptionStore` from the already-open scope (`:114`) and read the project's subscription. If `kind`'s toggle is disabled, `return` early — no insert, no realtime hint. The synthesized all-enabled default handles the missing-row case.

- No in-process cache in this change. The projection is event-driven (low frequency), correctness dominates, and a cache would introduce a staleness window between an API update and the next projected event. Revisit only if profiling shows a hot path.
- **Idempotency preserved:** early-return on a disabled kind is safe under replay; re-enabling does not backfill because the event was never inserted (the `InboxItems.UQ_InboxItems_SourceEvent` idempotency index is unaffected either way).

### D5 — HTTP API: `GET` + `PUT` on `/api/projects/{projectRef}/inbox/subscription`

Extend the existing `InboxRoutes` group (same `ProjectResolutionEndpointFilter`, same project isolation) with:

- `GET  /subscription` → returns the four toggles (all-enabled when no row).
- `PUT  /subscription` → accepts the four toggles keyed by `NotificationKind`; rejects any key failing `NotificationKinds.IsDefined` with `ApiResults.BadRequest(...)` (spec: "update SHALL NOT accept keys other than the four supported NotificationKind values").

**Decision: PUT (whole-object replace), not PATCH-merge.** The surface is four fixed toggles and the UI will always send the full state. PUT is unambiguous, trivially validates the key set, and avoids partial-update semantics. DTOs are defined inline in the route file with a `FromState`/`ToState` mapper, following `InboxItemDto.FromView` (`InboxRoutes.cs:83-108`).

- **Alternative considered:** PATCH for "one or more kinds." Rejected as unnecessary complexity — partial vs. full is indistinguishable for a four-toggle form, and PUT makes the "reject unknown keys" rule a single set-equality check.

### D6 — Web UI: new dedicated `'inbox'` tab, not `PreferencesSection`

**Decision:** Add a new `inbox` section to `VALID_SECTIONS` / `SECTION_META` in `SettingsPage.tsx` and a new `InboxSubscriptionSection.tsx` rendering four toggles with product-facing labels.

- `PreferencesSection` is explicitly guarded: `PreferencesSection.test.tsx` fails the build if "notification" appears there. The new tab avoids that guard entirely and matches the proposal's "under project/inbox settings" wording.
- **Labels:** reuse the existing product-facing label map `KIND_DESCRIPTORS` from `InboxPage.tsx` ("Workflow failed", "Approval requested", "Issue started", "Issue completed") so server kind strings never surface to the user (spec: "SHALL NOT display raw event or CloudEvent type names").
- **Hooks:** add to `entities/inbox/api/` (co-located with inbox), mirroring `useInbox` — `subscriptionQueryKey = (projectId) => ['inbox-subscription', projectId]`, `useInboxSubscription()`, `useUpdateInboxSubscription()` (invalidates `['inbox-subscription', projectId]` on success). The inbox list query (`['inbox', projectId]`) is **not** invalidated — toggling a kind changes only future projection, never existing items.
- **Toggle primitive:** use `@base-ui/react/switch` inline (mirrors how `PreferencesSection` already uses `@base-ui/react/radio-group` inline). No new shared component yet — there is no second consumer. A shared `switch.tsx` is deferred until one appears.

## Risks / Trade-offs

- **[Proposal says "Orleans grain", codebase says EF]** → Resolved by D1; the deviation is documented and consistent with the inbox domain's existing persistence. No functional difference for a local-first single-node deployment.
- **[Stale subscription after an API update races an in-flight event]** → Acceptable eventual consistency: the next event after the `SaveChanges` commit sees the new state. Worst case is one extra/missed item around the toggle instant; idempotent insert means no corruption. No mitigation needed for the MVP.
- **[Projection does one extra DB read per mapped event]** → Negligible: events are low-frequency and SQLite is local. No cache now (D4); defer to profiling.
- **[Adding a 5th kind later requires a migration + projection switch + UI change]** → True under any storage shape (D2); acceptable given the Non-Goals explicitly fix the kind set for this issue.
- **[Build-break risk from `PreferencesSection` non-goal guard]** → Mitigated by D6: new tab, `PreferencesSection` untouched.
- **[UI toggle has no accessible name]** → Mitigation: each toggle is wrapped with `<Label>` (`shared/ui/components/label.tsx`) and the a11y config's `aria-toggle-field-name` rule is satisfied; covered by the existing settings a11y test suite.

## Migration Plan

This is an additive, backward-compatible change for a local-first product currently in active development (no version-compatibility concern).

1. **Schema:** add EF migration `<timestamp>_AddInboxSubscriptionsTable.cs` (+ `.Designer.cs`, regenerate `MohistDbContextModelSnapshot.cs`), mirroring `20260629003151_AddInboxItemsTable.cs`. Table: `InboxSubscriptions`, PK `ProjectId` (FK → `Projects(Id)`), four `bool` columns, `UpdatedAt`. No data backfill — absence = all-enabled.
2. **Deploy:** schema migration runs on startup (`MohistDbContext` migrates on boot). Existing projects keep MVP behavior automatically because they have no row.
3. **Rollback:** revert the code; the table is additive and harmless if present. Re-running the older code simply never reads it (projection inserts unconditionally again).

## Open Questions

- Should disabling a kind also invalidate the unread-count query used by badges? Current assumption: **no** — unread counts derive from existing items, which are unchanged. Confirm during UI implementation.
- Is there a future desire to expose subscription state via SignalR (live update when prefs change elsewhere)? Out of scope here; the read query refetches on settings mount, which is sufficient for local-first.
