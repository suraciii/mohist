## Why

The issue detail page is the core operator surface, yet it does not contain its own content. Long repository git URLs force page-level horizontal scroll at common desktop widths, the right rail mixes seven different user intents (metadata, artifacts, runtime/session, configuration, actions) into one undifferentiated list, and the five-stage workflow bar compresses labels into unreadable controls on mobile. Operators must fight the layout before they can inspect state, artifacts, repository context, or take action, making the page feel fragile.

## What Changes

- Contain repository metadata (name, base branch, git URL) within the Details column so long git URLs wrap, break, or truncate (with tooltip/copy) instead of forcing horizontal page scroll
- Regroup the issue-detail sidebar by user intent: metadata, latest artifacts, runtime/session summary, configuration controls, and workflow actions become distinct, visually separated groups
- Separate safe inspection links (artifacts, transcripts, files) from state-changing actions (start, stop, retry, rerun, resume)
- Adapt the workflow stage bar for mobile using a compact current-stage display or scrollable stepper instead of squeezing five labels into unusable widths
- Ensure icon-only controls expose accessible names and adequate touch/click targets on both desktop and mobile
- Add responsive/component test coverage at desktop (≈1280px) and mobile (≈390px) widths

## Capabilities

### New Capabilities

<!-- None. All issue-detail rendering requirements already live under the web-ui capability; this change adds requirements to it. -->

### Modified Capabilities

- `web-ui`: Issue Detail gains requirements for layout containment (no page-level horizontal scroll when repository git URLs are present; bounded, readable repository metadata), responsive workflow stage navigation that stays operable on mobile, sidebar information-architecture grouping by user intent, separating safe inspection links from state-changing actions, and accessible icon-only controls with adequate touch/click targets.

## Impact

- **Frontend layout**: `IssueDetailPage.tsx` right-rail/grid structure and Details metadata rendering (`min-w-0`, overflow and word-break containment); `WorkflowView.tsx` `StageBar`/`StageBarCell` responsive adaptation; `LatestArtifactsPanel.tsx` and shared layout primitives.
- **Accessibility**: explicit accessible names on icon-only buttons (e.g. the issue edit control) and hit-target sizing that meets the local UI baseline for mobile.
- **Tests**: new/updated component tests in `IssueDetailPage.test.tsx` and `WorkflowView.test.tsx` covering desktop and mobile breakpoints, plus repository-URL containment.
- **No backend, API, data-model, workflow-semantics, or task-progress read-model changes** (out of scope per non-goals).
