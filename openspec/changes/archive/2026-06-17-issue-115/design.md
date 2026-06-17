## Context

This is a frontend-only UX-consistency cleanup for the Settings page (`/settings/*`), bundling four low-risk changes into one PR. The proposal and specs established the *what*; this design establishes the *how*, grounded in the **current** code state.

Grounding audit performed before writing this design (the issue body's file/line pointers are partly stale):

- **Workflow 404 root cause confirmed**: `packages/web/src/entities/settings/api/client.ts:111-113` — `getWorkflowProfile(id)` calls `request(... \`/workflow-templates/system/${encodeURIComponent(id)}\`)`. Profile ids contain `/` (e.g. `mohist/default`), so the slash is encoded to `%2F` and the backend `{*id}` catch-all route (`MapGet("/api/workflow-templates/system/{*id}")`) does not match. Fix is a one-character edit.
- **Duplicate header lives in the global app-shell, not `SettingsPage.tsx`**: `packages/web/src/widgets/app-shell/ui/Header.tsx` renders `<SidebarTrigger />`, an `<h1>{title}</h1>` (title derived from `usePageTitle()`, which returns `Settings` / `Settings · <Section>` for `/settings/*`), a runner-status pill, and a desktop-only `New Issue` button. `SettingsPage.tsx` itself only renders the tab navigation. The sidebar (`AppSidebar.tsx`) *also* offers `New Issue`, hence the duplication the issue targets.
- **Mutation hooks already emit sonner toasts**: `useAddRepository` / `useRemoveRepository` / `useSetDefaultRepository` (`entities/project/api/queries.ts`), `useSaveProjectTemplate` / `useDeleteProjectTemplateOverride` (`entities/template/api/queries.ts`), `useSetLogLevel`, `useUpdateOpencodeModel`, `useSetStageModels`, and `useSetAgentRuntime` (`entities/settings/api/queries.ts`) all already call `toast.success` / `toast.error`. The issue body's "useSetLogLevel currently has no toast" note is stale.
- **AgentSettingsSection double-reports**: `handleSave` calls `setAgentRuntime.mutateAsync` (whose hook already toasts) **and** sets its own local `saveSuccess` / `saveError` state rendered as inline green/red banners (`AgentSettingsSection.tsx:210,212,281-284,416-426`). This local banner state is the duplication to remove.
- **`unsupportedFields` mechanism is absent**: a repository-wide search for `unsupportedFields` in `packages/web/src` returns no matches. The Runtime form (`AgentSettingsSection.tsx`) renders a fixed `FIELDS` array with no server-driven field suppression. The issue's regression-guard requirement is therefore vacuous against current code; flagged in Open Questions.

Constraints: strictly frontend; no backend API/route/response-shape changes; no change to global toast config; no `useToastMutation` abstraction; do not regress `SettingsPage.test.tsx` (incl. the `useRepositoriesMock('proj-selected')` assertion); preserve field-level inline validation errors.

## Goals / Non-Goals

**Goals:**
- Fix the Workflows profile 404 so `mohist/default` resolves against the backend catch-all.
- Remove the duplicate page title + New Issue affordance above the Settings tab navigation while keeping the sidebar toggle and the sidebar's own New Issue.
- Unify Settings save feedback to sonner toasts by eliminating the surviving per-section inline *mutation* banner (AgentSettingsSection), keeping inline *field* validation.
- Remove the redundant Runtime/Command/Models summary from the Coder Agent section; keep the model selector and stage overrides.
- Keep the existing test green and document where the issue's assumptions diverge from code.

**Non-Goals:**
- Backend endpoint or route changes (the 404 is a client encoding bug).
- #19 scope (Runtime/System API fixes), #113 (Popover), #121 (agent.model resolution), #116 (Card visual redesign), #117 (IA/empty-state).
- A unified `useToastMutation` wrapper; global sonner config; visual unification of banner vs. toast styling.
- Introducing or altering any `unsupportedFields` mechanism (none exists today).

## Decisions

### Decision 1 — Workflow 404: drop `encodeURIComponent` in the client (one-line)

Edit `client.ts:112` to `request<WorkflowProfileDetail>(\`/workflow-templates/system/${id}\`)`. The id originates from `getWorkflowProfiles()` (server-controlled, already trusted as a path-like id) and flows through `useWorkflowProfile(id)`. No URL injection risk is introduced because the id is not user free-text.

- **Alternative considered**: encode only the segments after splitting on `/` (`id.split('/').map(encodeURIComponent).join('/')`). Rejected as needless complexity — the backend catch-all matches literal `/` and the id is server-supplied, so a verbatim interpolation is both simpler and matches the backend's mental model.

### Decision 2 — Suppress the duplicate header in the global `Header.tsx`, scoped to `/settings/*`

The title `<h1>` and `New Issue` button physically live in `Header.tsx`, not `SettingsPage.tsx`. Rather than relocate sidebar logic, suppress only those two affordances on Settings routes:

- Keep `<SidebarTrigger />` (this is the "Toggle Sidebar" the acceptance criteria require to remain) and the runner-status pill.
- In `Header.tsx`, derive an `isSettingsRoute` flag (the settings branch already exists inside `usePageTitle()`) and conditionally hide the title `<h1>` block and the desktop `New Issue` button when on `/settings/*`.
- Leave `SettingsPage.tsx` and `AppSidebar.tsx` untouched so the sidebar's own New Issue / Settings nav stay intact.

- **Alternative A — Hide the entire `<Header>` on Settings and render a `SidebarTrigger` inside `SettingsPage`**: rejected; duplicates sidebar-trigger wiring and risks divergent mobile behavior.
- **Alternative B — Push the change into `SettingsPage.tsx` as the issue literally states**: rejected; the elements are not there, so this would be a no-op. Documented as an issue-body inaccuracy.

### Decision 3 — Rely on existing hook toasts; remove AgentSettingsSection's local mutation banner

Because every affected hook already toasts, the remaining work is removing the *component-local* mutation feedback in `AgentSettingsSection.tsx`:

- Delete `saveError` / `saveSuccess` state (lines 210, 212), their resets in `handleChange` (232-233) and `handleSave` (262-263, 281-282, 284), the reset-error path in `confirmReset` (297, 306), and the inline banner JSX (416-426).
- `handleSave` continues to call `setAgentRuntime.mutateAsync`; on rejection the hook's `onError` already fires `toast.error`. Wrap the `mutateAsync` call so a thrown error no longer sets local state (it can be re-thrown or left to the hook — the hook's `onError` runs regardless).
- **Keep** `validationErrors` and the per-field `<p className="text-xs text-red-600">` (line 197) inline rendering — that is field validation, explicitly out of scope for toast conversion.
- For the other Settings sections (Repositories, Templates, System Log Level, Coder Agent Model): **no code change required** — verified they already toast on success/error. This becomes a verification/consistency check, not an edit.

- **Alternative — keep a success banner and only drop the error banner**: rejected; the acceptance criteria call for toast-only mutation feedback, and `useSetAgentRuntime` already provides the success toast.

### Decision 4 — Delete the Coder Agent Runtime/Command/Models block

Remove `AiSettingsSection.tsx:68-86` (the bordered 3-column summary + its "Mohist does not configure AI providers…" note). Keep the `External Coder Agent` `<h3>`, `ModelSelect`, and the Stage Model Overrides block. `useOpencodeRuntime` may become unused in this component — drop its destructure (lines 12) if so to avoid an unused-variable lint, unless retained for the optional hint.

- **Optional "N models available" hint**: implement inline next to the `Default Coder Agent Model` label using the already-computed `coderModels.length`. Low cost, restores the only useful datum (model count) from the removed block. Recommended.

### Decision 5 — `unsupportedFields` regression guard: no-op, documented

No `unsupportedFields` mechanism exists in `packages/web/src`. The Runtime form renders a static `FIELDS` array with no server-driven field suppression. Therefore the regression-guard requirement is satisfied vacuously; no protective code is added, and no existing path can be broken. See Open Questions.

## Risks / Trade-offs

- `[Route-awareness leaking into a global Header widget]` → Mitigation: confine the suppression to the existing settings branch already computed for `usePageTitle()`; keep it a single boolean gate; cover with a Header test asserting the title/New Issue are absent under `/settings/*` and present elsewhere.
- `[Removing AgentSettingsSection's success banner removes the auto-dismiss "saved" cue]` → Mitigation: `useSetAgentRuntime.onSuccess` already fires `toast.success('Coder agent runtime updated')`, so the cue is preserved and unified, not lost.
- `[Stale issue assumptions (wrong file/line pointers, missing unsupportedFields, "no toast" claims)]` → Mitigation: every change is grounded in the audited current code; deviations are recorded here and in Open Questions so reviewers can reconcile against the issue body.
- `[Optional model-count hint could be seen as scope creep]` → Mitigation: it is explicitly optional in the spec; if review prefers minimalism it can be dropped without affecting any acceptance criterion.
- `[Weak prerequisites #113/#121 mean the Coder Agent Model toast cannot be fully runtime-verified now]` → Mitigation: the acceptance criterion for that item is explicitly "verify the toast call exists," which is statically checkable.

## Migration Plan

Frontend-only, single PR. No data migration, no backend deploy, no feature flag.

- Deploy: merge + standard web build. Behavior is purely additive/visual.
- Rollback: `git revert` of the merge commit; no state to restore.
- Verification order (independent): (1) workflow detail open → 200; (2) Settings header no longer shows title/New Issue, sidebar intact; (3) AgentSettingsSection Save/Reset no longer shows inline banners, toast fires instead, field errors still inline; (4) Coder Agent tab no 3-column block, model selector + stage overrides present. `SettingsPage.test.tsx` must remain green throughout.

## Open Questions

1. **`unsupportedFields` mechanism**: the issue's Design Principle #1 and a regression acceptance criterion reference a `Set<FieldKey>` `unsupportedFields` mechanism in `AgentSettingsSection.tsx:224`, but no such mechanism exists in the current code (line 224 is the `dirty` useMemo; repo-wide search finds zero occurrences). Is this a stale reference to a #19-planned feature, or a documentation error? **Default action: none** — treat the guard as vacuously satisfied. Confirm with the issue author if a stricter stance is wanted.
2. **Header suppression granularity**: should the runner-status pill also be hidden on Settings, or only the title + New Issue? **Default: keep the pill** (it is orthogonal global status, not the duplication the issue targets).
3. **Optional model-count hint**: implement now or defer to #116/#117? **Default: implement** (trivial, restores useful info).
