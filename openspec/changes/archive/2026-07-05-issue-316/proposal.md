## Why

The `session-transcript` cluster still has two high-complexity files blocking safe iteration after #251: `entities/session/model/view.ts` (scc Complexity 251 / 775 lines) packs three independent event→view projectors (`buildChatView`, `buildTimelineView`, `buildCompactView`) into one file, and `widgets/session-transcript/model/transcript-tool-state.ts` (C=180) carries ~50 lines of duplicated merge logic in `updateToolInTurn` plus a 6-branch tool-family dispatcher (`buildLiveToolDetails`) that belongs with the tool views, not the state-transition module. Touching a single projector or the tool-call merge today means editing across unrelated code, where divergent edits collide. This finishes the cluster cleanup started by #251 under the "代码复杂度热点治理" epic (epic #22).

## What Changes

- Split `entities/session/model/view.ts` into three independent projector files — `view/chat.ts`, `view/timeline.ts`, `view/compact.ts` — and a shared `view/helpers.ts` for the common event-parsing utilities (`narrowPayload`, `extractTextChunk`, `normalizeToolName`, `is*Event` predicates, `readToolString`/`readToolValue`, etc.). Chat-specific builders (`makePromptTurn`, `upsertToolPart`, `appendTextPart`, …) move into `view/chat.ts`.
- Keep `entities/session/model/view.ts` as the public entry point: it re-exports the view types and the `viewSessionEvents<K>(events, kind)` dispatcher, so the public surface — including the imports in `widgets/coder-session/model/useSessionTimeline.ts` — is unchanged.
- Merge the two near-duplicate update branches in `transcript-tool-state.ts`'s `updateToolInTurn` (the by-`toolCallId` and by-`correlationKey` arms share the same merge/body; ~50 lines duplicated) into a single merge helper, preserving the exact output shape and field-resolution order.
- Relocate `buildLiveToolDetails` (the bash/task/skill/question/todowrite family dispatcher) out of the state-transition module to co-locate with the tool views (alongside the other tool-family rendering logic in `widgets/session-transcript/ui/tool-views/` or its model), keeping `transcript-tool-state.ts` purely about turn/part state.
- No **BREAKING** changes: every symbol currently imported from `entities/session/model/view` (`viewSessionEvents`, `SessionEvent`, `SessionViewKind`, the `Session*View`/`SessionChat*`/`SessionTimeline*` types) and from `transcript-tool-state` (`updateToolInTurn`, `createToolPart`, `mapStatusToDisplay`, `isTerminalState`, `findToolByCorrelation`, `deriveToolTarget`, `buildLiveToolDetails`, `getDisplayFields`, `getNormalizedName`, `LiveToolCall`) remains importable — moved files just change their internal address, and `view.ts` / the tool-state module re-export as needed so external consumers need zero changes.

## Capabilities

### New Capabilities

_None._ This change introduces no new user-visible or system behavior; it is a pure internal restructuring.

### Modified Capabilities

_None._ Existing behavior is preserved bit-for-bit: the three event→view projections (chat/timeline/compact output shapes), the tool-call merge/state-machine semantics, and the `details` records produced per tool family are all unchanged. The acceptance criteria are structural — file placement, de-duplication, and complexity reduction — verified by the existing `view.test.ts` and `transcript-state.test.ts` suites passing unchanged (with migrated/near-by coverage for the extracted projectors).

## Impact

- **Code** (`packages/web/src/`):
  - `entities/session/model/view.ts` — slimmed from 775 lines / C=251 to a thin dispatcher + re-exports; three projectors + shared helpers move into `entities/session/model/view/{chat,timeline,compact,helpers}.ts`.
  - `widgets/session-transcript/model/transcript-tool-state.ts` — `updateToolInTurn` de-duplicated (merge helper); `buildLiveToolDetails` removed and relocated to the tool-views area; C drops out of the 180 hotspot band.
  - `widgets/session-transcript/model/transcript-state.ts` — imports from `transcript-tool-state` unchanged (re-exported if the dispatcher moves).
  - `widgets/coder-session/model/useSessionTimeline.ts` — unchanged (still imports from `entities/session/model/view`).
- **Tests**: existing `entities/session/model/view.test.ts` and `widgets/session-transcript/model/transcript-state.test.ts` are the regression guard and must pass unchanged; projectors extracted into their own files gain co-located/migrated unit tests where coverage was previously indirect.
- **APIs / Dependencies / Systems**: none. No server, runner, or CLI changes; no new dependencies; no SSE/protocol changes.
- **Risk**: medium — the projectors feed both the session-timeline widget and the transcript widget, so a shape regression is possible but contained by the existing direct tests on the three views and the tool-call merge.
