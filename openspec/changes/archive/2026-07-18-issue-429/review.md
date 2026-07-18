# Review Findings

## [P1] Error-bar navigation uses a different locate registry from the transcript

`SessionDetailShell` creates `useTranscriptLocate({ scrollContainerRef })` at lines 190-195 and passes its `locate` callback to `SessionErrorsEvidence` at lines 245-253. `SessionTranscriptLayout` independently creates another `useTranscriptLocate` instance at `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx:63`, and only that second instance's `expansionRegistry` and `highlightRegistry` are passed to `TurnList` and the tool rows.

As a result, a successful error-bar jump can scroll the target DOM row, but `highlightRegistry.get(rowAnchorId)` in the shell-owned hook is empty, so it never applies the required highlight. For a failed tool inside a collapsed context group, the shell-owned `expansionRegistry` is also empty, so the inner failed row is not mounted when the rAF query runs and the jump becomes a no-op. This fails the primary error-location acceptance criterion and the collapsed-group requirement. Use one shared locate/registry instance for both the launchers and the rendered transcript rows.

## [P1] A highlighted turn is incorrectly hidden from assistive technology

`packages/web/src/widgets/session-transcript/ui/TurnList.tsx:83` applies `aria-hidden={true}` to the entire `TurnItem` whenever it is highlighted. Turn navigation nodes deliberately target turn anchors, so activating a turn hides its divider, prompt, every assistant message, tool row, and file-change summary from screen readers for the highlight duration.

The jump-highlight spec requires only the decorative highlight cue to be hidden from assistive technology; it explicitly requires that the destination row's role and accessible name remain unchanged. Move `aria-hidden` to a decorative overlay, as `ToolRowView` already does, or remove it from the turn root.

## [P1] The mini timeline is not a timeline for long sessions

`packages/web/src/widgets/session-transcript/ui/MiniTimeline.tsx:68-76` renders every node as an ordinary vertically stacked button with a fixed `gap-1`. It does not calculate a relative position from a node's transcript offset, map nodes to the scrollable transcript height, or otherwise relate the rail's vertical positions to the transcript's scroll position.

For sessions with many events, the rail grows with every node and scrolls as normal content rather than remaining a compact sticky overview. It therefore cannot provide the planned mini-timeline navigation surface for the long transcripts in the issue, and violates design D1's requirement that node vertical position approximate transcript position. Project node positions against the transcript/turn offsets and render them on a fixed-height sticky track (with an overflow strategy for coincident markers).

## [P1] The change regresses expanded file-change details

`packages/web/src/widgets/session-transcript/ui/TurnList.tsx:135` now renders only `change.path` when the turn-level changed-files summary is expanded. The pre-change implementation rendered the operation badge, moved-from path, additions, and deletions for every file. This is unrelated to the requested lazy-render/navigation work and removes existing information from the on-screen transcript contract.

Restore the full expanded file-change row presentation (operation, move source, and line statistics) while retaining the scoped lazy-render changes.

## [P2] File-change event classification includes failed edit calls

`packages/web/src/widgets/session-transcript/model/timeline-nodes.ts:24-28` treats any non-failed tool with a non-empty `changedFiles` array as a file-change event, including `pending` and `running` calls. The product/spec requirement is for completed file-changing tool calls, and the design decision likewise says file-change nodes are emitted for completed tools.

Require `tool.status === 'completed'` before emitting a file-change node. This prevents a running tool that has partial/provisional changed-file metadata from being represented as a completed green change event.

<promise>FAIL</promise>
