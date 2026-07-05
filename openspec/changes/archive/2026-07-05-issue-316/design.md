## Context

This change finishes the `session-transcript` cluster cleanup that #251 started under the "代码复杂度热点治理" epic (epic #22). Two files still sit in the `C=180+` hotspot band and block safe iteration:

- `packages/web/src/entities/session/model/view.ts` — **C=251 / 775 lines**. Packs three independent event→view projectors (`buildChatView` 408–499, `buildTimelineView` 505–672, `buildCompactView` 674–766), a set of shared event-parsing helpers (`narrowPayload`, `extractTextChunk`, `normalizeToolName`, the `is*Event` predicates, `readToolString`/`readToolValue`, … lines 118–251), and chat-only builders (`makePromptTurn`, `upsertToolPart`, `appendTextPart`, … lines 248–406) into one module.
- `packages/web/src/widgets/session-transcript/model/transcript-tool-state.ts` — **C=180 / 393 lines**. The `updateToolInTurn` body (246–393) carries two near-identical merge branches (by `toolCallId` 257–307 and by `correlationKey` 309–364) that differ in exactly one line, and `buildLiveToolDetails` (121–208) is a 6-branch tool-family dispatcher that has nothing to do with turn/part state transitions.

**Stakeholders / consumers** (confirmed by grep, the full importer set):

| Consumer | Imports from | Change required |
|---|---|---|
| `widgets/coder-session/model/useSessionTimeline.ts` | `entities/session/model/view` | none |
| `widgets/session-transcript/model/transcript-state.ts` | `./transcript-tool-state` | none |
| `widgets/session-transcript/model/useSessionTranscript.ts` | (via `./transcript-state`) | none |

**Regression guard**: `entities/session/model/view.test.ts` (483 lines) and `widgets/session-transcript/model/transcript-state.test.ts` (619 lines) directly exercise the three projections, the merge precedence, and every tool family. These suites are the contract; they must pass byte-for-byte unchanged.

**Constraints**:

- Behavior-preserving. The proposal and both specs forbid any change to projection output shape, merge precedence/field-resolution order, or `details` records per tool family.
- No new dependencies, no protocol/SSE/server/runner/CLI surface changes (frontend-only refactor).
- The repo runs `TreatWarningsAsErrors`-equivalent discipline on the web package: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` are the gate.

## Goals / Non-Goals

**Goals:**

- Decompose `view.ts` into one file per projector (`view/chat.ts`, `view/timeline.ts`, `view/compact.ts`) plus a shared `view/helpers.ts`, leaving `view.ts` as a thin dispatcher + re-export hub.
- Collapse the two duplicate merge branches in `updateToolInTurn` into a single parameterized helper; move `buildLiveToolDetails` out to co-locate with the tool-family views.
- Drop every touched file below the `C=180` hotspot threshold.
- Keep the public import surface of both modules byte-identical so the two named consumers compile unchanged.

**Non-Goals:**

- Any change to event→view projection semantics, merge precedence, or tool-family `details` shape.
- Re-doing #251's `AssistantParts.tsx` / `useSessionTranscript.ts` splits.
- Performance work (e.g. removing the in-place mutation of `toolCallMap`, or rewriting the projection loops).
- Expanding test coverage beyond migrating/moving existing cases that pin to a single projector. New behavior assertions are out of scope; this is a refactor.
- Touching server / runner / CLI.

## Decisions

### D1 — `view.ts` split layout: types stay in `view.ts`, projectors do type-only back-imports

The spec mandates the file set `view/{chat,timeline,compact,helpers}.ts` and requires `view.ts` to remain the public entry point that re-exports the types and `viewSessionEvents`. The 13 view types (`SessionEvent`, `SessionViewKind`, `SessionChatPart`, … `SessionView`) physically stay at the top of `view.ts`, and the three projector files consume them via `import type { … } from '../view'`.

- **Rationale**: keeps the public type surface at its existing address with zero churn, and `import type` is erased at compile time so there is **no runtime circular import** between `view.ts → view/chat.ts → view.ts`.
- **Alternative considered**: move types to a new `view/types.ts`. Rejected — adds a file not in the spec's enumerated set, and the type-only back-import achieves the same decoupling without it. Revisit only if a linter flags the type-only cycle (it should not; `verbatimModuleSyntax` accepts `import type` cycles).

`view.ts` after the split contains: (1) the 13 type declarations, (2) the `viewSessionEvents<K>` dispatcher that imports the three `build*View` functions from `./view/{chat,timeline,compact}`, (3) `export { … }` re-exports of the types. No projector body, no helper body.

### D2 — Helper placement boundary

- **`view/helpers.ts`** gets every helper used by ≥2 projectors: `isRecord`, `narrowPayload`, `getStringProp`, `getNumberProp`, `extractTextChunk`, `normalizeRaw`, `normalizeToolName`, `mapToolState`, `mapTerminalStatus`, `defaultToolStatus`, `toolRecord`, `readToolString`, `readToolValue`, and the `is*Event` predicates (`isInputEvent`, `isAssistantTextEvent`, `isAssistantReasoningEvent`, `isToolEvent`, `isSessionClosedEvent`, `isLivenessEvent`).
- **`view/chat.ts`** gets chat-only builders: `KNOWN_PROMPT_KINDS`, `nextPartId`, `appendTextPart`, `appendReasoningPart`, `closeOpenTextParts`, `upsertToolPart`, `pushErrorPart`, `makeInitialTurn`, `makePromptTurn`, plus `buildChatView`.
- **`view/timeline.ts`** owns `isCompactionEvent` (timeline-only predicate currently at 501–503) and `buildTimelineView`.
- **`view/compact.ts`** owns `buildCompactView`.

**Boundary rule** (for the implementer, not new policy): a symbol is "shared" iff ≥2 projectors reference it; everything else is projector-local. This keeps `helpers.ts` from accreting chat-only helpers and matches the spec's enumeration exactly.

### D3 — `updateToolInTurn` de-duplication: one `mergeToolPart` helper, parameterize the only behavioral delta

The `toolCallId` arm (257–307) and `correlationKey` arm (309–364) are textually ~50 lines apart and differ in **exactly one statement**: the correlation arm writes the incoming `toolCallId` onto the matched part (`toolCallId,` at line 344), the `toolCallId` arm does not (it omits that key, leaving the matched part's id). Every other line — `rawInput`/`rawOutput` fallback chain, `metadata`/`details` selection, `startedAt` selection, `getDisplayFields` resolution, `stringifyPayload`, `parseEditInput`/`parsePatchOperations`-derived `changedFiles`, `mapStatusToDisplay`, `completedAt = isTerminalState(newStatus) ? now : …` — is identical.

Extract one file-local helper inside `transcript-tool-state.ts`:

```ts
function mergeToolPart(
  toolPart: ToolPart,
  updates: Partial<LiveToolCall>,
  now: string,
  overrideToolCallId?: string,   // correlation arm passes incoming toolCallId; toolCallId arm omits
): ToolPart { /* the single shared body */ }
```

`updateToolInTurn` then becomes: find by `toolCallId` → `mergeToolPart(existing, updates, now)`; else find by `correlationKey` → `mergeToolPart(correlated, updates, now, toolCallId)`; else `createToolPart(...)` append.

- **Rationale**: the only behavioral difference (correlation arm overwrites `toolCallId`) becomes an explicit optional parameter, preserving the spec's "toolCallId-overwrite difference between arms is preserved" scenario verbatim. The merge body literally no longer appears twice.
- **Alternative considered**: extract only the field-resolution expressions (rawInput/rawOutput/…) into a smaller helper and leave the two `turn.assistant.map` wrappers separate. Rejected — the wrapper is exactly the duplicated part, so it would leave the duplication intact.
- **`now` parameter**: the helper receives `now` rather than calling `new Date()` itself, so the existing `transcript-state.test.ts` timing pattern (which reads `completedAt` presence, not wall-clock) is undisturbed and the helper stays pure.

### D4 — `buildLiveToolDetails` relocation target: `widgets/session-transcript/ui/tool-views/live-details.ts`

The dispatcher is pure data derivation (no JSX, no React), but the spec says to "co-locate with the tool views (alongside the other tool-family rendering logic in `widgets/session-transcript/ui/tool-views/` or its model)". That directory already hosts non-JSX logic in `shared.tsx` (`getToolDisplayLabel`, `getToolDisplayArgs`, `getRegistrySubtitle`, …), so a `.ts` sibling `live-details.ts` is consistent with the existing pattern.

- **Re-export strategy**: `transcript-tool-state.ts` adds `export { buildLiveToolDetails } from '../../ui/tool-views/live-details'` so `transcript-state.ts`'s import specifier `./transcript-tool-state` keeps resolving every symbol it uses today. After the move, `transcript-tool-state.ts` contains only turn/part state-transition logic (`updateToolInTurn`, `createToolPart`, `mapStatusToDisplay`, `isTerminalState`, `findToolByCorrelation`, `deriveToolTarget`, `getDisplayFields`, `getNormalizedName`) plus the re-export.
- **Alternative considered**: place at `widgets/session-transcript/model/live-tool-details.ts` (keeps "model vs ui" blur minimal). Rejected — the spec explicitly names the tool-views area, and `shared.tsx` already establishes that the tool-views directory holds non-UI helpers. Going to `model/` would also lengthen the re-export path for no benefit.
- **Dependencies carried along**: `buildLiveToolDetails` uses `asPayloadRecord`/`asRecord`/`getNumber`/`getString`/`truncatePreview` from `./transcript-payload` (path becomes `../../model/transcript-payload`) — no other state-transition symbols, so the move is self-contained.

### D5 — Verification contract: existing tests are the regression oracle, `scc` is the complexity gate

- **Behavior**: `entities/session/model/view.test.ts` and `widgets/session-transcript/model/transcript-state.test.ts` run **unedited**. They already cover empty input, input-first, assistant-first (legacy-missing turn), tool-only, interleaved deltas, all `tool_call.*` transitions, `session.closed` terminal states, liveness failures, compaction with/without an existing round, and every `buildLiveToolDetails` family. Passing them unchanged is the proof of behavior preservation.
- **Complexity**: `scc --by-file` on `view.ts`, `view/chat.ts`, `view/timeline.ts`, `view/compact.ts`, `view/helpers.ts`, and `transcript-tool-state.ts` — every file must report `Complexity < 180`. (Pre-change baseline measured: `view.ts` C=251, `transcript-tool-state.ts` C=180.)
- **Types**: `npm run typecheck -w packages/web` must stay green (catches any accidental runtime cycle from D1 if a non-type import slips in, and any dropped export).
- **Co-located projector tests**: where `view.test.ts` has clearly projector-scoped `describe` blocks (chat/timeline/compact), the implementer *may* migrate those blocks into `view/{chat,timeline,compact}.test.ts` to give each projector a co-located suite. This is optional and strictly a move of existing assertions — no new behavior cases. If it risks disturbing the green suite, skip it and leave `view.test.ts` as the single regression oracle.

## Risks / Trade-offs

- **[Type-only circular import between `view.ts` and `view/*.ts` is mis-flagged as a real cycle]** → Mitigation: use `import type` exclusively for the back-import; `npm run typecheck` will catch any non-type import that slips in. If a tool complains, fall back to D1's alternative (extract `view/types.ts`).
- **[The `mergeToolPart` helper drifts from one arm's semantics during extraction]** → Mitigation: the helper has exactly one optional parameter (`overrideToolCallId`) representing the only documented behavioral delta; the two existing scenarios in `transcript-tool-state/spec.md` (`toolCallId-match path is invariant`, `correlationKey-match path is invariant`) pin both arms field-by-field and must pass unchanged.
- **[A transitive importer of `buildLiveToolDetails` outside the known set is missed]** → Mitigation: grep over `packages/web/src` confirms the only importer is `transcript-state.ts`; the re-export from `transcript-tool-state.ts` (D4) is belt-and-suspenders so even a missed importer keeps resolving.
- **[Re-export masks a now-dead import inside `transcript-state.ts`]** → Mitigation: typecheck + the unchanged test suite catch any symbol-resolution regression; no lint suppression added.
- **[Co-located test migration (D5 optional) destabilizes the regression suite]** → Mitigation: it is explicitly optional; if the move is non-trivial, leave `view.test.ts` and `transcript-state.test.ts` intact. Behavior preservation outranks test co-location.
- **[Complexity drops below 180 but a new hotspot appears elsewhere in the cluster]** → Accepted trade-off: this issue's scope is the two named files. A new hotspot would be a follow-up under epic #22, not a failure of this change.

## Migration Plan

This is a pure frontend refactor with **no data, schema, protocol, or API migration**.

1. **Branch**: PR off `master` (the issue's `resolvedBaseBranch`).
2. **Order of operations** (each step leaves the suite green, commit per step):
   1. Create `view/helpers.ts` — move shared helpers; update `view.ts` to import them; `view.test.ts` untouched. → `typecheck + test`.
   2. Extract `view/chat.ts` (`buildChatView` + chat-only builders); `view.ts` imports `buildChatView` and re-exports types. → `typecheck + test`.
   3. Extract `view/timeline.ts` (incl. `isCompactionEvent`). → `typecheck + test`.
   4. Extract `view/compact.ts`. → `typecheck + test`; `view.ts` is now thin.
   5. De-duplicate `updateToolInTurn` via `mergeToolPart` in `transcript-tool-state.ts`. → `typecheck + test`.
   6. Move `buildLiveToolDetails` to `ui/tool-views/live-details.ts`; add re-export from `transcript-tool-state.ts`. → `typecheck + test`.
   7. Run `scc --by-file` on all touched files; confirm each `Complexity < 180`.
3. **Deploy**: merge the PR — standard web build/deploy, no server/runner/CLI restart, no `mo update`.
4. **Rollback**: `git revert` the merge commit. No state to restore (pure code move), no backward-compat shims needed because no public symbol moved address.

## Open Questions

- **Co-located projector tests (D5)**: migrate existing `describe` blocks from `view.test.ts` into per-projector `view/{chat,timeline,compact}.test.ts`, or leave the single oracle file? Resolve during implementation by trying the move; if the suite stays green trivially, do it; otherwise defer to a follow-up. Not blocking.
- **`isCompactionEvent` final home**: placed in `view/timeline.ts` per D2 (timeline-only predicate). If a future compact/chat projector needs it, promote to `view/helpers.ts` then. Not blocking for this change.
