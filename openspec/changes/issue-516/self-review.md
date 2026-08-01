# Self-Review — Issue 516 (thread discussion as agent startup context)

Reviewer role: critical review of `proposal.md`, `design.md`, `tasks.json`, `specs/` against
the issue. Not a fixer — findings only.

## Verdict

**FAIL** — the design and tasks are coherent and well-reasoned, but `proposal.md` contradicts
them on two technical decisions it had deferred and that the design later resolved the other way.
The proposal's Impact section was never reconciled. A builder reading the proposal would implement
the wrong thing on both points.

## Coverage check (positive)

All six issue acceptance criteria are owned by a spec + task:

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
priority). Design decisions D1–D6 are each justified with a rejected alternative.

## Findings that must be fixed

### F1 — Proposal contradicts design on the launch fingerprint (BLOCKER)
`proposal.md:7` states the startup context is added and *"The launch fingerprint
(`AgentLaunchCoordinatorTypes.cs:186-213`) folds it in so replays are detected"*; `proposal.md:27`
repeats that the field is added to *"the launch fingerprint."*

`design.md` D2 (line 42–47) decides the opposite — title **"EXCLUDED from the fingerprint"**,
*"It is not folded into `Fingerprint`"*, with *"Fold raw background into the fingerprint"* listed
as the rejected alternative. `tasks.json` T-001 acceptance criterion 2 and notes both require
fingerprint-exclusion.

These directly conflict. A builder following the proposal would add background to `Fingerprint()`;
the design and tasks forbid exactly that. **Fix:** reconcile `proposal.md` to the design — state
that background is excluded from the fingerprint (dedup on Slack message identity; first-accepted
snapshot persisted on the plan is authoritative).

### F2 — Proposal contradicts design on who reads thread history (BLOCKER)
`proposal.md:28` defers the decision to `design.md`, but `proposal.md:29` then presupposes the
losing option: *"`mohist-slack` adapter: likely grows thread-history fetching (`SlackWebClient`,
`adapter.ts`) per the wire-translation boundary."*

`design.md` D1 (line 36–40) resolves it the other way: *"Thread history is fetched by the Server
via a new `ISlackApiClient.ConversationsRepliesAsync`"*, with adapter-side fetching **Rejected**.
`tasks.json` T-002 implements Server-side reading and leaves the adapter untouched.

**Fix:** reconcile `proposal.md:29` to the design — the Server reads history with the decrypted
bot token (consistent with the 7 existing Server-side Slack read APIs); the adapter is unchanged.

## Findings that should be fixed

### F3 — Design D2 rationale is imprecise about the redelivery path (MEDIUM)
`design.md:45` justifies fingerprint-exclusion with: *"Folding volatile history into the
fingerprint would make a Slack at-least-once redelivery (after the thread grew) raise
`LaunchIdempotencyConflictException`."* But a plain Slack redelivery is deduped earlier — the
provider inbox dedups on `(ConnectionId, SlackMessageIdentity)` and the launch reservation returns
`Bound`, so a duplicate mention becomes a follow-up (or conflict) and the coordinator is not
re-invoked with drifted background (`SlackConnectionRoutes.cs:1518-1531`, `1490-1503`).

The **decision** (exclude) is still correct; the **stronger rationale** is recovery/replay
robustness (`AgentLauncher.ResumeIdempotentAsync`) plus the principle that the mention-message
identity — not volatile content — is the dedup boundary. **Fix:** tighten D2's rationale so it
does not over-claim a redelivery path that the inbox already absorbs.

### F4 — Overlapping capability descriptions for the completeness contract (MINOR)
`proposal.md:18` attributes *"the caller-side completeness contract (refuse rather than submit
incomplete background)"* to `agent-startup-context`, while `proposal.md:19` also lists
*"refuse-on-incomplete (no AgentJob)"* under `slack-thread-context`. The specs resolve this
sensibly — `agent-startup-context` R3 carries the transparency (state what was read + truncation
marker), and `slack-thread-context` R5 owns the refuse action — but the proposal blurbs make the
two capabilities sound like they overlap on the same behavior. **Fix:** clarify in the proposal
that the API-layer capability owns *transparency/attestation* and the provider-layer capability
owns the *refuse* action.

## Nits (non-blocking)

- **N1** `tasks.json` T-001 criterion 1 asks for *"byte-identical plan, fingerprint, session input,
  and dispatch payload."* Adding an append-only `[Id(n)]` field changes the serialized record, so
  "byte-identical" is too strong; reword to *observationally identical* (identical fingerprint,
  dispatched prompt, session-input text, job references).
- **N2** `design.md` Non-Goals could explicitly mirror the issue's non-goals (e.g. "uploading
  Mohist artifacts as Slack files") for completeness, though they are inherited from the issue.
- **N3** `agent-startup-context` R3 requires the context to *"flow to … the session input audit
  record"*; T-001 adds the field to `AgentSessionInputRecord` (satisfying it) but does not say
  whether it must also surface in `AgentSessionInputObservationDto`. Clarify whether API/audit
  surfacing is in-scope for T-001 or deferred.

## What is solid

- D1 (Server reads), D3 (server-side read-only composition, Prompt stays task-only for the work
  label), D4 (char budget, no tokenizer), D5 (refuse-on-fetch-failure; visibility gaps ≠
  incompleteness; truncation ≠ refusal), D6 (scope = visible messages before the mention by ts)
  are each well-argued with a rejected alternative and are internally consistent with the specs
  and tasks.
- The fingerprint-exclusion decision and the Server-side-read decision are correct; the problem is
  solely that the proposal still states the rejected options.
- Migration is correctly characterized as additive (append-only Orleans ids, no DB migration),
  with a clean rollback (disable the read branch).

<promise>FAIL</promise>
