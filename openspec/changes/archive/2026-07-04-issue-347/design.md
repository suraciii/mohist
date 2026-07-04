## Context

The `/agents` list page (`packages/web/src/pages/agent-list/ui/AgentListPage.tsx`)
has two agent-creation entry points — the "New Agent" header button
(`AgentListPage.tsx:140-146`) and the `AgentEmptyState` "Create Agent" button
(`AgentListPage.tsx:149-152`). Both call `navigate(toProjectPath('/agents/new'))`.

The route table in `App.tsx` only registers `agents` and `agents/:agentId`
(there is no `agents/new`). As a result the segment `new` is captured as
`:agentId`, the user lands in `AgentDetailPage`, which calls `useAgent("new")`,
the backend returns 404, and the page renders a red "Failed to load agent."
banner with four 404s in the console. Net effect: **no agent can be created
from the UI**, even though the create capability itself is fully built and
self-contained.

The create capability lives in `AgentProfileEditor`
(`packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx`).
When mounted with `agent === null` it runs in create mode: `isEditing=false`,
`handleSave` calls `useCreateAgent`, and on success it (a) invalidates the
`['agents']` query and (b) navigates to `/agents/<new-id>`
(`AgentProfileEditor.tsx:99-117`). `AgentDetailPage` already consumes this
editor via local `editorOpen` state and conditional rendering
(`AgentDetailPage.tsx:92`, `:306-312`), so the pattern is established.

This is a P0 single-file wiring bug. Constraints:

- **No API, persistence, schema, or routing-table change.** The fix is purely
  client-side UI wiring.
- **No new dependency.** Everything needed already exists.
- **Codebase convention**: agent creation is a Dialog, not a page — every other
  create/edit surface in the agent domain uses `AgentProfileEditor` as a modal.

## Goals / Non-Goals

**Goals:**

- Make both create entry points on `/agents` open the `AgentProfileEditor`
  dialog in create mode instead of navigating.
- On a successful create, automatically refresh the list (via existing query
  invalidation) and route the user to the new agent's detail page (via the
  editor's existing navigate-on-success).
- Keep the change to a single production file (`AgentListPage.tsx`), mirroring
  the established `AgentDetailPage` pattern.
- Add regression specs asserting the dialog (testid `agent-profile-editor`)
  opens on click of either entry point — not asserting route changes.

**Non-Goals:**

- No new `agents/new` route. Creation is a Dialog, consistent with the rest of
  the codebase; adding a route would contradict that convention.
- No changes to `AgentProfileEditor` form logic, `AgentDetailPage`, `App.tsx`,
  the API, or persistence.
- No refactor of `AgentListPage` beyond what the fix requires.

## Decisions

### Decision 1: Reuse `AgentProfileEditor`'s create branch via local state

Add `const [editorOpen, setEditorOpen] = useState(false)` to `AgentListPage` and
point both create handlers at `setEditorOpen(true)`. Mount the editor in create
mode with `agent={null}`, mirroring `AgentDetailPage.tsx:306-312`:

```tsx
{editorOpen && (
  <AgentProfileEditor
    agent={null}
    open={editorOpen}
    onClose={() => setEditorOpen(false)}
  />
)}
```

Because `useCreateAgent` already invalidates `['agents']` and navigates to the
new agent's detail page on success (`AgentProfileEditor.tsx:106-115`), the list
page itself performs **no** navigation. `useNavigate`/`useProjectPath` remain in
use only for `AgentRow`, so no imports become dead.

**Alternatives considered:**

- **Add an `agents/new` route with a thin create page.** Rejected: creation is
  a Dialog everywhere else in the agent domain, the editor already handles
  create end-to-end, and a new route would add a route-table entry, a new page
  component, and a second navigation handoff for no behavioral gain.
- **Lift state into a shared parent (e.g. router context) so list and detail
  pages share one editor instance.** Rejected: cross-cutting state for a
  single-file bug; the two pages never need to coordinate, and the existing
  per-page `editorOpen` pattern is simpler and proven.

### Decision 2: Conditional rendering, not always-mounted with manual reset

Mount the editor with `{editorOpen && <AgentProfileEditor …/>}` rather than
keeping it persistently mounted and toggling `open`. This unmounts the
component on close, so `useState` initializers (`name`, `instructions`,
`skillsText`, `model`, `variant`, `errors`) re-run from a clean base on the next
open — satisfying the "reopening starts from a clean form state" requirement
without any explicit reset logic.

This is the exact pattern `AgentDetailPage` already uses, so it is also the
lowest-surprise choice for maintainers.

**Alternatives considered:**

- **Always-mounted with `open={editorOpen}` plus a `useEffect` that clears
  fields on close.** Rejected: more code, easy to forget a field, and the
  `useState(agent?.name ?? '')` initializers would still bind to the *first*
  mount's values on reopen. Conditional mount is strictly simpler.
- **Add a `key` prop to force remount while always-mounted.** Functionally
  equivalent to conditional rendering but with extra indirection; rejected in
  favor of the direct conditional form that matches `AgentDetailPage`.

### Decision 3: List page does not navigate on success

The editor's `onSuccess` already drives the post-create navigation to
`/agents/<new-id>` and the list refresh. The list page supplies no `onSaved`
callback and performs no `navigate`. This keeps a single source of truth for
"what happens after a create" (the editor) and avoids a double-navigation race.

**Alternatives considered:**

- **Pass an `onSaved` from the list page that also navigates.** Rejected:
  duplicates the editor's navigate-on-success, risks a race between two
  `navigate` calls, and gives the list page knowledge it does not need.

### Decision 4: Regression tests assert dialog presence, not route

Update `AgentListPage.test.tsx` to:

- Mock `AgentProfileEditor` with a lightweight stub exposing testid
  `agent-profile-editor` (keeps the spec focused on the list page's contract
  and avoids dragging in model/settings queries).
- For each entry point (`agent-list-create` and `agents-empty-create`): click
  and assert the editor stub appears in the DOM. Assert no navigation to
  `/agents/new` occurs (e.g. by asserting the URL is unchanged under
  `MemoryRouter`, or simply by not wiring any route for `/agents/new` so a
  stray navigation would surface as a missing-route error if it happened).
- Replace any prior assertion on route changes.

**Alternatives considered:**

- **Render the real `AgentProfileEditor` in the list-page spec.** Rejected: it
  pulls in `useAvailableModelIds`, `useModelVariants`, and the create mutation,
  widening the test's integration surface and coupling the list spec to the
  editor's internals. A stub is the right fidelity for a list-page spec.

## Risks / Trade-offs

- **[Risk] The stubbed editor in tests hides contract drift in the real
  editor.** -> Mitigation: the editor's own create-mode behavior is covered by
  its dedicated specs; the list-page spec only asserts the open/close wiring,
  which is its actual responsibility. Keep the stub minimal and pin it to the
  `agent-profile-editor` testid that the editor exposes today.
- **[Risk] A future change to the editor's create-mode success contract
  (e.g. dropping the navigate-on-success) would silently break the list page's
  "routes to detail page" expectation.** -> Mitigation: that contract is part
  of the editor's public behavior and is covered by the `agent-list-create`
  spec requirement that the list refreshes and the user reaches the detail
  page on success; document the dependency on the editor's existing contract
  in this design (Decision 3) so the coupling is explicit.
- **[Trade-off] Conditional rendering re-runs all `useState` initializers on
  each open.** This is desired (clean form) but means any expensive
  initialization in the editor would re-run on each open. Today the
  initializers are trivial (a few strings and a `readAgentModelAndVariant`
  call), so this is a non-issue; called out for completeness.
- **[Risk] If `useNavigate` becomes unused after removing the two create
  navigations, lint/tsc may flag it.** -> Mitigation: `AgentRow` still uses
  `useNavigate`, and `AgentListPage` itself keeps `useProjectPath` only where
  still needed; verify with `npm run typecheck -w packages/web` after the
  edit. Remove now-dead imports as part of the same change.

## Migration Plan

This is a pure UI wiring fix with no data, API, or route-table change, so there
is no data migration and no backward-compatibility surface.

**Deploy:**

1. Edit `packages/web/src/pages/agent-list/ui/AgentListPage.tsx` per Decisions
   1-3.
2. Update `AgentListPage.test.tsx` per Decision 4.
3. Verify locally: `npm run typecheck -w packages/web` and
   `npm run test:run -w packages/web`.
4. Manual smoke: open `/agents`, click "New Agent" → dialog opens, fill &
   submit → lands on `/agents/<new-id>`; repeat from empty state.

**Rollback:** Revert the single commit; the previous (broken) behavior is
restored with no side effects, since no persisted state or shared contract is
touched.

## Open Questions

None. The editor's create-mode contract, the conditional-mount pattern, and
the codebase's "creation is a Dialog" convention together make the design
fully determined. Any deviation surfaced during implementation should be
raised as a follow-up rather than resolved ad hoc.
