## Context

Issue Detail page has two tabs (Files, Commits) that lack meaningful code review capability. The Files tab only shows inaccurate `+/-` symbol counts (parsed from `--stat` visual bars, e.g. `workflow-controller.ts` shows +10 instead of actual +114). The Commits tab relies on Issue #90's fix. Both tabs are rendered inline in `IssueDetailPage.tsx` (~950 lines) with a minimal `CommitDiffView` (lines 44-70) that has no line numbers, file headers, or expand/collapse.

Current API shape for `GET /:number/diff`:
```json
{ "files": [{ "file": "path.ts", "additions": 3, "deletions": 2 }] }
```
Parsed from `git diff --stat` output by counting `+`/`-` characters in the visual bar — fundamentally inaccurate.

## Goals / Non-Goals

**Goals:**
- Merge Files + Commits tabs into single Changes tab with Files changed (default) and Commits sub-views
- Fix inaccurate diff stats by using `git diff --numstat` for precise line counts
- Provide GitHub PR-style inline diff viewing per file in Files changed view
- Extract a reusable DiffViewer component with line-level green/red/gray rendering
- Add noise commit filtering in Commits sub-view
- Maintain full backward compatibility with existing `/commits` and `/commits/:hash/diff` endpoints

**Non-Goals:**
- Fixing the commits API parsing bug (that's Issue #90's scope)
- Syntax highlighting beyond basic diff coloring (no language-aware tokenization)
- Side-by-side diff view (unified only)
- Inline commenting on diff lines
- Task progress tracking in Changes tab (that's the Tasks section's job)

## Decisions

### D1: Backend splits full patch into per-file diffs server-side

Use two git commands: `git diff --numstat` for precise stats, then `git diff` for the full unified patch. Split the patch into per-file chunks server-side using the `diff --git a/... b/...` boundary. Each file entry in the API response gets its own `diff` string.

**Rationale:** Sending the full patch and splitting client-side would require the frontend to implement diff parsing. Splitting server-side keeps the frontend simpler and allows binary file detection to happen naturally.

**Alternatives considered:**
- Send full patch, split on frontend → rejected: duplicates parsing logic, larger initial payload
- Use `git diff --stat -p` combined output → rejected: harder to parse reliably, mixes stat and patch
- Use porcelain format (`--porcelain`) → rejected: more complex parsing for no real benefit

### D2: DiffViewer is a self-contained component with inline parser

Build a lightweight unified diff parser (~50 lines) that splits diff text into typed line arrays (`{ type: 'hunk' | 'add' | 'del' | 'context', content: string, oldLine?: number, newLine?: number }`). No external diff library.

**Rationale:** The diff format is simple and well-defined. A parser handling `@@ -a,b +c,d @@` hunk headers and `+`/`-`/` ` prefixed lines is straightforward. External libraries like `diff` or `react-diff-viewer` add unnecessary dependencies.

**Alternatives considered:**
- `react-diff-viewer` / `react-diff-viewer-continued` → rejected: heavy dependency, opinionated styling, overkill for our needs
- `diff` npm package → rejected: provides diff computation, not just parsing; we already have the diff text from git

### D3: Noise commit detection via message prefix matching

Define noise patterns as a simple regex: `/^(chore\(tasks\)|WIP|chore: commit remaining)/i`. Commits matching any pattern are grouped under an expandable "Auto commits (N)" section. The grouping preserves chronological order within each group.

**Rationale:** The set of noise patterns is small and stable (derived from our own agent's commit conventions). Regex matching on the first line is sufficient — no NLP or fuzzy matching needed.

### D4: File search filters client-side only

Filter files by simple case-insensitive substring match on the file path. No debouncing needed since the file list is already in memory (from the API response).

**Rationale:** File lists are typically <100 entries. Client-side filtering is instant and simpler than server-side search with query params.

### D5: Tab state uses simple React state, not URL params

Sub-view selection (Files changed / Commits) stored as `useState`. Not persisted in URL hash or search params.

**Rationale:** Matches the existing pattern (`diffTab` state in current `IssueDetailPage.tsx` line 133). No user has requested shareable URLs to specific sub-views.

## Risks / Trade-offs

- [Large diffs may be slow to render] → Mitigation: files are expanded one-at-a-time on click; collapsed by default. No virtualization needed for typical issue sizes (<50 files, <5000 lines).
- [Server-side patch splitting could fail on edge cases (e.g., filenames with spaces)] → Mitigation: Use the `diff --git` boundary which is unambiguous. Test with filenames containing spaces and special characters.
- [Issue #90 dependency means Commits sub-view may show stale data until fixed] → Mitigation: Changes tab defaults to Files changed view which doesn't depend on #90. Commits sub-view gracefully shows whatever the API returns.
- [Numstat returns `-` for binary files] → Mitigation: Detect `-` entries and mark as binary with `"Binary file, no diff available"`.

## Migration Plan

1. **Backend first**: Modify `GET /:number/diff` handler to use `--numstat` + `git diff`, return new response shape. Old `files` array gets new `diff` field added — backward compatible since old frontend ignores unknown fields.
2. **Frontend types**: Update `DiffFile` type to include `diff?: string`. Add `totalAdditions`/`totalDeletions` to response type.
3. **DiffViewer component**: Create new `DiffViewer.tsx` with parser + renderer.
4. **IssueDetailPage refactor**: Replace Files/Commits tabs with Changes tab. Extract `CommitRow` improvements (noise filtering).
5. **Rollback**: Revert frontend tab structure to show old Files/Commits tabs if needed. Backend response is additive-only so no rollback needed.

## Open Questions

None — all decisions are resolved. Issue #90 is a hard prerequisite but its fix is independent of this design.
