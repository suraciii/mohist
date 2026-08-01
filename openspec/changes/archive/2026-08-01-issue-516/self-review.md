# Self-Review — Issue 516 (thread discussion as agent startup context)

Reviewer role: fresh critical review of `proposal.md`, `design.md`, `tasks.json`, `specs/` against
the issue. Not a fixer — findings only. This review supersedes the prior one; it reflects the
current (post-fix) state of the artifacts.

## Verdict

**PASS** — the plan is ready to build. The two blocker contradictions from the prior review
(proposal vs. design on the fingerprint and on who reads history) are resolved and verified
consistent across proposal, design, and tasks. Remaining items are non-blocking wording nits.

## Prior findings — resolution check

| Prior finding | Status | Evidence |
|---|---|---|
| F1 fingerprint contradiction (BLOCKER) | ✅ Fixed | `proposal.md:7,27` now "**excluded**"/"deliberately **not** folded into `Fingerprint`"; aligns with `design.md` D2 (43–48) and `tasks.json` T-001 ac (line 12). |
| F2 who-reads contradiction (BLOCKER) | ✅ Fixed | `proposal.md:28` now "read **Server-side** … adapter is **unchanged**"; aligns with `design.md` D1 (36–40) and `tasks.json` T-002. |
| F3 D2 rationale over-claimed redelivery | ✅ Fixed | `design.md:46` rewritten: a plain redelivery is absorbed by inbox/reservation before the coordinator; the real reason to exclude is recovery/replay robustness. Risk bullet (line 76) re-aligned. |
| F4 capability overlap on completeness | ✅ Fixed | `proposal.md:18-19` split cleanly: API layer owns transparency/attestation, provider layer owns the refuse action. |
| N1 "byte-identical" too strong (task) | ✅ Fixed | `tasks.json` T-001 ac (line 11) reworded to "observationally identical … (only an extra null field is carried)". |
| N2 design non-goals incomplete | ✅ Fixed | `design.md` Non-Goals now mirror the issue (added uploading artifacts as Slack files; cloud-drive links). |
| N3 audit-DTO surfacing scope unclear | ✅ Fixed | `tasks.json` T-001 ac (line 15) requires the attestation be surfaced in the session input observation so the audit is inspectable. |

## Coverage check (positive)

All six issue acceptance criteria are owned by a spec requirement and a task:

| AC | Capability / Requirement | Task |
|---|---|---|
| 1 visible scope + mention is task | slack-thread-context R1, R2 | T-002 |
| 2 truncation stable + marked (ack & agent) | slack-thread-context R2, R3 | T-002 |
| 3 incomplete → no AgentJob | slack-thread-context R5 | T-002 |
| 4 empty mention → no work | slack-thread-context R4 | T-002 |
| 5 history as untrusted input | agent-startup-context R2 | T-001 |
| 6 edits/deletes immutable | slack-thread-context R6 | T-002 |

Spec format is sound: every requirement has ≥1 scenario, all scenarios use exactly 4 hashtags,
normative SHALL/MUST throughout. Task graph is a valid DAG (T-002 → T-001, strictly lower
priority); spec anchors resolve to existing requirements; `passes:false` on both.

## Remaining non-blocking nit

### N4 — Design Migration Plan still says "byte-for-byte unchanged"
`design.md:85` reads: *"Existing launches that omit it are byte-for-byte unchanged (D2 preserves
fingerprint/output)."* The parenthetical gives the accurate intent, but "byte-for-byte unchanged"
mildly contradicts the corrected task wording (`tasks.json` T-001 ac: *"only an extra null field is
carried"*): adding an append-only `[Id(n)]` field changes the serialized grain state even when null.
This is a wording-quality nit (the design's own parenthetical already conveys the right meaning), not
a technical-decision contradiction — a builder implements the same code either way. Recommend aligning
the design line to "observationally identical" for consistency with the task. Does not block building.

## What is solid

- D1 (Server reads, adapter unchanged), D3 (server-side read-only composition, `Prompt` stays
  task-only for the work label), D4 (char budget, no tokenizer, depth cap), D5 (refuse on fetch
  failure; visibility gaps ≠ incompleteness; truncation ≠ refusal), D6 (scope = visible messages
  before the mention by ts) are each justified with a rejected alternative and are mutually
  consistent, and now consistent with the proposal and tasks.
- Migration is correctly additive (append-only Orleans ids, no DB migration) with a clean rollback
  (disable the read branch). Open questions (budget defaults, bot-message filtering, boundary-table
  wording) are appropriately scoped and non-blocking.
- The two layers (`agent-startup-context` API channel; `slack-thread-context` provider) decompose
  cleanly into T-001 → T-002, each independently deliverable with embedded test coverage and no
  standalone test/technical-step tasks.

<promise>PASS</promise>
