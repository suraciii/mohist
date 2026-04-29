## Context

The current review approval UI lives entirely in `IssueDetailPage.tsx` (lines 781–926). When the agent pauses at a review gate, three sections render in the right sidebar:

1. **Review Report** (L781–791) — a `max-h-64 overflow-y-auto` div with `whitespace-pre-wrap` raw text
2. **Approval Required** (L794–889) — a single "Approve & Continue" button regardless of result, plus a rebase button for review stage
3. **Send Message** (L891–926) — a generic textarea that ships raw text to the agent

The backend already stores structured data in `approvalState.output` (`Record<string, unknown>` in `types.ts:22`): `result` (PASS/FAIL), `dimensions` (array with `name`, `status`, `issues`), `reviewReport` (full markdown), and `selfReviewNotes`. The frontend currently ignores all of this and dumps `reviewReport` as plain text.

No backend changes are needed. No new dependencies are needed (`react-markdown ^10.1.0` already in `packages/cli/web/package.json`).

## Goals / Non-Goals

**Goals:**
- Render review gate as a decision panel with three visual layers: Result Banner → Issue Summary → Action Area
- Switch from in-panel report expansion to a Full Report Modal
- Send structured issue summary instead of full report on "Send back for fixes"
- Remove "Force Approve" double-click confirmation; replace with "Approve anyway" single-click
- Extract review UI into `ReviewSummary` and `ReviewApprovalPanel` components

**Non-Goals:**
- Backend API changes (existing approve/sendMessage endpoints are sufficient)
- Type changes (`ApprovalState.output` is `Record<string, unknown>`, which accommodates all needed fields)
- Plan stage approval panel changes (keeps existing "Approve & Continue" + Send Message UI)
- Diff/Files tab changes (existing tab is sufficient; "View Files →" just scrolls to it)
- Changing the review prompt format or backend parsing logic

## Decisions

### D1: Parse result and dimensions from output at render time

Extract `result` and `dimensions` from `approvalState.output` inside components using a helper function, not via type changes. The output is `Record<string, unknown>`, so we cast at the boundary:

```typescript
type ReviewOutput = {
  result?: string
  dimensions?: Array<{ name: string; status: string; issues?: string[] }>
  reviewReport?: string
  selfReviewNotes?: string
}

function parseReviewOutput(output?: Record<string, unknown>): ReviewOutput { ... }
```

**Alternatives considered:**
- Add `ReviewOutput` to `ApprovalState` in types.ts → would require backend type alignment, scope creep for a frontend-only change
- Trust the field types without parsing → unsafe, `output` is untyped from API

### D2: Two-component extraction

Split into `ReviewSummary` (data display) and `ReviewApprovalPanel` (actions + orchestration):

- `ReviewSummary` receives `output` prop, renders Result Banner + Issue Summary. No side effects.
- `ReviewApprovalPanel` receives `output`, `issueNumber`, `onViewFiles`, and callbacks. Renders `<ReviewSummary>`, Action Area, and Full Report Modal. Owns all mutation state (approve, sendMessage, expanded states).

This separates the read-only display from the action logic, making each testable independently.

**Alternatives considered:**
- Single monolithic component → would be ~400 lines, hard to test
- Three components (Banner + Summary + Actions) → over-engineering for this scope; two is enough

### D3: Full Report Modal as portal-style overlay

Implement the modal as a sibling to the sidebar content, using a `fixed` position overlay. No React portal needed — render it inside `ReviewApprovalPanel` at the top level of its return, before the sidebar content. The overlay z-index sits above the sidebar.

Structure:
```
<div> <!-- ReviewApprovalPanel root -->
  {reportModalOpen && (
    <div className="fixed inset-0 z-50 ...">  <!-- backdrop + modal -->
      ...
    </div>
  )}
  <div className="rounded-lg ...">  <!-- sidebar panel -->
    <ReviewSummary ... />
    <!-- action area -->
  </div>
</div>
```

**Alternatives considered:**
- React portal to `document.body` → unnecessary complexity, the sidebar is already the right container
- Third-party modal library → no other modal in the codebase uses one; `fixed` overlay is the pattern used by `EditIssueDialog`

### D4: Send back message composition with fallback chain

The `handleSendBackForFixes` function follows a priority chain:

1. `dimensions` available → extract FAIL dimension names + issues → format as `### Name\n• issue1\n• issue2`
2. `reviewReport` available → regex extract content between `## Fix Suggestions` and next `##` heading
3. Neither → generic fallback message

This keeps the agent focused on actionable items without sending ~2000 words of context.

**Alternatives considered:**
- Always send full report → defeats the purpose; agent gets distracted by PASS sections
- Backend extracts summary → would require backend changes, out of scope

### D5: IssueDetailPage integration via conditional rendering

In `IssueDetailPage.tsx`, replace the three review-gate sections (L781–926) with:

```typescript
{isApprovalGate && issue.stage === Stage.Review && (
  <ReviewApprovalPanel
    output={issue.approvalState?.output}
    issueNumber={issueNumber}
    onViewFiles={() => setDiffTab('files')}
    rebaseResult={rebaseResult}
    onRebase={() => { setRebaseResult(null); rebaseMutation.mutate() }}
    rebasePending={rebaseMutation.isPending}
  />
)}
{isApprovalGate && issue.stage !== Stage.Review && (
  /* existing Plan/etc approval UI unchanged */
)}
```

The `isApprovalGate` check, `approveMutation`, `sendMessageMutation`, and `rebaseMutation` remain in `IssueDetailPage`. `ReviewApprovalPanel` receives what it needs via props or creates its own local mutations.

**Alternatives considered:**
- Move all mutations into `ReviewApprovalPanel` → loses the shared `queryClient.invalidateQueries` pattern established in IssueDetailPage
- Context provider for mutations → over-engineering for a single consumer

### D6: Markdown rendering via react-markdown

Use the existing `react-markdown ^10.1.0` for the Full Report Modal content. Pass `reviewReport` as children to `<ReactMarkdown>`. No custom components or plugins needed — default rendering is sufficient.

**Alternatives considered:**
- Custom markdown parser → unnecessary, react-markdown already installed
- `dangerouslySetInnerHTML` → XSS risk even with trusted content

## Risks / Trade-offs

- [`dimensions` field may not always exist in output] → Fallback chain in D4 handles all three levels: dimensions → report extraction → generic message. Graceful degradation.
- [Untyped `output` field means runtime errors if backend schema changes] → `parseReviewOutput` helper validates at the boundary and provides safe defaults. Risk is low since backend is our own code.
- [Modal inside sidebar may overflow on small screens] → Use `max-h-[90vh] overflow-y-auto` on modal content area. The 80% width constraint leaves margin on all sides.
- [Rebase button moved into ReviewApprovalPanel] → Pass rebase state as props from IssueDetailPage. Keeps the rebase mutation owner in one place.

## Migration Plan

1. Create `ReviewSummary.tsx` with `parseReviewOutput` helper + Result Banner + Issue Summary rendering
2. Create `ReviewApprovalPanel.tsx` with Action Area + Full Report Modal + send-back logic
3. Edit `IssueDetailPage.tsx` to conditionally render `ReviewApprovalPanel` for Review stage gates (replace L781–926 when `stage === 'review'`)
4. Verify Plan stage approval UI is unaffected
5. No rollback concern — old code is preserved for Plan stage; Review stage simply renders new component

## Open Questions

None. All technical decisions resolved: no new dependencies, no backend changes, no type changes.
