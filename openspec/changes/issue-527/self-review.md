# Self-Review — issue-527 (Slack files as Agent input)

## Verdict summary

The proposal/specs capture the issue's intent and acceptance criteria faithfully, the spec format is correct, and the task graph is a valid DAG. **However, the design's idempotency model (D5) contains a correctness gap that would cause duplicate or lost attachments on Slack redelivery, and a spec scenario rests on reasoning that is unsound.** This must be fixed in `design.md` before build.

**Promise: FAIL**

---

## What is solid

- **Spec format** — `specs/slack-attachment-entry/spec.md` uses exactly `### Requirement` / `#### Scenario` (4 hashtags), normative SHALL language throughout, and every requirement has ≥1 scenario (9/9). Verified by grep.
- **Issue coverage** — all six acceptance criteria map to spec requirements; the four Non-Goals are honored (thread-history text, URL scraping, artifact upload, cross-session library).
- **Capability discipline** — the new spec correctly confines itself to Slack-specific *entry* behavior and does not re-state the provider-agnostic invariants issue-513 already governs (`session-input-attachments`, `attachment-input-lifecycle`, `agent-attachment-delivery`).
- **Task graph** — valid JSON, acyclic, every `dependsOn` points to a strictly lower-priority task, each task has verifiable acceptance criteria including test coverage. T-001/T-002 parallel; T-003 fans in; T-004 follows.
- **Reuse claim is accurate** — verified that `EnsureInitialLaunchCommand.Attachments` (`IAgentSessionGrain.cs:335`), `AcceptFollowupCommand.Attachments`/`PreMintedInputId`/`AttachmentResults` (`:229`–`253`), and `AgentLaunchCoordinatorCommandEnvelope.Attachments`/`PreMintedInputId` (`AgentLaunchCoordinatorGrain.cs:481`–`498`) already carry the fields; the grains need no shape change. The runner `openAgentInputAttachment` is genuinely owner-scoped and provider-agnostic.
- **`Source = "upload"` hardcode** — confirmed at `AttachmentService.cs:520`; D4's column approach is the right fix.

---

## Findings

### F1 (BLOCKER) — D5's redelivery idempotency is unsound for attachments; causes duplicate or lost attachments

D5 asserts: *"the preminted `{sessionId}/{inputId}` owner is deterministic … so even if a bind is reached twice, the second pass re-validates rows already owned by that owner … rather than colliding."* This is **false** for the proposed per-message ingest model, and it breaks a spec scenario.

What the code actually does (verified, `SlackConnectionRoutes.cs:1128`–`1174`):

1. The provider inbox is accepted and `AlreadyExisted` is computed **before** the launch block.
2. The launch block re-runs whenever `route.SessionId` is null (`:1133` `if (string.IsNullOrWhiteSpace(sessionId))`) — **regardless of `AlreadyExisted`**. `SetRouteSessionIdAsync` (`:1142`) only runs *after* a successful launch, so a crash between inbox-accept and `:1142` leaves `sessionId` null on redelivery → the block re-executes.
3. If attachment fetch+ingest+bind is placed before the launch (as D1 prescribes, mirroring Web), redelivery runs it **again**, minting **fresh** `att_{guid}` ids (D5 explicitly chose per-message ingest, rejecting deterministic ids). `ValidateAndBindAgentInputAsync` then claims these *new* pending rows under the same owner — so the same `SessionInput` ends up with **two** bound copies of the same Slack file. D5's "re-validates rather than collides" only holds if the second pass presents the *same* attachment ids, which fresh ingest guarantees it will not.

Two downstream consequences, both bad:

- **If no idempotency fingerprint includes attachment ids** → silent duplicate attachments bound to one input.
- **If a fingerprint does include attachment ids** (as Web launch does, `AgentSessionLaunchRoutes.cs:102`–`116`) → every redelivery conflicts, because the ids are non-deterministic. The Slack path would need a stable fingerprint it currently cannot produce.

This also defeats spec scenario *"A restart does not lose pending file binding"* (`spec.md:127`–`130`): there is no specified re-driver for a post-accept/pre-bind crash, and D5's claim that *"Slack redelivers the unacked message"* is not substantiated — the design never states the Slack-ack timing the whole argument hinges on.

**Must be fixed in `design.md`.** The clean resolution D5 rejected without considering: **deterministic attachment ids scoped to (Slack message identity, file id)**, e.g. `att_slack_{messageTs}_{fileId}`, ingested insert-if-absent. This makes redelivery re-ingest a no-op (same row) and re-bind a true no-op (already owned by the deterministic preminted owner), while a *different* message carrying the same physical Slack file gets a different id (no cross-session `AlreadyBound` — the exact property D5 wanted). D5's rejected alternative conflated "deterministic by file id" with "global single-owner"; a message-scoped key separates the two concerns. Either adopt this, or specify an alternative that makes attachment binding idempotent across the inbox-accept → launch-complete window, and state the Slack-ack timing assumption explicitly.

### F2 (consistency) — Proposal Impact contradicts Design D5 on the inbox schema

`proposal.md:23` states the `SlackProviderInboxDraft` *shall carry file references so redelivery resolves to the same bound attachments*. Design D5 states the opposite — redelivery is handled by identity dedup + re-fetch, implying **no** inbox schema change. These cannot both be true. Given F1, the inbox may indeed need to carry a stable key (or the message-scoped attachment ids) to support correct redelivery binding. Reconcile proposal ↔ design; whichever direction is chosen, the other document must be updated to match.

### F3 (minor) — T-003 omits the Web launch resume/fingerprint step

T-003's notes cite `AgentSessionLaunchRoutes.cs:182`–`256` (premint + prebind + rollback) but not the preceding `ResumeIdempotentAsync` + attachment-id fingerprint step (`:102`–`137`). For Slack the attachment set is derived from the message, so the conflict case is less acute — but the builder needs to decide explicitly whether to include attachment ids in the coordinator fingerprint, and that decision is coupled to F1's resolution (stable ids → stable fingerprint). Add a note so the builder doesn't silently skip it.

### F4 (minor/clarity) — "Slack file identifier" must stay out of the stored record

`spec.md:154` requires the stored record to contain *no Slack file identifier*. Design D2 has the envelope carry the Slack file id transiently (needed to fetch). These are consistent today (envelope ≠ stored record), but if F1's fix persists any key derived from the Slack file/message identity, take care that the raw Slack file id itself does not land in `AttachmentRow`/observation. Worth a one-line guard in the design to keep the spec invariant unambiguous.

---

## Format / mechanical checks (all pass)

- Spec headings: 9 `### Requirement`, all scenarios at `####` (verified).
- `tasks.json`: valid JSON; 4 tasks; DAG; `dependsOn` → strictly lower priority; all `mode=AFK`, `type=WRITE`, `passes=false`, ≥1 acceptance criterion each.

## Recommendation

Resolve F1 (and the coupled F2/F3) in `design.md` — specify an idempotent attachment-binding strategy for the redelivery window and state the Slack-ack timing assumption. F4 is a one-line clarification. With those fixes the plan is ready to build.

<promise>FAIL</promise>
