## Why

Creating an Epic is defining a product goal, not filling in a plain title. Today the Create/Edit Epic forms (`EpicCreateDialog`, `EditEpicDialog`) are bare title/description/priority inputs: there is no guidance to write the Goal / Background / Non-goals / Scope that make a milestone legible, creation closes the dialog and silently drops the user back on the list, and the `Epic created` toast implies the Epic is executing when it actually starts `idle` and needs an explicit Start. The Create/Edit flow should become a lightweight planning entry point that helps the user express the milestone and continue planning its linked issues.

## What Changes

- The Create/Edit Epic description area SHALL provide guided structure (Goal / Background / Non-goals / Scope) for empty or simple descriptions, while remaining a free-form markdown editor.
- Create Epic SHALL offer a choice after success: navigate to the new Epic detail page to plan linked issues, or stay on the current page.
- The create-success feedback SHALL convey that the Epic is created `idle` / ready to plan, and SHALL NOT imply it has started executing.
- Edit Epic SHALL preserve the existing markdown description and SHALL NOT force-rewrite or override existing content with the template.
- The Create/Edit forms SHALL remain usable on mobile: no horizontal scrolling, and the primary fields stay operable when the soft keyboard is open.
- Empty or simple descriptions SHALL still let the user create quickly without being forced into the full template.

## Capabilities

### New Capabilities
- `epic-create-flow`: The Epic Create/Edit form experience — guided milestone description (Goal / Background / Non-goals / Scope), post-create navigation choice (continue to the new Epic detail to plan issues or stay), an idle-aware creation message, markdown-preserving editing, and mobile operability of the forms.

### Modified Capabilities
<!-- None. This change does not alter the Epic persistence model, creation API fields, or lifecycle transitions, so no existing spec-level requirements change. -->

## Impact

- **Web UI** (`packages/web`):
  - `features/create-epic/ui/EpicCreateDialog.tsx` — guided description, post-create navigation choice, idle-aware messaging.
  - `features/edit-epic/ui/EditEpicDialog.tsx` — preserve existing markdown, non-destructive template guidance.
  - `entities/epic/api/queries.ts` — `useCreateEpic` success handling (returns new epic id/number for navigation; idle-aware toast).
  - `pages/epics/ui/EpicListPage.tsx` — host the post-create navigation target.
- **Server / API / Runner**: No change. The Epic create/update API fields are unchanged (Non-Goal); this is a presentation-layer flow change.
- **Dependencies**: No new runtime dependencies.
