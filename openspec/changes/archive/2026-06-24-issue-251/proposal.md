## Why

The web `session-transcript` widget — the core surface for observing agent sessions — has two files that have grown past 1100 lines / 300+ complexity (`ui/AssistantParts.tsx`, `model/useSessionTranscript.ts`), and one of them holds ~200 lines of pure diff/patch *calculation* in the UI layer, split-brain with the patch *parsing* already living in `model/transcript-tool-utils.ts`. Adjusting a single tool renderer or the transcript loading behavior today means jump-editing across these mega-files, where unrelated changes collide. This is part of the "代码复杂度热点治理" epic and is the next hotspot blocking safe iteration on transcript presentation.

## What Changes

- Move the pure diff/patch *calculation* (`buildDiffFromEdit`, `buildDiffFromPatchText`, `extractPatchForFile`) out of `ui/AssistantParts.tsx` into the model layer, consolidating it with the existing patch *parsing* (`parsePatchOperations`, `parseEditInput`) in `transcript-tool-utils.ts` to eliminate the split-brain. The diff *rendering* components (`PatchDiffView`, `DiffBlockView`, etc.) stay in the UI layer.
- Extract the pure state-transition functions (reducer logic) out of `model/useSessionTranscript.ts` into a React-independent `model/transcript-state.ts` so they can be unit-tested directly; the hook keeps only reactive wiring (subscriptions, streaming, effects).
- Split the per-tool content views out of `ui/AssistantParts.tsx` into `ui/tool-views/` (one file per tool family), leaving `AssistantParts.tsx` as dispatch + basic part views only.
- Slim `model/useSessionTranscript.ts` and `ui/AssistantParts.tsx` so no widget file remains among the web package's top complexity offenders.
- No **BREAKING** changes: the widget barrel (`index.ts`) public surface — `projectTurn`, `useSessionTranscript`, `SessionTranscriptLayout` — is untouched, and external consumers require zero changes.

## Capabilities

### New Capabilities

_None._ This change introduces no new user-visible or system behavior; it is a pure internal restructuring.

### Modified Capabilities

_None._ Existing transcript behavior — visual presentation, loading/pagination/streaming semantics, tool rendering rules, and interactions — is preserved bit-for-bit. The `agent-session-ui` and `session-timeline-ui` specs describe behavior, not implementation layout, and no spec-level requirement changes. All acceptance is structural (file placement, complexity, unchanged render output) rather than behavioral.

## Impact

- **Code** (`packages/web/src/widgets/session-transcript/`):
  - `ui/AssistantParts.tsx` — slimmed; loses diff-calculation functions and per-tool content views.
  - `model/useSessionTranscript.ts` — slimmed; loses pure state-transition functions to a new module.
  - New files: `model/transcript-state.ts` (pure reducer/state machine), `ui/tool-views/*` (per-tool-family content views). Diff calculation merges into the existing `model/transcript-tool-utils.ts` (or a co-located `model/diff-builder.ts`).
  - `index.ts` barrel, `ui/tool-registry.tsx`, `model/session-transcript-display.ts` — unchanged exports.
- **Tests**: existing `useSessionTranscript.test.tsx` and transcript component/render tests are the regression guard and must pass unchanged; the extracted pure state functions gain direct unit tests.
- **APIs / Dependencies / Systems**: none. No server, runner, or CLI changes; no new dependencies; no SSE/protocol changes.
- **Risk**: medium — the widget is the primary session-observation UI, so render-output regressions are possible but contained by existing component tests verifying output equivalence before/after each step.
