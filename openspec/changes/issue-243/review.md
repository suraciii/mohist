# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.tsx`, `packages/web/src/widgets/session-transcript/ui/TranscriptToolbar.tsx`
  Evidence: The "copy full text" action is rendered only through `TranscriptToolbar` as `rightSlot={<CopyFullTextButton turns={turns} />}` in `SessionTranscriptLayout.tsx:77-80`, but `TranscriptToolbar` is hidden at desktop sizes via `className="lg:hidden ..."` in `TranscriptToolbar.tsx:40-44`. On `lg+` viewports the right-rail TOC is visible (`TurnTocRail`), but no copy action is rendered anywhere. This violates `specs/session-transcript-navigation/spec.md:54-57`, which requires the copy action to be available whenever at least one turn is rendered. [disallowed:product-behavior-change]
  SuggestedAction: Render the copy action in a viewport-independent location, or add a desktop-visible action alongside the `lg+` TOC rail while preserving the mobile toolbar behavior.
  Verification: Add an integration test that proves `[data-copy-full-text]` is available in the desktop layout as well as the mobile toolbar, then run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/session-transcript/model/useTurnKeyboardNav.ts`, `packages/web/src/pages/session/ui/SessionPage.tsx`
  Evidence: Keyboard shortcuts are attached to `scrollContainerRef.current` when it exists (`useTurnKeyboardNav.ts:53-55`). In the main page, that ref is the transcript scroll div (`SessionPage.tsx:769-783`), while the header and followup composer are siblings outside the scroll container (`SessionPage.tsx:761-789`). Keydown events fired while focus is on the document body, header link, or composer area do not bubble to the scroll container, so `j`/`k`/`g`/`G` are not reliably scoped to the active session transcript page as required by `specs/session-transcript-navigation/spec.md:24-27`. Existing tests dispatch directly on the container, masking the production path. [disallowed:product-behavior-change]
  SuggestedAction: Attach the shortcut listener to `window` or another page-level target while retaining the editable-focus and modifier-key bailouts, or explicitly make/focus the scroll container and test that path end-to-end.
  Verification: Add tests that dispatch keydown from `window`/`document.body` with no editable focused and assert navigation, plus tests that a focused followup textarea still receives keystrokes without navigation. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/widgets/session-transcript/model/serialize-transcript.ts`
  Evidence: The copy serializer does not copy all assistant response content that the transcript displays. Tool parts are reduced to one line in `assistantPartLines` (`serialize-transcript.ts:64-72`), even though `DisplayToolPart` includes `input`, `output`, `rawInput`, `rawOutput`, `error`, and `changedFiles` (`session-transcript-display.ts:59-80`) and the UI can reveal those details through `ToolRowView` (`tool-views/index.tsx:45-119`) and changed-file output. Context groups are similarly reduced to `[context-group] <title>` (`serialize-transcript.ts:60-73`) without nested tool content. This conflicts with the issue acceptance criterion "复制全文" and `specs/session-transcript-navigation/spec.md:56-63`, which require copying the entire transcript, including prompts and responses, in document order. [disallowed:product-behavior-change]
  SuggestedAction: Decide the intended plain-text representation for tool input/output, tool errors, changed files, and nested context-group tools, then include that visible transcript content in `serializeTranscriptPlainText` with regression tests.
  Verification: Extend `serialize-transcript.test.ts` with a tool fixture that has input, output, error, changedFiles, and a context-group containing nested tools; verify the copied text includes the required response details.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/web/src/widgets/session-transcript/ui/SessionTranscriptLayout.integration.test.tsx`
  Evidence: The narrow-viewport overflow test does not test the candidate layout. `fakeOverflowMeasurements` hard-codes `clientWidth` to `320` and `scrollWidth` to `baseClientWidth + overflowPx`, and the test always calls it with `overflowPx = 0` (`SessionTranscriptLayout.integration.test.tsx:467-520`). The assertion therefore proves only the fake values (`320 <= 320`), not that the header/cards/code/TOC classes prevent overflow at 320, 375, or 430 px. This leaves `specs/agent-session-ui/spec.md:67-76` without meaningful regression coverage.
  SuggestedAction: Replace the tautological measurement fake with assertions over the actual responsive class contract for the layout-critical elements, or add a browser-level/Playwright check for 320/375/430 px that observes real overflow.
  Verification: Add a failing-first regression test that would fail if a long prompt, assistant markdown code block, or transcript grid lacks its responsive overflow classes; run `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/widgets/session-transcript/ui/TranscriptMarkdown.tsx`, `packages/web/src/widgets/session-transcript/ui/transcript-markdown-theme.ts`
  Evidence: Running `npm run test:run -w packages/web -- src/widgets/session-transcript/ui/TranscriptMarkdown.render.test.tsx` passes but emits six `Could not parse CSS stylesheet` warnings. The full web test suite emits the same warning repeatedly while still passing. This appears tied to the injected transcript highlight stylesheet and makes test output noisier, but it did not fail the candidate gates.
  SuggestedAction: Inspect the generated `TRANSCRIPT_MD_HIGHLIGHT_CSS` for jsdom-incompatible or malformed rules, or switch to a standard Vite CSS import if the test transform can be configured cleanly.
  Status: follow-up

## Pre-existing or Out-of-scope Items

(none)

## Verification

- `mo issue show 243 --project-id proj_f6c141d63b6243bfbb481737b2243b87` read before review.
- Reviewed `openspec/changes/issue-243/proposal.md`, `design.md`, `tasks.json`, delta specs, and `self-review.md`.
- Reviewed all files changed relative to `master...HEAD`, including the web implementation, package changes, and new/updated tests.
- Ran `npm run typecheck -w packages/web`: passed.
- Ran `npm run test:run -w packages/web`: 179 test files passed, 2577 tests passed, 1 skipped; repeated `Could not parse CSS stylesheet` warnings were emitted.
- Ran `npm run test:run -w packages/web -- src/widgets/session-transcript/ui/TranscriptMarkdown.render.test.tsx`: passed with six `Could not parse CSS stylesheet` warnings.

<promise>FAIL</promise>
