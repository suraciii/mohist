## Context

The `session-transcript` widget (`packages/web/src/widgets/session-transcript/`) is the primary read-only surface for observing agent sessions. Its FSD skeleton (`model/` + `ui/` + barrel `index.ts`) is correct, but two files have become mega-files and one carries a layer violation:

- `ui/AssistantParts.tsx` — 1161 lines / complexity 359. Mixes (a) basic part views + dispatch, (b) seven per-tool content views, and (c) ~200 lines of **pure diff/patch calculation** (`buildDiffFromEdit`, `buildDiffFromPatchText`, `extractPatchForFile`) that is domain logic living in the UI layer.
- `model/useSessionTranscript.ts` — 1137 lines / complexity 318. Lines 59–613 are **pure state-transition functions** (no React); line 614 onward is the hook's reactive wiring.

**Split-brain:** patch *parsing* (`parsePatchOperations`, `parseEditInput`, `parseEditWriteChanges`) already lives in `model/transcript-tool-utils.ts`, while the companion patch *diff-building* lives in the UI file. Changing patch handling means editing both layers.

**Constraints:** behavior, visual output, streaming/pagination semantics, the public barrel (`projectTurn`, `useSessionTranscript`, `SessionTranscriptLayout`), and state-machine transition semantics must all be preserved. The widget has existing test coverage (`useSessionTranscript.test.tsx` plus component/render tests) that serves as the regression net. Part of epic #22 (代码复杂度热点治理).

## Goals / Non-Goals

**Goals:**
- Correct the layer violation: all pure diff/patch *calculation* moves to `model/`; UI keeps only diff *rendering*.
- Decouple the transcript state machine from React so pure transitions are directly unit-testable.
- Split per-tool content views by tool family so a single renderer change touches one focused file.
- Bring every widget file out of the web package's top complexity offenders, with each file owning one reason to change.

**Non-Goals:**
- No change to visual presentation, layout, interaction, loading/pagination/streaming behavior, or SSE/protocol.
- No change to state-machine transition semantics — functions are relocated, not re-authored into a new reducer shape.
- No new tool/part rendering, no performance optimization, no public-API change.
- No server / runner / CLI changes.

## Decisions

### D1. Diff calculation → new `model/diff-builder.ts` (not merged into `transcript-tool-utils.ts`)

Move `buildDiffFromEdit`, `buildDiffFromPatchText`, `extractPatchForFile`, and the `FileBlock` type out of `AssistantParts.tsx` into a new `model/diff-builder.ts`. These functions consume `FileChangeSummary` (from `entities/coder-session`) and the parsed patch/edit structures from `transcript-tool-utils.ts`, returning renderable `FileBlock[]`.

**Rationale:** `transcript-tool-utils.ts` (454 lines) is about *tool identity & display metadata* (names, labels, args, display type, patch→`FileChangeSummary` parsing). Diff-building is about *change computation* (`FileBlock[]` for rendering). They change for different reasons (tool-display rules vs. diff format), so co-locating would just move the hotspot. A dedicated `diff-builder.ts` keeps each file single-purpose and stays well under the complexity ceiling.

**Alternative considered:** merge into `transcript-tool-utils.ts` to physically eliminate the split-brain in one file. Rejected — it would push that file toward ~650 lines and re-mix two concerns; the split-brain is fixed by *layer* (both now in `model/`), not by *file*.

**Alternative considered:** a `model/diff/` folder. Rejected — over-fragmentation for three functions; one file suffices.

### D2. Pure state machine → new `model/transcript-state.ts`, 1:1 relocation

Move the pure functions from `useSessionTranscript.ts` lines 59–613 (`createTextPart`, `createToolPart`, `ensureLiveTurn`, `appendInputTurn`, `appendTextToTurn`, `closeActiveTextPart`, `appendReasoningToTurn`, `findToolByCorrelation`, `buildLiveToolDetails`, `updateToolInTurn`, the `asRecord`/`getString`/`getNumber` payload helpers, etc.) verbatim into `model/transcript-state.ts`, preserving exact signatures and bodies. The hook imports them.

**Rationale:** these are already pure `(state, event) → state` functions; relocation makes them directly unit-testable without React and shrinks the hook to reactive wiring only. A 1:1 move honors the Non-Goal of preserving transition semantics and minimizes regression risk.

**Alternative considered:** re-author as a single `reducer(state, action)` with a discriminated action union. Rejected for this issue — it changes call sites and transition shape (semantic risk), and is explicitly a Non-Goal. The pure-function set can still be asserted directly; a reducer consolidation can be a later issue if desired.

**Alternative considered:** split payload helpers (`asRecord`, `asPayloadRecord`, `getNumber`, `getString`, `truncatePreview`) into a separate `model/payload-helpers.ts`. Deferred — they are small and only used by the state functions; splitting now is premature. Noted as an open question.

### D3. Per-tool content views → `ui/tool-views/`, grouped by family

Move the content views out of `AssistantParts.tsx` into `ui/tool-views/`, one file per tool family:
- `bash-view.tsx` — `BashContentView`
- `read-view.tsx` — `ReadContentView` (read/list)
- `search-view.tsx` — `SearchContentView` (grep/glob/search)
- `todo-view.tsx` — `TodoContentView`
- `delegation-view.tsx` — `DelegationContentView`
- `diff-view.tsx` — `DiffContentView`, `PatchDiffView`, `DiffBlockView` (rendering only; computation comes from `model/diff-builder.ts`)
- `shared.tsx` — cross-cutting bits consumed by multiple views (`ToolStatusDot`, `ToolIcon`, `truncateOutput`, `getToolDisplayLabel/Args/Subtitle`)
- `index.tsx` — re-exports + `ToolRowView` (display-type dispatcher) and `ContextGroupView`

`AssistantParts.tsx` keeps only: basic part views (`AssistantTextPartView`, `ReasoningPartView`, `ErrorPartView`, `DividerPartView`) and the top-level `AssistantParts` part dispatcher.

**Rationale:** matches "split by reason for change" — one tool family's rendering changes independently of others. `ToolRowView`/`ContextGroupView` are tool-level dispatchers, so they belong with the tool views, not the part dispatcher.

**Alternative considered:** one file per component (7+ files). Rejected — over-fragmentation; family grouping keeps related renderers reviewable together.

### D4. Step ordering by ascending risk (matches issue plan)

1. **Layer correction** — relocate diff calc to `model/diff-builder.ts`, wire `DiffContentView` to import from there. Pure migration; establishes the test baseline first.
2. **Extract state machine** — move pure functions to `model/transcript-state.ts`; have `useSessionTranscript.test.tsx` assert the same outcomes (now exercisable both via the hook and, optionally, directly).
3. **Split tool content views** — mechanical component move into `ui/tool-views/`.
4. **Slim** — finalize `AssistantParts.tsx` (dispatch + basic parts) and `useSessionTranscript.ts` (reactive wiring only).

Each step ends with `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`. Commit per step for bisectability.

**Rationale:** the riskiest semantic change (state machine) is isolated on top of a green baseline; the lowest-risk move (pure components) goes last when the structure is already proven.

## Risks / Trade-offs

- **[Render regression from relocated diff calculation]** -> The three diff functions are pure; add direct unit tests in `diff-builder.test.ts` asserting `FileBlock[]` output for representative edit/patch inputs before/after the move. Existing component tests guard the rendered diff views.
- **[Import cycles when splitting tool-views]** -> Enforce layering: `model/*` never imports `ui/*`; `ui/tool-views/*` import from `model/*` (types + `diff-builder`) only, never from `AssistantParts.tsx`. The `ui/index.ts` barrel already exists as the aggregation point.
- **[Over-fragmentation / too many tiny files]** -> Family-grouped files (D3) rather than one-per-component; merge any sub-20-line helper into `shared.tsx`.
- **[Test churn masking regressions]** -> Keep `useSessionTranscript.test.tsx` asserting through the hook unchanged; new direct tests for `transcript-state` and `diff-builder` are net-additive coverage, not replacements.
- **[Forgetting the barrel contract]** -> `index.ts` is edited only if a re-export path changes; the three public symbols must remain re-exported from their original module paths.

## Migration Plan

This is a pure internal refactor: no data, protocol, API, or config change; no feature flags; no server deployment coupling.

- **Deploy:** ships as a normal web build. Recommend a single PR with one commit per step (D4) so any regression bisects to the exact relocation.
- **Rollback:** revert the PR/commits; no data migration or state cleanup required.
- **Verification gate:** `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` green at every step; manual smoke of a live transcript + a historical (refresh) transcript to confirm visual/streaming equivalence.

## Open Questions

- **Payload helpers:** keep `asRecord`/`asPayloadRecord`/`getNumber`/`getString`/`truncatePreview` inside `transcript-state.ts`, or factor a `model/payload-helpers.ts` if they get reused by `diff-builder.ts`? Decide at step 2 based on actual sharing.
- **`FileBlock` type home:** define in `model/diff-builder.ts` (exported) and have `ui/tool-views/diff-view.tsx` import the type — confirm no other UI module needs it.
- **Reducer consolidation:** defer to a follow-up issue, or fold a minimal `reducer` wrapper in now? Current recommendation: defer (preserves semantics, stays in Non-Goals).
