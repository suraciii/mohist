# Self-Review — Issue 634 plan (re-review)

Reviewer: pi. This is a disposition re-review. I first re-read the canonical
issue goals and acceptance criteria with:

```bash
mo issue view 634 --project proj_f6c141d63b6243bfbb481737b2243b87 --json number,title,body,comments,attachments,feedback,updatedAt
```

I then checked the prior MF-1 through MF-5 dispositions against the current
`proposal.md`, `design.md`, `tasks.json`, and all three capability specs, and
traced the changed plan through the current channel-ingress, thread-binding,
lease, interaction, outbox, launch, and follow-up paths.

## Verdict: FAIL

MF-1 through MF-5 are now disposed correctly, but one pre-existing must-fix
coverage/correctness gap remains. Explicit multi-Bot mentions inside an
existing Slack thread are already treated as ambiguous by the current
codebase, yet the plan specifies dispatch only for root multi-mentions and
unmentioned replies in multi-bound threads. It therefore does not satisfy the
issue's explicit requirement to route root, existing-thread-session, and new
thread-launch cases from the original durable provenance.

## Must-fix findings

### MF-6 — Explicit multi-Bot mentions inside an existing thread have no correct selection dispatch

The issue's Acceptance Criterion #8 explicitly requires all three routing
shapes to use the original durable provenance: **root**, **existing thread
session**, and **new thread launch**. Acceptance Criterion #2 also requires the
selected Bot alone to process the original message while preserving its
conversation/thread route.

This is a reachable existing-code case, not speculative scope. Channel ingress
checks `mentionedWorkspaceBots.Count >= 2` before the single-Bot and
thread-binding branches, without restricting that check to root messages
(`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.ChannelIngress.cs:119-138`).
Consequently, a reply inside an existing thread that explicitly mentions two
Bots already enters the ambiguity prompt path. The current single-Bot behavior
shows the two outcomes a later selection must preserve:

- if the addressed Bot already owns a binding in that thread, the message is a
  follow-up to that bound Session (`SlackConnectionRoutes.ChannelIngress.cs:149-158`);
- if the addressed Bot is not yet bound while another Bot is bound, it launches
  and binds a new Session under the existing thread anchor
  (`SlackConnectionRoutes.ChannelIngress.cs:176-189`).

The plan instead defines the ambiguity shapes as only a **root** message with
multiple mentions or an **unmentioned** reply in a multi-bound thread
(`specs/slack-agent-selection-prompt/spec.md:1-15`). Its execution spec and
design likewise provide only:

- “root multi-mention” → launch a Session, using the original message ts as the
  thread root (`design.md:336-349`); and
- “ambiguous multi-bound-thread reply” → follow up the chosen already-bound
  Session, never launch a Session (`design.md:350-357`;
  `specs/slack-selection-execution-attribution/spec.md:1-21`).

T-003 repeats the same two-way split and has no acceptance scenario for an
explicit multi-mention inside an existing thread (`tasks.json:54-65`).

Concrete failure case: thread `T0` is already bound to Connection A; Connection
B is an eligible workspace Bot but is not bound to `T0`. A message in `T0`
explicitly mentions both A and B. The existing ingress creates a chooser. If A
is selected, the original message must continue A's existing Session. If B is
selected, it must launch B's new Session and bind it to `T0`. The planned
“root multi-mention” branch would instead treat the reply's own message ts as a
new thread root, while the planned “multi-bound-thread reply” branch cannot
launch B at all. Neither result satisfies issue AC #8, and both lose the
required original conversation/thread routing from AC #2.

The plan must classify dispatch using the retained original ingress shape and
thread anchor, not only “root mention versus unmentioned multi-bound reply.”
For an explicit multi-Bot mention in an existing thread, selection must resolve
the chosen Project/Connection's current binding at click time: dispatch a
follow-up when that candidate is already bound, or run the existing
launch-and-bind path under the original thread anchor when it is not. The specs
and T-003 need deterministic scenarios for both outcomes, including no new
Session for the bound choice, a new selected-Connection Session for the
unbound choice, and original thread/provenance preservation.

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
substitution, and returns visible `unavailable` when the selected lease is
missing or invalid.

### MF-3 — Action lifetime and retention bounds: FIXED

The artifacts now use the issue-pinned five-minute action lifetime, settle
expired pending prompts without a new grace period, and reap finished records
under the existing `SlackEventRetentionWindow`, with no long-term audit
archive.

### MF-4 — Prompt-owner current-policy re-authorization: FIXED

The click pipeline now re-evaluates the prompt-owner Connection through
`SlackConnectionAccessDecider` under its own current route-validated lease
before winner commit, separately from the selected Connection's own current
lease and policy. The specs and T-003 cover the mutable-policy denial cases.

### MF-5 — Cross-Project candidate ownership was lost: FIXED

The current artifacts carry ordered `(ProjectId, ConnectionId)` candidate
references through root and thread discovery, the durable snapshot, signed
payload, selected lookup, lease target, authorization, CAS, pre-allocation,
dispatch, persistence, and recovery. They include deterministic cross-Project
success, denial, unavailable, provenance, and restart-recovery coverage. No
remaining operative path in the plan intentionally infers the selected Project
from the prompt-owner route.

## Re-review checks

- **Every previous must-fix disposition:** checked; MF-1 through MF-5 are fixed
  properly as summarized above.
- **Regressions introduced by the MF-5 fix:** checked; no must-fix regression
  was found in complete candidate identity, candidate ordering, selected lease
  targeting, separate authorization, selected-Project identity allocation,
  dispatch, recovery, task dependencies, or retention.
- **Pre-existing problem missed earlier:** FAIL due to MF-6. Earlier reviews
  accepted the plan's “root multi-mention / unmentioned multi-bound reply”
  categories as exhaustive and concentrated on candidate count, dual
  authorization, leases, retention, and cross-Project identity. They did not
  trace the current multi-mention branch's ordering against `ThreadTs`, nor
  reconcile that reachable branch with the issue's explicit three-part AC #8
  wording (“root, existing thread session, and new thread launch”). That is why
  the prior per-dimension verdicts did not catch this gap.

## Observations

1. The action spec says the signed payload binds the chooser message identity,
   while Design Decision 3 signs the original message identity and enforces the
   chooser presentation identity through the acked outbox provider identity.
   The design supplies a deterministic context check, but the terminology is
   still inconsistent.
2. The additive migration needs explicit defaults/sentinels for existing
   pre-fact rows. Non-null columns plus “no backfill” do not alone make those
   rows structurally ineligible; T-002 does require a surfaced no-execution
   guard and test, so this is implementation detail rather than a separate
   must-fix omission.
3. The concurrency scenario says “two users” click different candidates, but
   strict actor binding permits only the original sender. Same-actor concurrent
   clicks, Slack redelivery, and adapter failover are the meaningful winner-CAS
   races and are already required elsewhere.
4. T-003's final test criterion mentions restart recovery even though the
   obligation worker is implemented by dependent T-004. The recovery core may
   be testable directly in T-003, and T-004 independently requires worker
   recovery specs, but the task pass boundary should be interpreted carefully.

<promise>FAIL</promise>