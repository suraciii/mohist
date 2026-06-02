# Self Review Report

## Result: PASS

The proposal, design, two spec files, and seven-task plan are aligned, complete, consistent, feasible, and free of circular dependencies. All issue-47 acceptance criteria are covered by traced proposal entries, every spec requirement has at least one implementing task, and task dependencies all point to lower-priority task IDs with no cycles.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The `spec` field anchors in `tasks.json` for `T-002`, `T-003`, and `T-005` drop punctuation from the matching `### Requirement:` header in `specs/coder-session-tracking/spec.md` (`reasoning, text, and tools` → `reasoning-text-and-tools`, `raw input, output, metadata, and details` → `raw-input-output-metadata-and-details`) and for `T-005` the `legacy-missing` hyphen round-trips to a space when normalized. The semantic mapping is correct in every case (each task description and acceptance criterion clearly targets the matching requirement), and the existing precedent in `openspec/changes/archive/2026-05-19-229-.../self-review.md` and other archived self-reviews shows the project tolerates this style of anchor so long as the spec file and the intent match.
  Verification: Re-derived every task's referenced spec section by hand from the task `description` and `acceptanceCriteria` and confirmed each maps to an `### Requirement:` heading that exists in the corresponding `spec.md`. A python script that lowercases and slugifies each anchor finds the corresponding spec requirement when punctuation is ignored. Ran a dependency audit: all `dependsOn` IDs exist, all target a strictly lower-priority task, and no cycles were detected.
  Status: not-repaired (intentional — semantics are correct and fixing the slug would not change behavior)

- [ID: item-2]
  Severity: info
  Scope: completeness
  Evidence: `T-007` (Web tests) declares a single `spec` field pointing to `specs/agent-session-ui/spec.md#session-page-prompt-card-reflects-the-real-mohist_prompt`, but its description and acceptance criteria cover five additional `agent-session-ui` requirements (multi-turn, reasoning/text interleaving, liveness visibility, legacy-missing rendering, raw tool payload disclosure).
  Verification: Re-read `specs/agent-session-ui/spec.md` and confirmed `T-007`'s acceptance list is a strict superset of the scenarios under the six `agent-session-ui` requirements. The test task is the only web-facing task in this change, and the description explicitly says it covers the new transcript shape end-to-end.
  Status: not-repaired (single-anchor convention is consistent with prior archived self-reviews where one test task anchors to the spec file and trusts the description)

## Blocking Items

None.

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: Design `D1` routes every non-`mohist_prompt` event into "the most recently opened turn's assistant list". If events arrive strictly before the first `mohist_prompt` (e.g. a `coder_recovery_status` recorded during session bootstrap), there is no currently open turn and the events have no defined home. The runner's normal order puts `mohist_prompt` first, so this is a theoretical edge rather than an observed failure.
  SuggestedAction: Decide during `T-001` implementation whether such events are dropped, attached to a synthetic pre-prompt slot, or held until the first `mohist_prompt` opens. If the runner can guarantee `mohist_prompt` is the first event per session, document that invariant in the runner rather than over-engineering the projection.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: completeness
  Evidence: `T-003` describes a per-turn `Dictionary<toolCallId, int>` for tool part merging, but does not specify what happens if a `tool_call_update` event arrives for a `toolCallId` that has no preceding `tool_call` in the same turn (update-before-create). The merge rule "replace the element at that index" assumes the index exists.
  SuggestedAction: During `T-003` implementation, decide whether to (a) materialize a tool part on update-before-create from the update payload, (b) drop the orphaned update, or (c) treat the update as a delayed create and append a new part. Add a small spec assertion for whichever rule is chosen.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: feasibility
  Evidence: Design Open Question 1 leaves a small implementation choice about the exact part shape used for `agent_liveness_status` (proposal says "divider or error part"; design D4 picks `ErrorPart { kind: 'recovery', message }` but notes a `DisplayDividerPart` may be introduced "if the visible rendering needs to differ"). `T-004` uses the same error-part shape.
  SuggestedAction: When implementing `T-004`, read `useSessionTranscript`'s live `onAgentEvent('agent_liveness_status', …)` handler in the web package to confirm whether the live part is rendered as a `recovery` error card or a distinct divider. Match that shape exactly so live and replay render identically. If a divider part is required, add it to the `ErrorPart` / part-type definitions and extend `T-006` / `T-007` with a small assertion.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: consistency
  Evidence: Design Open Question 3 defers removal of `session-transcript-display.ts`'s `applyReasoningReorder` post-pass. With the backend now authoritative for ordering (D2), the post-pass is defense in depth.
  SuggestedAction: After this change ships, run a small prod sample to confirm the post-pass is a no-op on backend-projected transcripts, then remove it in a follow-up to simplify the rendering pipeline. Do not remove it in this change.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: completeness
  Evidence: The proposal notes "Earlier issues (#158, #184, #190, #205, #220) already moved the rest of the transcript to a real-conversation shape". The change builds on that history, and `T-007` assumes the existing `SessionTranscriptView` / `PromptBlock` / `TurnList` / `ToolCallCard` already accept the new payload shape.
  SuggestedAction: Before starting `T-007`, briefly read each of those four components to confirm they actually pass `turn.user.text` (not `user.summary.title`) into the expand / copy controls and that `ToolCallCard` already surfaces `rawInput` / `rawOutput` / `metadata` / `details`. If any component still consumes `session.Title` or strips raw fields, fold the small alignment into `T-007` rather than opening a new task.
  Status: follow-up

<promise>PASS</promise>
