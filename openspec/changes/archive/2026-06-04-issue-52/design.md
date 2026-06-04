## Context

The Issue Detail page is the primary surface users consult to understand which workflow will run for an issue. Today, the `IssueWorkflowProfileEditor` widget (mounted at `IssueDetailPage.tsx:503`) renders a single, unconditional view: a header, a large YAML `<Textarea>`, an error block, and a `Save` button. The textarea's React `placeholder` is the literal string `"Loading workflow profile..."`.

When the workflow-profile read endpoint returns `yaml: null` and `hasCustomTemplate: false` (i.e. the issue is purely inheriting its profile — observed on `/issues/52` with `profileId: mohist/default`, `updateMode: Reference`), the component still mounts the textarea. Because the draft is empty, what the user sees is:

- a big empty editor,
- the literal text `Loading workflow profile...` sitting in the editor's empty placeholder slot — so the page looks stuck after data has loaded,
- a `Profile: mohist/default` caption,
- a disabled `Save` button that cannot do anything.

In parallel, the Issue Detail DETAILS sidebar (`IssueDetailPage.tsx:724-731`) renders a second `Workflow Profile: <profileId>` row, so the profile identity is duplicated between the main card and the sidebar. The sidebar should be reserved for issue metadata (stage, project, repository).

The bug is a UI state-modeling problem, not a backend problem: the workflow-profile read response already distinguishes `Reference` from `Custom` via `updateMode` and `hasCustomTemplate`, and the backend already exposes a `DELETE /projects/:projectId/issues/:number/workflow-profile/template` route that clears the custom template. We just need to render the right state on the Web side and label the active-run YAML surface so users do not confuse it with the issue profile.

The active run YAML is exposed on Issue Detail through `WorkflowYamlDialog` (`IssueDetailPage.tsx:149-186`, mounted at line 569-570). Its trigger currently reads `Workflow Definition (YAML)` / `View`, and the dialog title is `Workflow Definition` — wording that, given the new card identity, can read as workflow profile configuration. It should be re-labeled as runtime output.

## Goals / Non-Goals

**Goals:**

- Render the Workflow Profile card on Issue Detail as a state-routing widget with four distinct states: `loading`, `error`, `reference` (inherited, `yaml: null` + `hasCustomTemplate: false`), `custom` (`yaml` populated + `hasCustomTemplate: true`).
- In `reference` state show a compact read-only summary listing `Profile`, `Mode` (`Inherited`), `Template` (system default or project default), and `Overrides` (`None`); do not render a textarea or `Save` button; expose a `Customize profile` action that opens the editor.
- In `custom` state keep the existing YAML editor and its save / dirty / validation / unsaved-changes behavior, label the editor explicitly as editing issue-owned workflow profile YAML (not active run YAML), and expose a `Revert to inherited profile` affordance backed by the existing `DELETE …/workflow-profile/template` endpoint.
- In `loading` state keep the existing skeleton and never render the `Loading workflow profile...` placeholder.
- In `error` state keep a compact error block with the failure message; do not use the editor placeholder as the error state; surface a `Retry` button.
- Remove the duplicate `Workflow Profile` row from the DETAILS sidebar; keep `Coder Model` and per-stage overrides in the ACTIONS sidebar untouched.
- Relabel the active-run YAML surface (`WorkflowYamlDialog`) as runtime output, not workflow profile configuration.
- Add Web test coverage for the four card states, the editor labeling, the absent placeholder, the revert affordance, and the sidebar de-duplication.
- If labeling the inherited source accurately needs a small additive read-model field, add it to the existing workflow-profile read endpoint rather than introducing a new contract.

**Non-Goals:**

- Building a new full workflow configuration sub-page.
- Redesigning project-level workflow variables or per-stage model configuration.
- Adding an effective variables preview.
- Making active run YAML editable.
- Changing workflow template / variable merge semantics.
- Touching Kanban, Epic detail, or any surface other than the Issue Detail page.

## Decisions

### D1: `IssueWorkflowProfileEditor` becomes a state-routing view

The current widget is a single render path that always mounts a `<Textarea>`. It will be restructured into a small router whose render branch is decided by the four states above. The contract between the four branches is explicit:

- `loading`: skeleton only, no `<Textarea>`, no `Save` button. The string `Loading workflow profile…` MUST NOT be the visible state.
- `error`: a compact error block, no `<Textarea>`, no `Save` button. A `Retry` button is wired to `refetch` from `useIssueWorkflowProfileYaml`.
- `reference`: a read-only summary card (Profile / Mode / Template / Overrides), no `<Textarea>`, no `Save` button. A `Customize profile` button flips a local `mode` to `editing`.
- `custom`: the existing editor + `Save` + dirty/validation behavior, plus a `Revert to inherited profile` button.

`mode` is local React state in the widget (default `view`). Transitions:

- `view → editing`: triggered by `Customize profile` from the reference summary, or by clicking the editor area when already in `custom` (no-op; default state).
- `editing → view`: triggered by `Revert to inherited profile` (after the server confirms the delete and the query refetches the `reference` summary), or by cancelling an unsaved draft (no server call).

**Alternatives considered:**

- Use a URL search param (`?profileMode=editing`) for the edit toggle so the state is shareable — rejected for this change. The editor is a card-level UI state, not a deep-link target, and adding query-string plumbing in a follow-up is cheap.
- Keep one big form and just hide the textarea in `reference` — rejected; the existing wiring always re-derives `serverYaml` / `draftYaml` from the query data, so leaving the form in place would still let `setServerYaml(null)` race and cause flicker. A clean branch is simpler to reason about and test.

### D2: Keep the custom-mode editor behavior verbatim, only add labeling and a revert button

The existing editor (`IssueWorkflowProfileEditor.tsx:79-161`) already implements `isLoading` / `fetchError` / draft / dirty / save success / save error correctly. We:

- move the existing `loading` and `error` early returns into clearly named branches,
- keep the `<Textarea>` block as the `custom` branch, with the same `draftYaml` / `serverYaml` / `validationErrors` / `saveSuccess` state,
- add a small heading under the `Workflow Profile` card title that reads `Edit this issue's own workflow profile YAML` (or equivalent) so the user knows it is issue-owned, not active run YAML,
- add a `Revert to inherited profile` button (small, secondary) in the editor toolbar that calls the existing `DELETE` endpoint and refetches.

**Alternatives considered:**

- Add a separate `Reverted` local state flag — rejected; the truth source is the server response. After a successful delete the query refetches, the response comes back with `hasCustomTemplate: false` and `yaml: null`, and the router re-renders the `reference` branch.
- Make the revert clear only the local draft and rely on next reload — rejected; the spec says reverting must "return the card to the reference / inherited summary state", which requires the server state to actually clear, otherwise the next refetch will yank the user back to `custom`.

### D3: Add a small additive `templateSource` field on the read response

The reference summary needs to label the inherited source as `System default` or `Project default`. The current response already exposes `SourceTemplateId` and `HasCustomTemplate`, from which a client could derive the source — but a derived field is clearer in the contract and keeps the Web side from re-implementing server business rules. We extend `IssueWorkflowProfileResponse` (`packages/server/.../IssueRoutes.cs:661`) and the matching TS type (`packages/web/.../entities/issue/model/types.ts:644`) with an optional `TemplateSource` field whose value is one of:

- `"system"` — no project template reference and no custom template (e.g. `profileId: mohist/default`),
- `"project"` — `SourceTemplateId` is set, no custom template,
- `"custom"` — `HasCustomTemplate` is true.

Computation lives in `BuildIssueWorkflowProfileResponseAsync` (`IssueRoutes.cs:585`) and is purely derived from existing inputs — no schema migration, no new column, no new endpoint. The field is additive: old clients ignore it, the new client renders `Template: System default` / `Project default` / `Custom` accordingly.

**Alternatives considered:**

- Derive `templateSource` purely on the client from `SourceTemplateId` / `HasCustomTemplate` — rejected; the server already knows which project template resolved, and shipping the label keeps the Web layer thin.
- Add a richer `effectiveTemplate` payload — out of scope; the spec only requires the label.

### D4: Remove the duplicate `Workflow Profile` row from the DETAILS sidebar

`IssueDetailPage.tsx:724-731` renders `{issue.workflowProfileId && (…Workflow Profile…)}`. The new card carries the same identity, so the sidebar row is removed. The sidebar retains `Issue Stage`, `Workflow Stage`, `Project`, `Repository`, and the existing model / stage overrides. No visual redesign.

**Alternatives considered:**

- Move the row to a less-prominent position in the sidebar — rejected; the card is the agreed single source of truth.
- Add a new `Effective profile` row summarizing only the override status — out of scope.

### D5: Relabel `WorkflowYamlDialog` so it is unmistakably runtime output

`WorkflowYamlDialog` (`IssueDetailPage.tsx:149-186`) currently shows `Workflow Definition (YAML)` / `View` on the trigger and `Workflow Definition` as the dialog title. We change:

- trigger label to `Active run YAML` (or equivalent) and the trailing `View` chip stays,
- dialog title to `Active run YAML`,
- a small caption clarifying that this is the active workflow run's rendered YAML, not the issue's workflow profile configuration.

This is a copy change in one component, no behavior change.

**Alternatives considered:**

- Hide the dialog entirely on `reference`-mode issues — rejected; users still need to inspect what the active run is doing.
- Move active-run YAML into the new card — rejected; the spec wants the card focused on the issue profile, and the active run is a different concept (runtime output, not configuration).

### D6: Test coverage at the widget and page level

`packages/web/tests/IssueWorkflowProfileEditor.test.tsx` is extended with four scenario blocks (reference, custom, loading, error) that already match the spec; `packages/web/src/pages/issue-detail/ui/IssueDetailPage.test.tsx` gains a sidebar de-duplication test that asserts the `Workflow Profile` label is not present in the DETAILS card. All tests are independent of network — they mock `useIssueWorkflowProfileYaml` per scenario.

**Alternatives considered:**

- Add a separate `IssueWorkflowProfileEditor.view.test.tsx` for the new branches — rejected; keeping one test file with grouped `describe` blocks is consistent with the existing style.
- Drive the four states through Storybook — out of scope; we ship Vitest coverage first.

## Risks / Trade-offs

- [Risk] The four-state branch can regress into "empty editor with placeholder" if a future refactor re-introduces the textarea as the default → Mitigation: the test for `reference` state asserts that no `<textarea>` element is rendered and that the string `Loading workflow profile...` is not in the document; the test for `loading` state asserts the same string is not used.
- [Risk] `DELETE …/workflow-profile/template` may not exist in older deployments, breaking the new revert button → Mitigation: the revert button only appears in `custom` mode; if the call fails, the editor surfaces the existing `validationErrors`-style error path and the user can retry. The `reference` / `custom` rendering itself depends only on the read endpoint, which already exists.
- [Risk] Adding `TemplateSource` to the response record could leak an internal classification (e.g., project template namespace) if implemented naively → Mitigation: the value is one of the three fixed strings, computed from existing inputs; no internal IDs are exposed.
- [Risk] Removing the DETAILS sidebar row may make the right column feel sparse → Mitigation: the row is being removed because the same identity is in the main card; the sidebar already has other items (stage, project, repository, workflow stage, model, overrides) that keep it balanced. No other sidebar items change.
- [Risk] Local `mode` state can get out of sync with server state if the user navigates away and back → Mitigation: on remount, `mode` resets to `view` and the router falls back to whatever branch the server response puts us in; any in-flight revert on a previous mount is irrelevant because the query refetches on every mount.
- [Risk] jsdom does not fully simulate React Query lifecycle for the four states → Mitigation: tests mock `useIssueWorkflowProfileYaml` directly (already the pattern in `IssueWorkflowProfileEditor.test.tsx`) and set `isLoading` / `error` / `data` per scenario; no real fetch is performed.

## Migration Plan

1. Backend (`packages/server/src/Mohist.Server`):
   - Extend `IssueWorkflowProfileResponse` (`Api/IssueRoutes.cs:661`) with a new positional / named `TemplateSource` of type `string` (values: `"system" | "project" | "custom"`).
   - In `BuildIssueWorkflowProfileResponseAsync` (`Api/IssueRoutes.cs:585-615`) compute `TemplateSource` from `template`, `state.SourceTemplateId`, and `state.HasCustomTemplate`.
   - No schema migration, no new endpoint, no breaking change to the read shape.
2. Web client types (`packages/web/src/entities/issue/model/types.ts`):
   - Add optional `TemplateSource?: 'system' | 'project' | 'custom'` on `IssueWorkflowProfileYamlResponse` (line 644) so the field is tolerated even if the backend has not been redeployed.
3. Web client widget (`packages/web/src/widgets/issue-workflow/ui/IssueWorkflowProfileEditor.tsx`):
   - Restructure the body into four explicit branches: `loading`, `error`, `reference`, `custom`.
   - Add local `mode` state (`'view' | 'editing'`) defaulting to `'view'`.
   - `reference` branch renders the read-only summary + `Customize profile` button.
   - `custom` branch renders the existing editor + `Revert to inherited profile` button that calls a new `deleteIssueWorkflowProfileTemplate` helper and refetches.
   - `loading` / `error` branches are the existing skeleton / error block, with the `error` branch gaining a `Retry` button that calls `refetch`.
4. Web client entity (`packages/web/src/entities/issue/api/client.ts` and `queries.ts`):
   - Add `deleteIssueWorkflowProfileTemplate(number, projectId)` calling `DELETE …/workflow-profile/template`.
   - Add `useDeleteIssueWorkflowProfileTemplate` mutation that invalidates the same query key as the update mutation.
5. Web client page (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`):
   - Remove the `Workflow Profile` row at lines 724-731.
   - Update `WorkflowYamlDialog` (lines 149-186) trigger label to `Active run YAML`, dialog title to `Active run YAML`, and add a one-line caption clarifying it is runtime output.
6. Tests:
   - Extend `packages/web/tests/IssueWorkflowProfileEditor.test.tsx` with the four `describe` blocks required by the spec.
   - Add a sidebar de-duplication assertion in `packages/web/src/pages/issue-detail/ui/IssueDetailPage.test.tsx` (mock `useIssueWorkflowProfileYaml` to return the reference state, render the page, assert the `Workflow Profile` label is not present in the DETAILS card).
7. Verification:
   - `npm run --workspace=packages/web test` (or the package's documented test command) for unit / component tests.
   - Manual: open `/issues/52` in mohist-local, confirm reference summary renders, no textarea / no `Save` / no `Loading workflow profile…` placeholder, sidebar row is gone, dialog says `Active run YAML`.

**Rollback:** the change is purely additive on the read response and additive in the Web client. Reverting the PR returns the editor to the prior always-textarea render and restores the sidebar row; the backend `TemplateSource` field is ignored by older clients. No data migration to undo.

## Open Questions

- None blocking. Two items to confirm with the reviewer before implementation:
  1. Exact copy for the active-run YAML surface (current proposal: `Active run YAML`). Acceptable alternatives: `Runtime workflow YAML`, `Workflow run YAML`.
  2. Whether the `Customize profile` button should be a primary CTA or a secondary text link. Current proposal: primary in `reference` mode (it is the only thing the user can do), secondary in `custom` mode (paired with the editor).
