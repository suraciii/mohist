## Context

The Settings IA refactor (issue-302) just stabilized the settings shell
(routing scope, sub-nav, `getSectionMeta` SOT, the `settings-consistency`
contract test). On that stable surface sits a set of long-standing
reliability/consistency defects, all confined to `packages/web` (no HTTP API,
domain, or persistence change — Server / runner / CLI are untouched):

- **Silent failures.** `AgentSettingsSection` `handleSave` / `confirmReset`
  both end in an empty `catch {}` (`AgentSettingsSection.tsx:322` and
  `:370`). `setSaveError` is reset at the top of each but never written in
  the catch, so a failed save/reset reverts the button with no feedback.
- **Inconsistent destructive confirmation.** Agent reset uses a hand-written
  `fixed inset-0 z-50` modal (`AgentSettingsSection.tsx:460-484`) with no
  focus trap, no focus restore, no Escape, no `role="dialog"`. The issue
  comment delete uses `window.confirm` (`IssueDetailPage.tsx:736`) — the
  **only** `window.confirm`/`window.alert` in `packages/web/src`. Label
  deletes, template deletes, and repository removes have **no** confirmation
  at all and fire on a single click (`LabelCatalogSection.tsx:343`,
  `TemplatesSection.tsx:326`, `RepositoriesSection.tsx:157`).
- **No a11y on field errors.** `AgentSettingsSection.InputField`
  (`:182-228`) renders errors in an un-id'd `<p className="text-red-600">`
  and never sets `aria-invalid`. Label-catalog add/edit errors are the same.
  `PreferencesSection` already shows the right pattern (`useId` +
  `aria-labelledby` + `aria-describedby`).
- **Wrong metadata.** `SystemSettingsSection.tsx:261` hardcodes Log Path to
  `~/.mohist/logs/` while the Paths card three inches below reads
  `systemInfo.paths.logs`. An amber "edit config.jsonc" banner sits as an
  orphan sibling at `:429`.
- **Inconsistent "no project" state.** Three different markups: a bare
  `<div className="text-sm text-muted-foreground">No project selected</div>`
  in `SettingsPage.tsx:34/44` (repositories/label-catalog/inbox), a dashed
  box in `TemplatesSection.tsx:258`, and a silent partial render in
  `WorkflowProfilesSection.tsx:274`. Empty-list states carry text but no
  next-step CTA.
- **Unsaved changes discarded.** `AgentSettingsSection` already computes a
  `dirty` boolean (`:255`) but nothing consults it on tab switch — settings
  routing is URL-driven through `<Link>` (`SettingsSubNav.tsx:48`), so
  clicking another tab navigates immediately.
- **No shared confirm/empty primitives.** No `AlertDialog`/`ConfirmDialog`
  exists (confirmed by grep). No shared `EmptyState` — `SectionState`
  (`SectionState.tsx`) is the closest, used by most settings tabs.

Foundational facts that constrain the design:

- `dialog.tsx` wraps `@base-ui/react/dialog`, whose `Root`/`Popup` already
  provide focus trap, focus restore, Escape dismissal, and `aria-modal` for
  free. `Dialog` is uncontrolled-by-default but accepts `open`/
  `onOpenChange` (used controlled by `NewTemplateDialog`, `CreateIssueDialog`,
  etc.). `react-router-dom@^7.14.0` is installed, so the data-router
  `useBlocker` API is available for the dirty-guard.
- Tailwind v4 (CSS-based, no config file). `text-balance`, `text-pretty`,
  `tabular-nums` are all core utilities; the first two are currently unused
  anywhere, `tabular-nums` is already used in 64 places.
- `SettingsSection.tsx` (21 lines) is the single SOT for the page-level
  `<h2>` title + `<p>` description; the `settings-consistency` contract test
  guarantees every section routes through it and through `getSectionMeta`.
- Settings sections today use **neither** toast system (`sonner` /
  `RuntimeToastHost`) — errors are inline-only. The spec's "critical errors
  must not be toast-only" rule is therefore a continuation of the existing
  baseline, not a new constraint.
- Test stack: vitest + @testing-library/react + jest-dom; the a11y track is
  `tests/a11y/settings-a11y.test.tsx` (vitest-axe structural rules) plus a
  Playwright axe spec. `label-catalog`/`inbox` are currently missing from the
  a11y matrix.

Stakeholders: three new capabilities — `destructive-confirmation`,
`settings-form-reliability`, `settings-content-consistency` (see `specs/`).
Risk `medium` comes from the dirty-guard + unified-confirmation touching
cross-section interaction patterns; there is no data-model change.

## Goals / Non-Goals

**Goals:**

- Add one shared accessible `AlertDialog` primitive on top of `dialog.tsx`
  (focus trap, focus restore, Escape) and route **every** destructive op
  through it: Agent reset, label-definition delete, repository remove,
  template delete, and the IssueDetail comment delete. Eliminate the
  hand-written modal and `window.confirm`.
- Make `AgentSettingsSection` save/reset failures visible inline (no silent
  `catch {}`); critical failures get persistent inline feedback, not a
  transient toast.
- Wire `aria-describedby` + `aria-invalid` on Agent and Label-catalog field
  errors, mirroring `PreferencesSection`.
- Warn before a dirty Agent form is discarded by a settings tab switch.
- Fix System tab accuracy (Log Path from `systemInfo.paths.logs`; relocate
  the orphan amber banner) and apply the typography baseline
  (`text-balance` / `text-pretty` / `tabular-nums`) from a single SOT.
- Give "no project" and empty-list states an explicit next-step CTA; add a
  Label-catalog search input on par with Templates.

**Non-Goals:** (per proposal)

- Introduce a notification subsystem / new toast library.
- Redesign section layout (IA — owned by the epic).
- Wholesale migrate to a new UI primitive library; `AlertDialog` is built on
  the existing `dialog.tsx` only.
- Migrate the other bespoke `fixed inset-0` overlays outside settings
  (`ReviewReportModal`, `AppSidebar`, `MarkdownReader`) — those are
  out of scope, though `AlertDialog` could absorb them later.

## Decisions

### D1. Build `AlertDialog` on top of `dialog.tsx`; let base-ui own focus/Escape

**Decision:** Add a new `packages/web/src/shared/ui/components/alert-dialog.tsx`
that composes the existing `Dialog`/`DialogContent`/`DialogTitle`/
`DialogDescription` primitives. It does **not** re-implement focus trap,
focus restore, or Escape — `@base-ui/react/dialog` already provides all
three (the current hand-written modal lacks them precisely because it
bypasses `Dialog`). `AlertDialog` is a *role/affordance* specialization:
`variant="destructive"` action button, no `showCloseButton` X by default
(dismissal is via explicit Cancel / overlay / Escape), and an opinionated
two-button footer.

**API (controlled, mirroring `NewTemplateDialog`):**

```ts
<AlertDialog
  open={open}
  onOpenChange={setOpen}
  title="Reset Coder Agent Settings"
  description="Reset all agent runtime settings to their default values?"
  confirmLabel="Reset"
  cancelLabel="Cancel"
  tone="destructive"
  loading={saving}
  onConfirm={confirmReset}
/>
```

**Rationale / alternatives:**

- **Hand-write a fresh focus-trap** — rejected; reinvents what base-ui
  already gives `Dialog`, and the spec explicitly demands the trap/restore/
  Escape behaviors that base-ui supplies.
- **Adopt Radix `AlertDialog`** — rejected; introduces a second dialog
  dependency alongside base-ui for one component, and base-ui's `Dialog`
  already meets the a11y contract. The Non-Goal "don't migrate to a new UI
  primitive library" rules this out.
- **Promote `SessionRecoveryActions`'s inline confirm-`Dialog` to the shared
  primitive** — considered; it is the closest existing pattern but is
  call-site-specific. `AlertDialog` generalizes its shape.

### D2. Per-call-site controlled state; no global confirm orchestrator

**Decision:** Each destructive trigger owns its own `open` state and a
"pending target" (e.g. the label key / repo name / comment id to delete).
The button's `onClick` sets the pending target and opens the dialog; the
dialog's `onConfirm` runs the mutation against the pending target and closes
on success. This matches how `NewTemplateDialog`/`CreateIssueDialog` already
manage their `Dialog` open state.

**Rationale / alternatives:**

- **One global `<ConfirmProvider>` + `confirm()` imperative API** — rejected:
  pulls confirmation state out of the component tree (worse testability,
  breaks the "no real side channels" testing rule), and forces a context
  dependency into every section. Per-call-site state is local, mock-free,
  and matches the existing controlled-`Dialog` convention.
- **Inline confirmation (no modal)** — considered (the
  `WorkflowProfilesSection.blockedMessage` amber-banner pattern is the
  precedent); rejected because the spec mandates a shared **dialog**
  primitive and uniformity across very different section layouts.

For lists with many deletable rows (label catalog, templates, repositories),
a **single** `AlertDialog` instance per section is reused with a
`pendingKey` state — not one dialog per row.

### D3. Surface save/reset failures inline via `saveError` + `aria-live`; no toast

**Decision:** Replace the empty `catch {}` in `handleSave` (`:322`) and
`confirmReset` (`:370`) with `catch (err) { setSaveError(message(err)) }`.
The existing `saveError` inline surface already renders in the section;
upgrade it to a `role="alert"` red alert card (matching the
`LabelCatalogSection` page-error at `:372-380`) and wrap it with
`aria-live="polite"` so a screen reader announces it (mirroring
`PreferencesSection.tsx:170`). No `sonner`/`RuntimeToast` call is added —
critical errors stay persistent-inline, satisfying the "not toast-only"
rule and continuing the existing settings convention.

**Rationale:** Settings already uses inline-only error feedback; introducing
toasts here would be a regression on the spec's explicit anti-toast clause.
`message(err)` reuses the existing error-to-string helper used elsewhere in
the section.

### D4. Field-error a11y: mirror `PreferencesSection`; extract a shared `FieldError`

**Decision:** In `AgentSettingsSection.InputField`, generate an error id via
`useId()`, add it to the error `<p>`, set `aria-describedby={errorId}` and
`aria-invalid={!!error}` on the `<Input>`. Apply the same to the Label-catalog
add/edit error surfaces and the new Templates/Label search inputs (add
`aria-label`/visible label — the Templates search at `:287` currently has
only a placeholder, which is itself an a11y gap).

Extract a tiny shared `FieldError` helper
(`packages/web/src/shared/ui/components/field-error.tsx`) that renders the
`role="alert"` `<p>` with a stable id, so the red-error markup (currently
inconsistent: `text-red-600` in `InputField`, `text-red-700` in
Label-catalog) converges on the `red-700` family used by the page-error
card — and stays inside the `settings-consistency` contrast rule.

**Alternatives considered:**

- **Full `<Field>` wrapper (label + input + error + description slot)**
  promoted app-wide — larger blast radius, touches `PreferencesSection` too;
  deferred (out of scope for a polish issue). `FieldError` alone gives the
  a11y wiring without a wider refactor.
- **Per-section local fixes only** — rejected; leaves the inconsistent
  error styling and misses the search-input label gaps.

### D5. Dirty-guard via react-router `useBlocker` + a `SettingsDirtyContext`

**Decision:** Settings routing is URL-driven through `<Link>`
(`SettingsSubNav.tsx:48`), so the natural interception point is the router.
Introduce a `SettingsDirtyContext` provider in `SettingsPage` with a
`setDirty(bool)` setter. `AgentSettingsSection` calls `setDirty(dirty)` in an
effect (the `dirty` memo already exists at `:255`). `SettingsPage` reads
`dirty` and calls react-router's `useBlocker(dirty)`; when a blocker is in
the `blocked` state, render an `AlertDialog` ("Discard unsaved changes?")
whose confirm proceeds (`blocker.proceed()`) and cancel reverts
(`blocker.reset()`).

**Rationale / alternatives:**

- **Intercept `<Link>` `onClick` in `SettingsSubNav`** — rejected: only
  covers sub-nav clicks, not browser back / in-page deep links; needs every
  navigation site to opt in. `useBlocker` covers all navigations uniformly
  (back, forward, link, `Navigate`) for one wiring cost.
- **Lift the whole form state into `SettingsPage`** — rejected: massive
  surface change for one guard. The context exposes only a boolean.
- **Why a context at all** — `dirty` lives as local state in
  `AgentSettingsSection`; the blocker must live where the router is in scope
  (`SettingsPage`). The context is the minimal bridge. The provider is
  settings-scoped, not global.

The guard ships for `AgentSettingsSection` first (per spec/Non-Goal);
extending to other sections later is just another `setDirty` call — no
architecture change.

### D6. Typography baseline lands in the SOT, not per-section

**Decision:** Add `text-balance` to the `<h2>` and `text-pretty` to the
`<p>` in `SettingsSection.tsx` — **one edit**, uniformly affects all nine
sections because the contract test guarantees they all route through it. Add
`tabular-nums` to the `InfoRow` value in `SystemSettingsSection`
(`:41-48`) and to the `AgentSettingsSection` numeric `<Input>`/unit rows.
No new motion or gradients (spec rule).

**Rationale:** Per-section edits would duplicate the utility 9× and risk
contract-test drift; the SOT edit is one line each and is self-documenting.
`tabular-nums` is intentionally **not** put on the page title (it's for
numeric columns/mono data rows only).

### D7. Consolidate empty states by extending `SectionState`; add CTAs

**Decision:** Extend the existing `SectionState` (used by most settings tabs)
rather than minting a new `EmptyState`. Specifically:

- Add an optional `action?: ReactNode` (and keep `children`) so the empty
  and no-project variants can render a CTA button.
- Add a `variant="no-project"` (or a `noProject` boolean on `empty`) that
  renders the dashed box with a "Select project" / "Create project" CTA.
- Replace the three existing "No project selected" markups
  (`SettingsPage.tsx:34/44` bare div, `TemplatesSection.tsx:258` dashed box,
  `WorkflowProfilesSection.tsx:274` silent partial) with this one variant.
- Give Label-catalog and Templates empty-list states an inline "New
  definition" / "New Template" CTA via `action`. The CTA triggers the same
  create affordance already in each section's header.

**Rationale / alternatives:**

- **New shared `EmptyState` under `shared/ui/components`** — considered;
  there are several local `*EmptyState` copies app-wide (`RunnerList`,
  `RunnersPage`, `AgentListPage`, …), so a shared primitive is desirable
  long-term. But consolidating those is out of scope for this settings
  issue. Extending `SectionState` delivers the settings requirement without
  a cross-app refactor; the broader consolidation is a follow-up.
- **CTA target for "no project"** — the CTA navigates to project
  selection/creation (reuse the existing project switcher / `CreateProject
  Dialog`). Exact target confirmed at implementation time from the current
  project-switcher entry point.

### D8. System tab accuracy + orphan banner — localized fixes

**Decision:**

- Replace the hardcoded `~/.mohist/logs/` (`SystemSettingsSection.tsx:261`)
  with `systemInfo.paths.logs ?? '—'`, reading from the same `useSystemInfo`
  query the Paths card uses. Add a `tabular-nums` to the mono row.
- Relocate the orphan amber banner (`:429`) **into** the card it describes
  (the Update / server-config card) as an inline amber note, or convert it
  to an info `Tooltip` on that card's heading. Prefer the inline-note option
  (matches the existing in-card amber notes at `:339-342` and the
  `WorkflowProfilesSection` blocked-message pattern) and fix the shade
  inconsistency (`text-amber-700` vs `text-amber-800`) to one token.

### D9. Sequencing: `AlertDialog` lands first; everything else depends on it

**Decision (ordering):** Ship in three reviewable slices so the foundational
primitive and its first migration are validated before the broad cross-section
sweep:

1. **Slice A — primitive + first migration:** add `AlertDialog`; migrate
   Agent reset (removes the hand-written modal) and IssueDetail comment
   delete (removes `window.confirm`). Add `dialog`/`alert-dialog` unit +
   a11y tests (focus trap/restore/Escape). This is the dependency for the
   rest.
2. **Slice B — the other destructive ops + error surfacing + field a11y:**
   route label/template/repo deletes through `AlertDialog`; fix the
   `AgentSettingsSection` `catch {}` blocks; wire `FieldError` a11y.
3. **Slice C — dirty-guard, System accuracy, typography, empty states,
   label search:** the consistency pass.

Each slice is independently green (typecheck + tests) so partial progress
still ships.

## Risks / Trade-offs

- **[`useBlocker` UX surprise — user gets prompted on back/refresh too]**
  → This is intended (unsaved changes are unsaved everywhere), but the
  dialog copy must be generic ("Discard unsaved changes?"). Confirm the
  router's blocker doesn't fire on same-route navigations (it shouldn't).
- **[Per-call-site `AlertDialog` state proliferates across sections]**
  → Each list section reuses **one** dialog + a `pendingKey` (D2), so the
  count stays at one-per-section, not one-per-row. Acceptable.
- **[Dirty context added to settings tree could be forgotten by a future
  section]** → The guard defaults to "not dirty"; a section that forgets to
  call `setDirty` simply isn't guarded — a graceful no-op, not a regression.
  Document the opt-in at `SettingsDirtyContext`.
- **[Extending `SectionState` risks the existing empty/loading/error
  variants] ** → New `action`/`variant` are additive and optional; existing
  call sites render unchanged. Add a snapshot/contract for `SectionState`.
- **[`useBlocker` requires a data router] ** → Confirm `App.tsx`'s router is
  `createBrowserRouter`/`RouterProvider` (not the legacy `<BrowserRouter>`),
  otherwise `useBlocker` is a no-op. Verify at implementation time; fall back
  to the `SettingsSubNav` `onClick` intercept (the rejected alternative) if
  the legacy router is in use.
- **[Contract test (`settings-consistency`) may trip on new error/search
  markup] ** → Keep error styling on the `red-700`/`amber-700` token families
  (no `text-gray-*`), no inline `<svg>`, keep routing titles through
  `SettingsSection`/`getSectionMeta`. Re-run the contract test per slice.
- **[`label-catalog`/`inbox` not in the a11y matrix] ** → Add them to
  `tests/a11y/settings-a11y.test.tsx`'s `settingsTabs` array as part of
  Slice B/C so the new a11y wiring is structurally verified.
- **[IssueDetail comment-delete error has a latent UX bug
  (`deletingCommentId === null` predicate at `:748`)]** → While migrating to
  `AlertDialog`, fix the predicate so the error persists until dismissed.

## Migration Plan

1. **No backend/schema change** — Web-only; no DB migration, no API change,
   no `mo update server`. Deploy is a web rebuild only.
2. **Slice A → B → C** per D9. Each slice lands behind its own commit and
   keeps `npm run typecheck -w packages/web` + `npm run test:run -w
   packages/web` + `npm run test:a11y` green.
3. **Tests added per slice:**
   - Slice A: `alert-dialog.test.tsx` (open/close, Escape cancels, confirm
     fires `onConfirm`, focus restored); update `AgentSettingsSection.test`
     and `IssueDetailPage.test` to assert the dialog replaces the modal/
     `window.confirm`.
   - Slice B: extend the section tests to assert confirmation-before-delete
     and inline error on rejected save/reset; add `aria-invalid`/
     `aria-describedby` assertions.
   - Slice C: dirty-guard test (dirty → blocked → confirm proceeds; clean →
     no prompt); assert Log Path equals `systemInfo.paths.logs`; assert
     empty-state CTA renders; extend the a11y matrix to `label-catalog`/
     `inbox`.
4. **Rollback:** Pure revert; no persisted state. The hand-written modal and
   `window.confirm` are restored by the revert (they are only removed, not
   refactored into shared infra that other code grows to depend on within
   the same change).

## Open Questions

- **Is the app router a data router?** Confirm `App.tsx` uses
  `createBrowserRouter`/`RouterProvider` so `useBlocker` is active (D5). If
  not, fall back to the `SettingsSubNav` `onClick` intercept.
- **"No project" CTA target** — exact entry point for "select/create
  project" (existing project switcher vs `CreateProjectDialog`). Confirm
  from current shell code at implementation time (D7).
- **Amber banner relocation — inline note vs `Tooltip`** — prefer inline
  note (D8); confirm the destination card is the server-config/Update card.
- **Should `FieldError` later grow into a full `<Field>` wrapper** —
  recommended as a follow-up (D4), out of scope here.
- **Extending the dirty-guard beyond Agent** — spec scopes it to
  `AgentSettingsSection` first; other sections opt in by calling
  `setDirty`. Decide whether Label-catalog edit-in-progress should also
  guard during this issue or as a follow-up.
