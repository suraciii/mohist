# Review Report

## Result: FAIL

The change is functionally sound: all four issue symptoms are addressed, typecheck is clean, and all 4787 web tests pass under `npm run test:run`. However, the post-build candidate breaks the project's CI test gate (`npm run test:ci` / root `npm test`), which runs an additional test-file-size-budget check that the new/extended test files violate. That gate is unresolved, so the candidate cannot merge as-is.

## Repaired Items

None. No small safe repairs (formatting, typos, obvious guards) were needed; the production code is clean and idiomatic.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/widgets/session-transcript/{model/transcript-state.test.ts, ui/AssistantParts.render.test.tsx, ui/tool-views/index.test.tsx}` + CI gate `npm run check:test-boundaries -w packages/web`
  Evidence: The candidate adds/extends test files past the repo's enforced test-file-size budgets. `npm run check:test-boundaries -w packages/web` exits 1 with three violations:
    - `model/transcript-state.test.ts` — 711 lines, exceeds its baseline allowance of **620** (master was 619; T-002 pushed it to 711). Tool message: "Split the file or lower it to at most 620 lines, then lower or remove the baseline entry."
    - `ui/AssistantParts.render.test.tsx` — 335 lines, exceeds the **300** hard limit with **no baseline** (master was 240; T-001's "no unknown" suite pushed it over). Tool message: "Split the file below 300 lines; do not add a new baseline entry."
    - `ui/tool-views/index.test.tsx` — 425 lines, exceeds the **300** hard limit with **no baseline** (master was 211; T-004's a11y suites pushed it over). Tool message: "Split the file below 300 lines; do not add a new baseline entry."

    This is not a hypothetical gate: the root `npm test` script is `dotnet test ... && npm run test:ci --workspaces`, web `test:ci` is `check:fsd && check:test-boundaries && vitest run`, and `.github/workflows/ci.yml:117` runs `npm run test:ci -w packages/web`. The task ACs only required `npm run test:run` (vitest), which is why this slipped past task-level verification, but the standard CI path and `npm test` fail. The checker explicitly forbids the easy workaround ("do not add a new baseline entry"), so the only sanctioned fix is splitting the files. [disallowed:reason] Repair was considered but not performed: splitting three test files is a structural test refactor that falls under "broad refactoring" in the repair policy, and the partition of `describe` blocks is a judgment call best made by the implementer, not unilaterally during review.
  SuggestedAction: Split each oversized file along its existing `describe` boundaries into collocated siblings (e.g. move the T-002 `appendTextToTurn`/`appendReasoningToTurn` paragraph-fidelity suites out of `transcript-state.test.ts`; move the "no unknown" suite out of `AssistantParts.render.test.tsx`; move the `ToolRowView accessibility` / `ContextGroupView accessibility` suites out of `tool-views/index.test.tsx`). Keep each file under 300 lines (or under the existing baseline for `transcript-state.test.ts`, then lower that baseline). Do not add new baselines.
  Verification: `npm run check:test-boundaries -w packages/web` must exit 0; then `npm run test:ci -w packages/web` (and root `npm test`) must pass.
  Status: unresolved

## Follow-up Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/widgets/session-transcript/model/session-transcript-display.ts:226-228` (`getContextToolSummary`) — context-group title suffix path
  Evidence: The "never unknown" floor was applied to the three choke points named in `design.md` D1 (`inferDisplayTitle`, `FallbackEntry.getTitle`, `getToolDisplayLabel`). A fourth label site was not covered: `getContextToolSummary` builds a context-group's visible title suffix as `tool.displayTitle ?? tool.displaySubtitle ?? tool.target ?? getToolPath(tool.input)`, with no `'unknown'` filter. `displayTitle` can be set to the literal `'unknown'` from upstream via `getDisplayFields`/`createToolPart` when the event carries `title: 'unknown'` or `displayTitle: 'unknown'` (`transcript-tool-state.ts:38-40, 141, 169`). In `ContextGroupView` that suffix becomes both visible text and part of the button's accessible name (`ui/tool-views/index.tsx:223`), which the spec (`specs/transcript-tool-naming/spec.md`) forbids: "MUST NOT appear ... not as a subtitle that becomes the visible title." The common observed path (name fallback) is fixed, so this is a narrow edge case, but it is a residual leak against the strict spec wording.
  SuggestedAction: Either filter `'unknown'` in `getContextToolSummary` (mirror `getToolDisplayLabel`'s guard) or normalize `displayTitle`/`displaySubtitle` at the model boundary so `'unknown'` never reaches any display site. Add a render assertion that a single-tool context group whose tool title is `'unknown'` exposes a readable group name.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: Text-fidelity acceptance criterion (`issue-426` AC #2) and `progress.txt` "T-002 Localization Outcome"
  Evidence: T-002 correctly pins the web presentation contract — `appendTextToTurn` is verified lossless and `\n\n` renders as distinct `<p>` blocks (9 new tests across `transcript-state.test.ts`, `useSessionTranscript.test.tsx`, `TranscriptMarkdown.render.test.tsx`). The localization concludes the `usage:Let me` fusion cannot reproduce in the web layer when deltas carry `\n\n`, so the root cause is upstream (agent/runtime token splitter dropping the separator, or persisted text already fused) and is escalated per the issue's non-goals. This is the sanctioned path, but: (a) the issue AC ("assistant 文本段落边界 ... 无跨段落粘连") may not be fully closed for the originally observed session until the upstream gap is fixed, and (b) `progress.txt` says "escalated as a follow-up issue" but records no issue number, so traceability is unconfirmed.
  SuggestedAction: Confirm the upstream follow-up issue exists (record its number in `progress.txt`), and verify against the original `T-001.1` repro whether the symptom is actually gone. If it persists, the follow-up owns the remaining fix.
  Status: follow-up

- [ID: item-4]
  Severity: minor
  Scope: `packages/web/src/widgets/session-transcript/ui/AssistantParts.tsx:35-39` — `assistant-text-streaming-glyph`
  Evidence: The per-part streaming glyph is a purely decorative dot (`<span>` with only a background class, no text) but is not marked `aria-hidden="true"`. D4 hid the indicator inner spans and the disclosure icons but did not address this glyph. It is redundant with the `role="status"` `StreamingIndicator` and conveys no information to AT, so it should be hidden from assistive tech. (Behavior is unchanged from before this change; the liveness gate correctly suppresses it on non-running sessions.)
  SuggestedAction: Add `aria-hidden="true"` to the glyph span, with a small render assertion.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/web/src/widgets/session-transcript/ui/SessionTranscriptView.tsx`, `ToolCallCard.tsx`
  Evidence: The legacy renderer still emits `'unknown'` (`SessionTranscriptView.tsx:20-23`) and lacks the new a11y attributes. Confirmed out of scope: it is not exported from `widgets/session-transcript/index.ts` (only `SessionTranscriptLayout`, `useSessionTranscript`, `projectTurn`, `DisplayTurn` are exported) and is referenced only by its own collocated test/fixture files, never by `src/pages`. Slated for #427 removal per `design.md`.
  SuggestedAction: None here; remove with #427. Noted so reviewers grepping for `'unknown'` understand the residual hits are test-only.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: `packages/web/src/widgets/session-transcript/ui/AssistantParts.tsx:93-95` — `ErrorPartView` warning triangle SVG
  Evidence: The decorative warning SVG in `ErrorPartView` is not marked `aria-hidden`. It is accompanied by visible text, so the control is still accessible, but the icon could be announced redundantly. This predates the change and `ErrorPartView` is not a disclosure control covered by the issue's a11y AC.
  SuggestedAction: Optionally mark it `aria-hidden="true"` in a future a11y pass.
  Status: pre-existing

## Notes

- Functional acceptance criteria are satisfied with evidence:
  - **AC1 (no "unknown" title):** floors at `transcript-tool-utils.ts:443-451` (`inferDisplayTitle`), `tool-registry.tsx:31-36` (`FallbackEntry.getTitle`), `shared.tsx:14-22` (`getToolDisplayLabel`); covered by `transcript-tool-utils.test.ts`, `tool-registry.test.tsx`, `shared.test.tsx`, `AssistantParts.render.test.tsx`. (Residual edge case in item-2.)
  - **AC2 (paragraph fidelity):** web contract pinned lossless at model/dispatch/render layers (`transcript-state.test.ts:132-181`, `useSessionTranscript.test.tsx:158-229`, `TranscriptMarkdown.render.test.tsx:62-108`); upstream escalation recorded (item-3).
  - **AC3 (liveness-gated indicators):** render gate at `SessionTranscriptLayout.tsx:91-92`, hook hygiene at `useSessionTranscript.ts:147`, per-part glyph at `AssistantParts.tsx:21`; covered by `tests/SessionTranscriptActivityIndicators.spec.tsx` (12 tests, fake-timer based, no wall-clock).
  - **AC4 (accessible controls):** `aria-expanded` on `ToolRowView`/`ContextGroupView`/`TurnDiffs`, `aria-hidden` on decorative SVGs, `role="status"` on indicators, `role="log"` retained; covered by `tool-views/index.test.tsx`, `TurnList.render.test.tsx`, `shared.test.tsx`.
- Verification run on the candidate: `npm run typecheck -w packages/web` (clean); `npm run test:run -w packages/web` (344 files, 4787 tests passed); `npm run check:fsd -w packages/web` (clean); `npm run check:test-boundaries -w packages/web` (FAILED — item-1).

<promise>FAIL</promise>
