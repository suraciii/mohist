## Context

Mohist renders Markdown in four user/agent-facing surfaces today, each with a different implementation path:

- **Issue description & comments** — `IssueDetailPage.tsx` owns a page-local `MarkdownContent` function (lines ~100-125) that wraps `react-markdown` + `remark-gfm` and overrides only `code`/`pre`. The same page owns a 600px description collapse (`descriptionExpanded`, `descriptionBodyRef`, `scrollHeight > 600`, gradient overlay, Expand/Collapse button at lines ~207-238, ~552-575). An embedded `# heading` in the issue body becomes a page-level `h1` that competes with the issue title.
- **Artifact Markdown** — `ArtifactContentViewer.tsx` renders recorded content (including `proposal.md`, `design.md`, `review.md`, etc. with `contentType: 'text/markdown'`) through a single `<pre className="whitespace-pre-wrap break-words ...">` block (line ~113). Markdown is shown as raw preformatted text, not rendered.
- **Review reports** — `ReviewReportModal.tsx` calls `react-markdown` directly inside a `prose prose-sm max-w-none` wrapper (lines ~79-86). Out of scope for this change.
- **Session transcripts** — `SessionTranscriptView.tsx` and `AssistantParts.tsx` call `react-markdown` directly inside a `text-sm text-gray-800 leading-relaxed` wrapper. Out of scope for this change.

Stack is already pinned in the issue decision: `react-markdown` + `remark-gfm` + Tailwind typography. Shared UI primitives live under `packages/web/src/shared/ui/` (with `components/` for shadcn-style atoms). Tests are colocated in `packages/web/tests/` and follow React Testing Library + `renderWithQueryClient` patterns.

## Goals / Non-Goals

**Goals:**

- Ship one reusable `MarkdownReader` component that owns document typography, base heading remap, table/code/link overflow, copy-code, optional TOC/anchors, and Reader-level `full`/`collapsible` long-document behavior.
- Migrate the minimum required surfaces: issue description, issue comments, and artifact Markdown (when `contentType === 'text/markdown'`).
- Remove page-local description collapse state and the duplicate `h1` landmark from `IssueDetailPage`.
- Provide test coverage that asserts the Reader contract (heading remap, code overflow, table overflow, long link/path wrapping, collapsible/full modes).

**Non-Goals:**

- Do not migrate `ReviewReportModal`, `SessionTranscriptView`, or `AssistantParts` in this change. Those are explicit follow-ups; `MarkdownReader` just has to be ready for them.
- Do not change Markdown storage, the issue body schema, or any backend API.
- Do not replace the Markdown parser (`react-markdown` / `remark-gfm`) unless implementation reveals a concrete blocker.
- Do not redesign the issue-detail workflow state surface, artifact history, or worktree retrieval.
- Do not introduce MDX or any Markdown-as-component execution path.

## Decisions

### Decision 1: Build on top of `react-markdown` + `remark-gfm`; do not switch parsers

Keep the existing engine and add the missing Reader layer on top. Heading remap, table wrapping, copy-code, and collapse are all expressible as `react-markdown` `components` overrides plus a wrapper component.

- *Alternatives considered:* `markdown-it` (HTML-string output would still need React bridging + sanitizer wiring); `marked` (no built-in sanitization); custom `unified`/`rehype` pipeline (heavier than needed; no current need for AST-level TOC indexing); MDX (inappropriate for untrusted/agent-generated content). None remove the need to design typography/overflow/heading hierarchy, so switching parser adds migration + security surface for no contract gain. This matches the issue's stated decision.

### Decision 2: `MarkdownReader` is a shared UI primitive, not owned by issue or artifact

Place `MarkdownReader` under a new folder `packages/web/src/shared/ui/markdown-reader/` (`MarkdownReader.tsx`, `MarkdownReader.test.tsx`, plus small building-block files for heading remap, table wrapper, and copy-code button). Export it from the shared UI barrel so any widget/page can import it.

Rationale: it is a presentation primitive reused across surfaces (issue, artifact, and later review/transcript). Owning it in `issue-detail` or `issue-workflow` would force a layer-up import for the other surface.

- *Alternative considered:* putting it under `widgets/`. Rejected because widgets in this repo are domain-scoped (issue-workflow, session-transcript, coder-session); a cross-domain reader is a shared primitive.

### Decision 3: `MarkdownContent` becomes the internal renderer consumed by `MarkdownReader`; public call sites migrate to `MarkdownReader`

Keep the existing `code`/`pre` override logic by moving it into the Reader's internal low-level renderer (or a sibling `renderMarkdownOverrides` helper) so the visible behavior of already-passing issue-detail Markdown tests is preserved when those tests are migrated. Do not keep `MarkdownContent` as a separate public export — the only current consumer is `IssueDetailPage`, which is migrating anyway. This avoids a stale parallel API.

- *Alternative considered:* keep `MarkdownContent` as a public low-level renderer. Rejected because it would invite new direct call sites and re-fragment the surface.

### Decision 4: `MarkdownReaderProps` is the contract from the issue, with one small clamp

```ts
type MarkdownReaderProps = {
  content: string
  baseHeadingLevel?: 1 | 2 | 3 | 4 | 5 | 6   // default 2
  mode?: 'full' | 'collapsible'                // default 'full'
  collapsedHeight?: number                     // default 600
  showToc?: boolean                            // default false
  showHeadingAnchors?: boolean                 // default false
  showCopyCode?: boolean                       // default false
  className?: string
}
```

Defaults chosen so consumers opt into extras rather than inheriting them: `baseHeadingLevel` defaults to `2` (so embedded `#` is demoted by default and the "no duplicate page-level h1" requirement holds without per-call-site care), `mode` defaults to `full` (collapsible is opt-in for issue descriptions only), and TOC/anchors/copy-code default off.

### Decision 5: Heading remap via `react-markdown` `components` overrides for `h1`-`h6`

Implement `remapHeading(base)` that returns a record of `h1`-`h6` component overrides. For an original level `L` (1-6) and `base` `B`, render `h<clamp(L + (B - 1), 1, 6)>`. Clamp at `h6` so a `## Section` at `baseHeadingLevel={5}` becomes `h6`, not `h7`. Anchors (when enabled) are derived from the rendered heading text slug, applied inside the same override.

- *Alternative considered:* a `rehype` plugin that rewrites heading depths in the AST. Rejected — more machinery, and `react-markdown` `components` overrides are the idiomatic, test-friendly path.

### Decision 6: Reader owns the collapse decision via measured content height

In `mode="collapsible"`, `MarkdownReader` measures the rendered content's `scrollHeight` against `collapsedHeight` (default 600) using a `ResizeObserver` plus a layout effect on `content` change. It renders an Expand/Collapse control and a gradient overlay **inside** the Reader. The control uses the existing shared `Button` (`variant="link" size="xs"`) so the issue-detail visual is preserved. Short content never renders the control. This replaces `IssueDetailPage`'s `descriptionExpanded`, `descriptionBodyRef`, `isOverflowing`, and the `max-h-[600px]` clip.

- *Alternative considered:* a CSS-only `max-height` + `:has` selector with no JS measurement. Rejected — the issue requires that short content not show the control, which needs a measurement.

### Decision 7: Typography and overflow via Tailwind typography plus explicit per-element overrides

Root element uses `prose prose-sm max-w-none` (matching the current review-report visual) and applies explicit overrides:

- `table` → wrapped in `<div className="overflow-x-auto">` so tables scroll inside their wrapper and never expand the page.
- `pre`/`code` block → `overflow-x-auto` so long code lines scroll inside the block.
- `a`, `code` (inline), and document body → `overflow-wrap:anywhere` so long URLs, long filesystem paths, and generated identifiers wrap rather than overflow.
- Inline `code` keeps the existing `px-1 py-0.5 bg-gray-100 rounded text-xs font-mono` styling so the `REQ-WUI-ISSUE-MARKDOWN-002` inline-code look is preserved.

The wrapper sets `max-w-none` so the Reader fills its containing column rather than imposing its own measure.

### Decision 8: Copy-code via `navigator.clipboard.writeText`

When `showCopyCode` is enabled, each fenced code block renders a small button (top-right of the block) that copies the block's raw text via `navigator.clipboard.writeText`. No backend, no telemetry. Guard for `navigator.clipboard` absence (older test contexts) by treating the missing API as "button visible, click is a no-op".

### Decision 9: Artifact Markdown detected via `contentType === 'text/markdown'`

In `ArtifactContentViewer`, when `data.kind === 'text'` and `data.contentType === 'text/markdown'`, render through `<MarkdownReader content={data.content} baseHeadingLevel={2} />`. Non-Markdown text content keeps the existing `<pre>` rendering. This avoids changing the artifact API or content model.

### Decision 10: Issue description uses `mode="collapsible"` with `baseHeadingLevel={2}`; comments use `mode="full"` with `baseHeadingLevel={3}`

Issue descriptions are the one surface that needs long-document collapse, so they get `mode="collapsible" collapsedHeight={600}`. Comments are short by nature and already live in a compact card; they get `mode="full"` with a deeper base level so a comment `#` cannot compete with the description's `h2`. Both settings are wired at the call site, not baked into the Reader.

## Risks / Trade-offs

- [`react-markdown` component override for headings loses original `data-*` attrs] -> keep the override signature `{ children, ...props }` and spread remaining props onto the remapped tag so remark-gfm and any future plugin data survive.
- [Collapsible measurement depends on layout being settled; images/async content could change height after first paint] -> run the measurement in a `ResizeObserver` rather than a one-shot `useEffect`, so the Expand control appears if content grows past the threshold after mount.
- [Default `baseHeadingLevel={2}` is a behavior change for any embedded-Markdown page that relied on `#` rendering as `h1`] -> this is the intended fix and matches the no-duplicate-h1 requirement; covered by an explicit acceptance test on issue detail.
- [Moving collapse state out of `IssueDetailPage` breaks the existing `scrollHeight`-based tests] -> those tests are rewritten to assert Reader-level Expand/Collapse (required by `REQ-MDR-012`); covered in migration plan.
- [`navigator.clipboard` may be undefined in jsdom] -> feature-detect and treat missing API as "button renders, click no-ops" so tests do not need a clipboard polyfill.
- [Review/transcript surfaces still diverge after this change] -> accepted as Non-Goal; `MarkdownReader` is the contract they will migrate to later, and their current `prose prose-sm max-w-none` path is already visually close to the Reader default.

## Migration Plan

1. Add `packages/web/src/shared/ui/markdown-reader/MarkdownReader.tsx` and building-block helpers (heading remap, table wrapper, copy-code button) plus `MarkdownReader.test.tsx` covering `REQ-MDR-001` through `REQ-MDR-009`.
2. Export `MarkdownReader` (and `MarkdownReaderProps`) from the shared UI barrel.
3. Update `ArtifactContentViewer.tsx`: branch on `contentType === 'text/markdown'` and render through `MarkdownReader`. Update `WorkflowArtifacts.test.tsx` / `WorkflowTaskArtifact.test.tsx` assertions for `.md` artifacts to expect rendered Markdown (e.g. a heading) rather than a `<pre>` block.
4. Update `IssueDetailPage.tsx`: remove `MarkdownContent`, `descriptionExpanded`, `descriptionBodyRef`, `isOverflowing`, the `scrollHeight > 600` effect, the `max-h-[600px]` clip, the gradient overlay, and the Expand/Collapse button. Render the description through `<MarkdownReader content={issue.body} mode="collapsible" collapsedHeight={600} baseHeadingLevel={2} />` and each comment through `<MarkdownReader content={comment.body} baseHeadingLevel={3} />`.
5. Rewrite the Markdown/collapse block of `IssueDetailPage.test.tsx` (the `scrollHeightSpy`-based cases at lines ~95-120, ~350-405) to assert: Reader-level Expand/Collapse appears for long content, does not appear for short/empty content, and embedded `#` renders as `h2` (not `h1`).
6. Run the web test suite, lint, and typecheck.

**Rollback:** every step above is a discrete commit. To roll back, revert the `IssueDetailPage` and `ArtifactContentViewer` migration commits first to restore the page-local collapse and `<pre>` rendering; the `MarkdownReader` primitive itself is additive and can stay or be reverted independently. No backend, storage, or API changes need rollback.

## Open Questions

- Should `MarkdownReader` accept a `remarkPlugins` passthrough so review-report and transcript migrations can keep any future custom remark plugin without forking the Reader? Default proposal: no (keep the API surface minimal); revisit when the first follow-up migration actually needs it.
- Should the optional TOC be sticky/scrollable inside a sidebar, or inline above the content? Defer the visual detail to the first consumer that turns `showToc` on (none in this change).
- Do we want a single shared copy-code icon/button component for reuse with the future review-report and transcript migrations? Not blocking; can extract later when the second consumer lands.
