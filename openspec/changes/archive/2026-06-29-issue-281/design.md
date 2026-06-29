## Context

Creating an Epic is defining a product milestone, but the current Create/Edit forms are bare inputs with no structure, no post-create path, and messaging that implies the Epic starts executing.

Current state (all presentation-layer, `packages/web`):

- `features/create-epic/ui/EpicCreateDialog.tsx` — plain title `Input`, description `Textarea` (marked `required`), priority `Select`. On success it resets state and calls `onClose()`, dropping the user back on the list. No template, no navigation choice.
- `features/edit-epic/ui/EditEpicDialog.tsx` — loads `epic.description` verbatim (already correct), but offers no guided structure.
- `entities/epic/api/queries.ts` — `useCreateEpic` fires `toast.success('Epic created')` (implies nothing about lifecycle) and ignores the returned `Epic`. Its `mutationFn` already resolves to the created `Epic` (with `id` + `number`) via `createEpic`.
- Routing (`app/App.tsx`): `epics/:id` → `EpicDetailPage`. `EpicListPage` navigates with `useProjectPath()(\`/epics/${epic.id}\`)`.
- `DialogContent` is `fixed` + centered, `max-w-[calc(100%-2rem)]`, with no internal scroll region — tall content (template scaffold + fields) risks vertical overflow on mobile and an obscured submit when the soft keyboard is open.

Constraints:

- **Non-Goal (from proposal):** no change to the Epic create/update API fields or the persistence model. The template is a presentation scaffold; only the resulting markdown string is sent.
- The Epic create mutation already returns the new `Epic`, so navigation needs no API change.

Stakeholders: this change is scoped to the Web UI; Server / Runner / CLI are untouched.

## Goals / Non-Goals

**Goals:**
- Guide the Create description toward Goal / Background / Non-goals / Scope while keeping it a free-form markdown editor.
- Let the user create quickly without forcing template completeness (incl. empty/no description).
- Offer an explicit post-create choice: navigate to the new Epic detail to plan issues, or stay.
- Make the create-success feedback idle-aware (created `idle`, needs explicit Start).
- Keep Edit Epic non-destructive: load existing markdown verbatim; template is opt-in only.
- Keep Create/Edit operable on mobile (320 / 390 / 430 px): no horizontal scroll, submit reachable with the keyboard open.

**Non-Goals:**
- No automatic issue breakdown or batch child-issue creation.
- No new create/update API fields or structured payload.
- No changes to Epic lifecycle transitions or persistence.
- No new runtime dependencies.

## Decisions

### 1. Shared markdown scaffold + detection helper in `shared/lib`

Add a small presentation module (e.g. `shared/lib/epic-description-template.ts`) exporting:
- `EPIC_DESCRIPTION_TEMPLATE` — the `## Goal` / `## Background` / `## Non-goals` / `## Scope` markdown scaffold (placeholder lines clearly marked, mirroring the existing `composeIssueTemplateBody` convention from CreateIssueDialog).
- `hasEpicDescriptionStructure(content)` — conservative detector that returns true only when Goal/Background/Non-goals/Scope headers are already present (used to decide auto-prefill vs. offer).

Rationale: both Create and Edit consume the same scaffold; a shared module avoids cross-feature (`create-epic` ↔ `edit-epic`) coupling and keeps the constant out of the entity layer (this is a presentation concern, consistent with the proposal's "presentation-layer flow change").
- *Alternative considered:* duplicate the constant in each feature — rejected (drift risk).
- *Alternative considered:* put it in `entities/epic/model` — rejected (it is a UI scaffold, not domain).

### 2. Create: auto-prefill only when empty; explicit Insert for non-empty

`EpicCreateDialog` initializes description to `EPIC_DESCRIPTION_TEMPLATE` **only when the field is empty** (the normal open path). If the dialog ever opens with existing simple text, it does **not** overwrite — instead it shows an "Insert template" action that appends the scaffold, preserving the user's text. This unifies Create and Edit behavior and satisfies "simple non-templated description still offers the structure … SHALL NOT silently overwrite".

- *Alternative considered:* always prefill regardless of existing content — rejected (would destroy user text, violating spec).

### 3. Edit: strictly opt-in Insert template

`EditEpicDialog` already loads `epic.description` verbatim — keep that. Add an "Insert template" action (visible when the description is empty, available on demand otherwise) that inserts the scaffold at the cursor/end. It is **never** applied automatically to existing content. Saving sends exactly the authored description (spec: stored description equals what the user authored).

- *Alternative considered:* auto-prefill empty descriptions in Edit — rejected by the spec ("template SHALL be inserted only when the user explicitly invokes the affordance").

### 4. Quick create: remove the description `required` constraint

Drop `required` on the Create description `Textarea` so empty sections, a cleared template, or no description all submit. Submission is blocked only on empty title (unchanged). The scaffold's placeholder lines are sent verbatim if the user leaves them — we do **not** auto-strip, so "stored description equals exactly what the user authored" holds.

- *Alternative considered:* strip unchanged placeholder lines before submit — rejected (violates the "exactly what the user authored" requirement and adds fragile parsing).

### 5. Post-create navigation choice as an in-dialog success state

On `createEpic.mutate` success, transition `EpicCreateDialog` to a success view driven by the returned `Epic` (`data.id`), showing:
- An idle-aware message: "Epic created as idle — start it to begin" (no "started"/"running" wording).
- Two explicit, always-reachable actions: **Open Epic** (`navigate(useProjectPath(\`/epics/${data.id}\`))` then close) and **Stay** (close, remain on the list; the new Epic appears via cache invalidation).

The dialog stays mounted until the user chooses; the X / overlay close still works (treated as Stay). Navigation uses the id returned by the create response — no API change.
- *Alternative considered:* close immediately and surface "Open Epic" as a Sonner toast action — rejected (action buttons are easy to miss/auto-dismiss; spec wants neither option hidden or disabled, which an in-dialog choice guarantees).
- *Alternative considered:* navigate to detail unconditionally — rejected (removes the "stay" option required by spec).

### 6. Move create-success messaging into the dialog; slim the shared hook

Move the success toast out of `useCreateEpic` (entity hook) into `EpicCreateDialog` (feature). The hook keeps `invalidateQueries(['epics'])` only; the dialog owns all success UX (idle-aware message + navigation choice). This aligns with the "presentation-layer" framing and avoids redundant double-feedback (toast + in-dialog panel).
- Verification: `useCreateEpic` is consumed only by `EpicCreateDialog`; removing the toast from the hook has no other blast radius.
- *Alternative considered:* keep an idle-aware toast in the hook **and** the in-dialog choice — rejected (noisy double feedback).

### 7. Shared `EpicDescriptionField` to enforce mobile-safe markup

Extract the description area (label + `Textarea` + optional Insert-template action) into a small shared field component used by both dialogs, applying: `w-full max-w-full`, `break-words` on user text, and a responsive `rows`. This centralizes the no-horizontal-overflow rules so both dialogs inherit them by construction, mirroring how `EpicListPage` enforces overflow invariants via shared markup.
- *Alternative considered:* re-apply the classes independently in each dialog — rejected (easy to diverge).

### 8. Make the dialog body internally scrollable for mobile reachability

Because `DialogContent` is `fixed` + centered with no scroll region, wrap the form fields in a region with `max-h-[calc(100dvh-...)] overflow-y-auto` and keep the action row (Cancel / Create, or the success Open/Stay row) in a `DialogFooter`-style sticky footer. This keeps the submit reachable when the soft keyboard is open and prevents vertical overflow once the template scaffold is prefilled. The existing `scrollWidth <= clientWidth` test pattern (already used in `EpicListPage.test.tsx`) is reused for the dialogs.

## Risks / Trade-offs

- [Pre-filled scaffold submitted as-is leaves placeholder lines in the stored description] -> Mitigation: placeholders are visually distinct; we intentionally do not strip them so storage matches authoring exactly. Trade-off accepted; documented in the field's help text.
- [In-dialog success state keeps the dialog mounted until the user chooses] -> Mitigation: X / overlay close remains available and is treated as Stay; no auto-navigation, so the user never loses their place unexpectedly.
- [Moving the toast out of the shared hook changes its contract] -> Mitigation: only consumer is `EpicCreateDialog` (verified); cache invalidation stays in the hook.
- [Template detection heuristic could misclassify borderline markdown] -> Mitigation: detector is conservative and only gates **auto-prefill on empty**; anything non-empty falls back to the explicit Insert action, so misclassification never destroys content.
- [`DialogContent` is `fixed`-centered; tall content + keyboard can obscure actions on small screens] -> Mitigation: internal scroll region + sticky footer (Decision 8); covered by mobile no-overflow/reachability tests.

## Migration Plan

This is a pure Web UI change; no Server / Runner / DB / API migration.

1. Land in one web PR: shared scaffold module + `EpicDescriptionField`, then `EpicCreateDialog` (template prefill, `required` removal, success/navigation-choice, idle-aware message), `EditEpicDialog` (opt-in Insert template), and `useCreateEpic` (drop toast, keep invalidation).
2. Verify: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web` (add unit tests mirroring `CreateIssueDialog.test.tsx` and `EpicListPage.test.tsx`: template prefill on empty, non-destructive on non-empty, quick-create accepted, navigate-vs-stay choice using returned id, idle-aware wording / no "started", Edit verbatim load + opt-in insert, mobile `scrollWidth <= clientWidth` at 320/390/430).
3. Rollback: revert the web package — no data or API shape to unwind.

## Open Questions

- Should the in-dialog success state auto-dismiss after a timeout, or always require an explicit choice? (Lean: always explicit, so both options stay reachable.)
- Should the user's last navigate-vs-stay choice be remembered as a future default? (Out of scope; treating as a non-goal for this change.)
