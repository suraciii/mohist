## Context

Issue Detail Page currently renders `issue.body` and `comment.body` with `whitespace-pre-wrap`, so Markdown syntax is exposed as raw text in the primary issue reading surface. The web package already depends on `react-markdown`, and existing components (`SessionTranscriptView`, `ExploreMessage`, `ReviewReportModal`) prove the dependency works in this UI stack.

The affected behavior is read-side rendering only. Issue editing, comment creation, comment deletion, issue data loading, and backend APIs should remain unchanged.

## Goals / Non-Goals

**Goals:**

- Render issue descriptions and comments as Markdown on Issue Detail Page.
- Keep typography visually aligned with the current page: small text, gray palette, compact spacing, and readable line height.
- Give inline code and fenced code blocks clear visual treatment with gray backgrounds, monospaced font, rounded corners, and horizontal scrolling for long lines.
- Collapse long descriptions by default around the 600px threshold and provide an explicit expand/collapse button.
- Keep the implementation localized and easy to reuse within `IssueDetailPage.tsx` or a small colocated component.

**Non-Goals:**

- Add syntax highlighting or broad Markdown plugin expansion beyond the minimal GitHub-flavored Markdown support required for strikethrough and bare URL autolinks.
- Change how Markdown is stored, edited, submitted, or loaded from the API.
- Add a live Markdown preview to the edit dialog or comment composer.
- Persist the expanded/collapsed state across navigation or reloads.

## Decisions

### D1: Use `react-markdown` with explicit component styling

Use `react-markdown` for both description and comments, with a shared rendering wrapper that maps common Markdown elements to Tailwind classes. Add `remark-gfm` if required to satisfy strikethrough and bare URL autolink support. Code styling should match the spirit of `SessionTranscriptView`: inline code uses `bg-gray-100`, `font-mono`, `rounded`, compact padding; fenced code is rendered as a block with `bg-gray-50`, `font-mono`, `rounded`, padding, and `overflow-x-auto`.

This keeps Markdown behavior consistent with existing app patterns while allowing Issue Detail Page to preserve its current `text-sm` and gray visual system.

**Alternatives considered:** Use `@tailwindcss/typography` `prose` classes only. This is simpler, but gives less control over compact issue-page spacing and code block consistency with `SessionTranscriptView`. Avoid GFM support. This would miss strikethrough and bare URL autolinks from the issue requirements. Add syntax highlighting. This improves code readability but requires more dependency and theme decisions than the current acceptance criteria require.

### D2: Extract a small Markdown renderer instead of duplicating JSX

Create a small `MarkdownContent` style component or helper near the issue detail implementation, then use it for both `issue.body` and `comment.body`. The component should accept content and an optional className/color variant rather than exposing Markdown component internals at each call site.

This keeps the interface simple and pulls Markdown styling complexity downward into one place, reducing future drift between description and comment rendering.

**Alternatives considered:** Inline two separate `<Markdown components={...}>` blocks directly in Description and Comments. This is initially smaller but duplicates styling knowledge and makes future Markdown tweaks more error-prone. Create a global Markdown component immediately. That may be useful later, but this change only needs Issue Detail Page behavior and should avoid broad refactors.

### D3: Implement description collapse with local UI state and CSS max height

Use local React state such as `descriptionExpanded` to toggle a wrapper around the rendered description. When collapsed, apply `max-h-[600px] overflow-hidden` and show a fade or boundary plus an expand button. When expanded, remove the max height and show a collapse button.

The implementation can always show the button for non-empty descriptions or use a small measurement pass to show it only when content exceeds the threshold. If measurement is used, it should be contained in the description section and should not affect comments or data loading.

**Alternatives considered:** Store expansion state per issue in URL params or local storage. That adds persistence complexity with little value for a readability control. Truncate by character count before rendering. That can break Markdown structure and code fences, so CSS height clipping is safer.

### D4: Keep comments fully expanded

Apply Markdown rendering to comments but do not add per-comment collapse behavior. Comments are already separated into individual entries and have their own management actions; adding collapse controls to every comment would add UI noise and is outside the requirement.

**Alternatives considered:** Collapse long comments too. This may be useful later, but the issue specifically calls out long descriptions as the first-screen density problem.

## Risks / Trade-offs

- [Risk] Markdown output spacing may feel too loose compared with the current compact page if default element margins are used. → Mitigation: explicitly style headings, paragraphs, lists, blockquotes, and rules with compact Tailwind classes rather than relying entirely on browser defaults.
- [Risk] Raw HTML in issue text could render unexpectedly if Markdown configuration is expanded later. → Mitigation: keep default `react-markdown` behavior and do not enable raw HTML plugins.
- [Risk] CSS height clipping can cut through the middle of a code block when collapsed. → Mitigation: provide a clear expand button immediately below the clipped region so the full content is one click away.
- [Risk] Duplicating Markdown styling across app surfaces can drift over time. → Mitigation: centralize the Issue Detail Page description/comment styling in one helper, and consider promoting it to a shared component only if another page needs the same contract.

## Migration Plan

1. Import `react-markdown` in the Issue Detail Page implementation or in a small colocated Markdown renderer.
2. Add `remark-gfm` to the web package if it is not already available and wire it into the renderer for strikethrough and bare URL autolinks.
3. Replace the raw `issue.body` and `comment.body` text containers with the shared Markdown renderer.
4. Add local description expanded/collapsed state and the 600px collapsed container behavior.
5. Verify existing issue edit, add-comment, and delete-comment flows still call the same mutations and update the same query keys.
6. Run the web build or relevant tests to catch TypeScript and rendering regressions.

Rollback is straightforward: restore the two read-side containers to plain text rendering and remove the local collapse state/imports. No data migration is required.

## Open Questions

- Should the expand/collapse button be shown only when measured rendered height exceeds 600px, or is always showing it for non-empty descriptions acceptable for the first implementation?
