# Self-Review — Issue 634 plan (re-review)

Reviewer: pi. This is a disposition re-review. I first re-read the canonical
issue goals and acceptance criteria with:

```bash
mo issue view 634 --project proj_f6c141d63b6243bfbb481737b2243b87 --json number,title,body,comments,attachments,feedback,updatedAt
```

I then verified the prior MF-1 through MF-6 dispositions against the current
`proposal.md`, `design.md`, `tasks.json`, and all three capability specs, and
checked the affected current code paths for ambiguity claims, interaction
routing, Connection lookup, lease validation, thread binding, launch,
follow-up, recovery, and retention.

## Verdict: FAIL

MF-1 through MF-6 are now disposed correctly, and their fixes introduced no
must-fix regression. One pre-existing outcome-classification error remains:
the plan reports a deleted/missing selected Connection as stale/no-longer-valid,
whereas the issue explicitly requires a missing selected Connection to return
`unavailable`.

## Must-fix findings

### MF-7 — A missing selected Connection is classified as stale instead of unavailable

The issue's Acceptance Criterion #4 is explicit: **“selected Connection 缺失或
lease 失效返回 unavailable”**. Its Domain Model likewise defines
`unavailable` as the target currently being non-executable or lacking a valid
lease. Therefore, if the signed and snapshotted selected Connection has been
deleted or cannot be resolved at click time, the required visible result is
`unavailable`, with no winner or execution resources.

The current plan specifies a different result:

- Design Decision 4 step 7 resolves the chosen pair and places a vanished
  candidate under `no_longer_valid` (`design.md:254-261`).
- The action capability groups a candidate that no longer resolves or remains
  workspace-bound under “no longer valid” (`specs/slack-agent-selection-action/spec.md:44-46`),
  while its `unavailable` requirement covers only the chosen Connection's
  missing or invalid runtime lease (`specs/slack-agent-selection-action/spec.md:121-135`).
- T-003 consequently requires a “vanished candidate” to produce
  `no_longer_valid` (`tasks.json:58`).
- Design Decision 4 then maps `no_longer_valid` into the issue's broad stale
  category (`design.md:301-308`).

Concrete failure case: the chooser durably snapshots candidate
`(ProjectB, ConnectionB)`, then `ConnectionB` is deleted before the original
sender clicks its still-fresh, otherwise-valid button. The project-scoped
`AgentConnectionStore.GetAsync(ProjectB, ConnectionB)` returns null. Following
the plan, the service emits `no_longer_valid`/stale. Following issue AC #4, it
must emit the distinct visible `unavailable` outcome. Both paths create no
winner or execution, but the user-visible outcome is part of the acceptance
criterion and is therefore not optional taxonomy.

The plan must distinguish at least these cases:

- the selected Connection row is absent/deleted: `unavailable`;
- the selected Connection exists but the prompt, context, or candidate facts no
  longer match the signed durable snapshot: stale/no-longer-valid;
- the selected Connection exists but its own current lease is absent or
  invalid: `unavailable` as already planned.

The action spec, Design Decision 4, and T-003's deterministic rejection matrix
must agree on that classification.

## Previous finding dispositions

### MF-1 — More than five candidates: FIXED

The proposal, Design Decision 2, prompt spec, and T-002 consistently require
signed controls plus readable text for two to five candidates, and one readable
non-interactive re-mention fallback beyond five, with no truncation,
auto-selection, or pagination.

### MF-2 — Selected Connection used the prompt-owner lease: FIXED

The plan resolves the selected Connection's own active runtime lease by its
complete selected Project/Connection target, re-validates it, builds the
selected access decision under that lease, forbids prompt-owner lease
substitution, and returns visible `unavailable` when that lease is missing or
invalid.

### MF-3 — Action lifetime and retention bounds: FIXED

The artifacts use the issue-pinned five-minute action lifetime, settle expired
pending prompts without a new grace period, and reap finished records under the
existing `SlackEventRetentionWindow`, with no long-term audit archive.

### MF-4 — Prompt-owner current-policy re-authorization: FIXED

The click pipeline re-evaluates the prompt-owner Connection through
`SlackConnectionAccessDecider` under its own current route-validated lease
before winner commit, separately from the selected Connection's current lease
and policy. The specs and T-003 cover the mutable-policy denial cases.

### MF-5 — Cross-Project candidate ownership was lost: FIXED

The artifacts carry ordered `(ProjectId, ConnectionId)` references through
candidate discovery, the durable snapshot, signed payload, selected lookup,
lease target, authorization, CAS, identity allocation, dispatch, persistence,
and recovery. The deterministic scenarios cover cross-Project success,
denial, unavailable lease, provenance, and restart recovery.

### MF-6 — Explicit multi-Bot mentions inside an existing thread lacked correct dispatch: FIXED

The plan now records `ThreadMultiMention`, resolves the selected candidate's
binding at click time, commits `ThreadFollowup` for an already-bound choice or
`ThreadLaunch` for an unbound choice, preserves the original thread anchor,
and tests both outcomes plus recovery without reclassification.

## Re-review checks

- **Previous dispositions:** checked every prior must-fix finding; MF-1 through
  MF-6 are fixed properly.
- **Regression check:** checked the fixes across coverage, correctness,
  codebase conventions, task dependencies, testability, selected-Project
  attribution, lease targeting, authorization order, thread dispatch,
  recovery, and cleanup. No must-fix regression was found.
- **Task breakdown:** checked ordering and verifiability. T-003 depends on the
  launch extraction and durable chooser state; T-004 depends on T-003 and
  covers worker recovery/settlement/cleanup. Spec anchors resolve and
  `tasks.json` parses.
- **Pre-existing problem missed earlier:** MF-7 meets the must-fix bar because
  it directly contradicts issue AC #4's required Slack-visible result. Earlier
  reviews focused on whether missing/invalid selected leases were correctly
  separated from prompt-owner leases and then on MF-6's thread dispatch. They
  did not separately trace the preceding selected-Connection lookup failure
  through the issue's exact “Connection missing → unavailable” wording, so the
  prior correctness verdict incorrectly treated all candidate disappearance as
  stale.

## Observations

1. The action spec says the signed payload binds the chooser message identity,
   while Design Decision 3 signs the original message identity and enforces the
   chooser presentation identity separately through the acked outbox provider
   identity. The design still provides a deterministic replay/context check,
   but the terminology should be made consistent.
2. The additive migration will need explicit legacy defaults or a sentinel
   state for existing pre-fact rows. “Non-null columns” and “no backfill” do not
   by themselves make old rows structurally valid or ineligible; T-002 does
   require migration coverage and a surfaced no-execution guard, so this is an
   implementation detail rather than a separate must-fix omission.
3. The concurrency scenario says “two users” click different candidates, but
   strict actor binding permits only the original sender. Same-actor concurrent
   clicks, interaction redelivery, and adapter failover are the meaningful CAS
   races and are already required elsewhere.
4. T-003's final test criterion mentions restart recovery even though the
   obligation worker is delivered by dependent T-004. The dispatch/recovery
   core can be tested directly in T-003, but the task pass boundary should be
   interpreted carefully.
5. The issue's broad domain description treats a currently non-executable
   target as `unavailable`, while the plan preserves existing finer-grained
   `connection_disabled` and setup-nudge outcomes. Those outcomes remain
   visible and resource-free and the plan explicitly maps them to the broader
   unavailable leg; unlike MF-7, the issue does not pin a different exact
   response name for those subcases.

<promise>FAIL</promise>