## Context

Issue 453 established one page-owned decision model and `IssueDecisionActionController`: desktop renders `IssueDecisionSurface`, narrow viewports render `MobileActionBar`, and both consume the same authorized action descriptors. Approval is detected by `RuntimeDecision.summary === 'approval-required'`, with `decision.approvalStage` identifying the stage. The controller already sends approve and `{ stage, body }` feedback requests through the existing APIs.

Approval evidence is currently outside that surface. `LatestArtifactsPanel` lists artifacts and opens `ArtifactContentViewer` dialogs, while the diff summary appears later in the reading flow. The desktop and mobile action renderers each own a separate bare send-back draft. Comments use `AttachmentComposer`, which forwards native textarea events but currently submits only from its button. Artifact, diff, feedback, and comment APIs already provide all required data and commands.

This is a Web-only composition and interaction change. The Server remains authoritative for approval availability and workflow transitions. Primary stakeholders are issue owners reviewing Plan and Check approvals on phone and desktop viewports, including keyboard users.

## Goals / Non-Goals

**Goals:**

- Replace the generic approval presentation with one stage-aware review package at the existing top decision position.
- Inline the exact Plan or Check evidence with independent loading, missing, and failure states.
- Preserve one action descriptor list and one command controller across responsive layouts while making the two approval actions direct on a phone.
- Share one structured send-back draft and deterministic text serialization across desktop and mobile.
- Add safe, contextual desktop shortcuts and form-local Command+Enter submission.
- Preserve all non-approval issue-detail behavior.

**Non-Goals:**

- Changing approval stages, permissions, workflow profiles, feedback APIs, or persisted models.
- Blocking an authorized approval because evidence retrieval failed.
- Adding line-level diff review, comments on artifacts, or a JSON task editor.
- Adding issue-detail shortcuts to the application-wide shortcut catalogue or making shortcuts configurable.
- Redesigning the lower workflow history, changed-files page, or general artifact browser.

## Decisions

### Add one page-local approval review package

Add `ApprovalReviewPackage` under `pages/issue-detail/ui` and mount it in the current decision-surface frame whenever the derived summary is `approval-required`. The page will pass the current issue number, workflow run ID, approval stage, decision copy, shared action descriptors/controller, primary action, viewport mode, and diff query state. The package will own approval-only artifact queries, send-back draft state, and approval shortcut registration.

On desktop, the package will compose the existing `IssueDecisionSurface` with inline evidence in its evidence slot. On narrow viewports, it will render the same evidence plus an approval-specific fixed, safe-area-aware action bar containing direct Approve and Send back controls. Approve will dispatch the existing descriptor immediately; Send back will reveal and focus the non-modal `SendBackFeedbackForm` inline within the review package. The approval path will not open `MobileActionBar`'s generic action sheet or `ConfirmationDrawer`. The package, rather than `IssueDetailPage`, will choose between responsive approval renderers so its send-back draft survives live viewport changes. For non-approval states, `IssueDetailPage` will retain the existing desktop surface and generic mobile bar unchanged.

The lower `LatestArtifactsPanel` and standalone diff-summary banner will be suppressed while the approval package presents those same facts. Workflow history, sessions, task progress, changed-files navigation, and comments remain in the reading flow.

Alternative considered: add artifact queries and approval branches directly to `IssueDetailPage`, `IssueDecisionSurface`, and `MobileActionBar`. This is locally incremental but enlarges the page orchestrator and keeps approval state split across responsive renderers, so it is rejected.

Alternative considered: reuse the current mobile launcher and expose approval actions in its modal sheet. That preserves generic mobile behavior but requires an extra tap before Approve or Send back and separates the actions from the review package, so approval mode receives direct controls instead.

### Resolve evidence from the approval stage and current run

A single page-model map will define required evidence: Plan maps to `proposal.md` and `tasks.json`; Check maps to `review.md` plus the diff summary. Artifact lookup will query each required path exactly and use the returned artifact ID to fetch content. It will not match display names, suffixes, or array positions. Each evidence item will independently render list loading/error, missing artifact, content loading/error, unexpected directory, or readable text so one failure cannot hide other evidence.

Extend the artifact-list query key with the current `workflowRunId` as a cache scope while leaving the HTTP request unchanged. The server route still resolves the issue's current run, but the run-scoped key prevents a newly started run from briefly reusing cached evidence from its predecessor. Unknown custom approval stages will keep their authorized actions and show that no inline evidence is configured; evidence availability never changes controller authorization.

Alternative considered: fetch the unfiltered artifact list once and locate files client-side. That saves small metadata requests but duplicates the server's latest-exact-path selection rule and can accidentally rely on result ordering, so exact path queries are preferred.

### Reuse content rendering and extract a compact diff presentation

Extract the text-content branch from `ArtifactContentViewer` into a presentation component reusable by the dialog and inline package. Markdown remains rendered by `MarkdownReader`; JSON and other text remain raw recorded content in a wrapped `pre`. The inline container will enforce `min-width: 0`, `white-space: pre-wrap`, word breaking, and overflow wrapping so long tokens do not widen the page. `tasks.json` will not be parsed or reserialized.

Extract the existing branch/files/additions/deletions markup into a compact issue-diff summary that accepts the existing query result states. The package will distinguish transport failure from `available: false` and show either inline without suppressing `review.md`.

Alternative considered: build a task-specific JSON UI and reuse the full changed-files panel. Both add interaction and visual weight beyond review evidence, so raw readable JSON and a compact summary are retained.

### Share one controlled structured send-back form

Create a page-local controlled `SendBackFeedbackForm` used by both responsive action renderers. Its draft is `{ category: 'direction' | 'scope' | 'detail' | null, body: string }`; category is presented as an accessible single-choice segmented control and free text remains required. On a phone, the direct Send back control reveals this form inline and moves focus to it without modal semantics. Opening, cancellation, validation, pending state, hints, and submission operate on the package-owned draft rather than renderer-local state.

A pure serializer will produce one stable body:

```text
Category: Direction

<trimmed free text>
```

Submission requires a category and nonblank text, then passes the serialized body to the existing `controller.runAction(sendBackAction, { sendBackBody })`. `FeedbackHistory` already renders the body with preserved whitespace, so the category and request remain visible without a response-model or API change.

Alternative considered: add a structured feedback request with separate category and body fields. The workflow contract intentionally remains text-only, and a schema change would affect Server persistence and consumers without adding value to this guided form, so it is rejected.

### Keep shortcuts contextual and route them through visible controls

Add a page-local approval shortcut hook that listens only while a desktop approval package is mounted. Unmodified, non-repeating `a` and `m` keystrokes will be ignored for editable targets and when the matching action is unavailable or any action is pending. Otherwise `a` dispatches the existing approve descriptor through the controller, and `m` opens the shared send-back form. The hook will use the established `useNarrowViewport` boundary and window-listener cleanup pattern.

Command+Enter remains form-local. `SendBackFeedbackForm` and `IssueCommentsSection` will call the same submit callback used by their buttons from `onKeyDown`, while guarding validation, pending state, key repeat, and IME composition. Plain Enter is untouched. `AttachmentComposer` already forwards `onKeyDown`, so no shared component change is required. Visible `<kbd>` hints live beside the applicable desktop actions and submit controls; contextual `a`/`m` bindings will not be added to the global settings shortcut registry.

Alternative considered: register all three behaviors in a global shortcut bus. The bindings depend on the active issue state and focused form, so global ownership would leak page state and make shortcut availability harder to keep aligned with visible controls.

## Risks / Trade-offs

- `[Risk] Cached artifacts from an earlier workflow run appear in a new approval` -> Include `workflowRunId` in artifact-list cache identity and test a run-ID transition.
- `[Risk] One evidence request fails and collapses the whole package` -> Model and render each artifact and diff state independently.
- `[Risk] Evidence failure encourages approval without review` -> Show a prominent inline unavailable state, but keep server-authorized actions available rather than introducing a new client-side workflow gate.
- `[Risk] Desktop and mobile feedback behavior diverges again` -> Keep one draft, serializer, form component, and controller path; allow only layout to differ.
- `[Risk] Single-key shortcuts approve accidentally while typing or using modified browser commands` -> Require an unmodified, non-repeating key outside editable targets and disable dispatch while unavailable or pending.
- `[Risk] Long Markdown or JSON causes mobile overflow` -> Apply `min-width: 0` through the package and explicit wrapping to preformatted/unbroken content; verify document width in a real browser.
- `[Risk] Two fixed mobile actions become cramped or overlap safe areas` -> Use stable two-column control dimensions, allow labels to wrap without resizing the bar, reserve page bottom padding, and verify both phone approval stages in Chromium.
- `[Trade-off] A Plan package makes up to two exact-path list requests and two content requests` -> Prefer deterministic server selection and independent states; TanStack Query caches each result.
- `[Trade-off] Category is encoded as readable text rather than structured data` -> Keep one stable prefix format and do not introduce parsing or category-dependent workflow behavior.

## Migration Plan

1. Add the approval evidence map, category serializer, and focused unit tests, including run-scoped artifact query keys.
2. Extract reusable artifact text and compact diff-summary presentations without changing existing dialog or non-approval output.
3. Add `ApprovalReviewPackage`, independent Plan/Check evidence states, and the direct drawer-free mobile approval bar at the existing top decision slot.
4. Replace renderer-owned send-back state with the shared controlled inline form, then add approval and Command+Enter keyboard handlers and hints.
5. Suppress duplicate lower artifact/diff summaries only during approval and retain all non-approval composition.
6. Add component/spec coverage for Plan and Check packages, failures, serialization/history, direct mobile action completion, action uniqueness, shortcut guards, and comment submission. Extend the focused browser spec with phone Plan and phone Check workflows that verify wrapping, horizontal overflow, safe-area clearance, one-tap Approve, one-tap inline Send back opening/submission, and absence of the generic action drawer.
7. Run Web typecheck and test suites, then the focused browser specification.

No data migration or staged backend rollout is required. The change ships in one Web bundle against unchanged APIs. Rollback is a Web-code revert; no data repair or compatibility step is needed.

## Open Questions

None. The proposal and capability specs fix the evidence set, feedback categories, keyboard commands, and unchanged API boundary needed for implementation.
