# Review Report

## Result: PASS

The post-repair candidate addresses all four issue symptoms with verifiable evidence, and every finding from the prior review is resolved: the test-file-size-budget blocker is fixed (files split under budget), the context-group `'unknown'` leak is filtered, the per-part glyph and `ErrorPartView` decorative icons are `aria-hidden`, and the upstream text-fidelity gap is recorded as follow-up issue #433. The full CI gate (`check:fsd` + `check:test-boundaries` + `vitest run`) is green: 348 files, 4790 tests, typecheck clean, zero boundary violations.

## Repaired Items

None. The candidate was already repaired by the fix-review-findings pass; this re-review made no code changes. No small safe repairs (formatting, typos, obvious guards) were needed — the production code and tests are clean.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: Text-fidelity acceptance criterion (issue AC #2) + follow-up issue #433
  Evidence: T-002 pins the web presentation contract as lossless at three independent layers — model (`appendTextToTurn` does `existing.text + text`, no stripping), dispatch (`message.delta`/`coder_text_chunk` append `detail.text` verbatim), and render (`TranscriptMarkdown` renders `\n\n` as distinct `<p>`). Regression tests across `model/transcript-text-fidelity.test.ts`, `model/useSessionTranscript.test.tsx`, and `ui/TranscriptMarkdown.render.test.tsx` prove the `usage:Let me` fusion cannot reproduce in the web layer when deltas carry `\n\n`. Localization concluded the fusion only reproduces when the runtime/server omits the inter-paragraph separator — an upstream gap. This is the issue's sanctioned escalation path (non-goals: "不改事件采集/回流链路"), and `progress.txt:49` records it as **follow-up issue #433** ("Streamed assistant text loses paragraph separators before reaching the web transcript"). The web-side AC is satisfied (faithful pass-through); the originally observed session may still fuse until #433 lands the upstream fix.
  SuggestedAction: Track #433 to closure — ensure the runtime token splitter / persisted-text assembly preserves `\n\n` across paragraph boundaries into emitted deltas, then re-verify the `T-001.1` repro.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: `packages/web/src/widgets/session-transcript/ui/SessionTranscriptView.tsx`, `ToolCallCard.tsx`
  Evidence: The legacy renderer still emits `'unknown'` (`SessionTranscriptView.tsx:20-23`) and lacks the new a11y attributes. Confirmed out of scope: it is not exported from `widgets/session-transcript/index.ts` (only `SessionTranscriptLayout`, `useSessionTranscript`, `projectTurn`, `DisplayTurn` are exported) and is referenced only by its own collocated test/fixture files, never by `src/pages`. `design.md` Non-Goals explicitly excludes it and assigns removal to #427.
  SuggestedAction: Remove with #427. Noted so reviewers grepping for `'unknown'` understand the residual hits are test-only.
  Status: out-of-scope

## Notes

- **Prior-review findings are resolved:**
  - Test-file-size-budget blocker: `transcript-state.test.ts` 711→619 (under its 620 baseline); `AssistantParts.render.test.tsx` 335→251; `tool-views/index.test.tsx` 425→211. New collocated siblings (`transcript-text-fidelity.test.ts` 121, `AssistantParts.tool-naming.test.tsx` 119, `index.accessibility.test.tsx` 234, `session-transcript-display.context-title.test.ts` 73) are all under 300. `npm run check:test-boundaries -w packages/web` → 0 violations (348 files). Moved tests verified non-duplicated and non-lost.
  - Context-group `'unknown'` leak: `model/session-transcript-display.ts:226-234` adds `readableSummary()` filtering `displayTitle`/`displaySubtitle`/`target`; covered by `session-transcript-display.context-title.test.ts` (single-tool group with `displayTitle:'unknown'` surfaces a readable fallback / bare "Gathering context").
  - Per-part glyph + `ErrorPartView` icons: `aria-hidden="true"` added (`AssistantParts.tsx:37,94`); assertions in `tests/SessionTranscriptActivityIndicators.spec.tsx:173` and `AssistantParts.render.test.tsx`.

- **Acceptance criteria — evidence:**
  - **AC1 (no "unknown" title):** floors at `transcript-tool-utils.ts:443-451` (`inferDisplayTitle`), `tool-registry.tsx:31-36` (`FallbackEntry.getTitle`), `shared.tsx:14-22` (`getToolDisplayLabel`), and `session-transcript-display.ts:226-235` (`getContextToolSummary`). A widget-wide grep confirms no production display path renders the literal `'unknown'` as a tool title (residual `'unknown'` hits are internal registry keys or unrelated liveness-probe diagnostic strings in `useSessionTranscript.ts:485,488`). Covered by `transcript-tool-utils.test.ts`, `tool-registry.test.tsx`, `shared.test.tsx`, `AssistantParts.tool-naming.test.tsx`, `index.accessibility.test.tsx`.
  - **AC2 (paragraph fidelity):** web contract pinned lossless at model/dispatch/render (see item-1); upstream gap escalated to #433.
  - **AC3 (liveness-gated indicators):** render gate at `SessionTranscriptLayout.tsx:91-92` (`isRunning && …`), hook hygiene at `useSessionTranscript.ts:147` (`clearStreaming()` on `isRunning`→false), per-part glyph at `AssistantParts.tsx:21` (`isRunning === true && …`, fails closed on undefined). Covered by `tests/SessionTranscriptActivityIndicators.spec.tsx` (12 tests, fake-timer based, no wall-clock).
  - **AC4 (accessible controls):** `aria-expanded` on `ToolRowView` (conditional on `showExpandableDetails`, `index.tsx:136`), `ContextGroupView` (`index.tsx:215`), `TurnDiffs` (`TurnList.tsx:115`); decorative SVGs `aria-hidden` (`ToolIcon`/`ToolStatusDot`/chevrons in `shared.tsx`, `index.tsx`, `TurnList.tsx`); `role="status"` on `StreamingIndicator`/`ThinkingPlaceholder` (`SessionTranscriptLayout.tsx:106,118`); `role="log"` retained on `TurnList`. Covered by `index.accessibility.test.tsx`, `TurnList.render.test.tsx`, `shared.test.tsx`.

- **Verification run on the post-repair candidate:** `npm run typecheck -w packages/web` (clean); `npm run check:fsd -w packages/web` (clean, 469 modules); `npm run check:test-boundaries -w packages/web` (0 violations, 348 files); `npm run test:run -w packages/web` (348 files, 4790 tests passed). Transcript widget subset: 38 files, 479 tests passed.

<promise>PASS</promise>
