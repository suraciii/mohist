# Self-Review — Issue 634 plan (re-review)

Reviewer: pi. This is a disposition re-review, not a second full sweep. I first
re-read the canonical issue with:

```bash
mo issue view 634 --project proj_f6c141d63b6243bfbb481737b2243b87 --json number,title,body,comments,attachments,feedback,updatedAt
```

I then checked every must-fix finding from the previous review against the
current `proposal.md`, `design.md`, `tasks.json`, and all three specs, inspected
the disposal commits (`e8bf2b644`, `7657d71ed`, `98256b33c`), and checked the
fixes against the current lease/access code paths.

## Verdict: FAIL

One must-fix problem remains in the plan. The three previously reported
must-fixes were fixed correctly, but the re-review exposed a pre-existing
coverage gap that meets the must-fix bar.

## Must-fix findings

### MF-4 — Click acceptance never re-authorizes the prompt-owner Connection under its current access policy

The issue's Product Shape requires both Connections to be re-authorized at
click time, each under its own current lease and current authorization state:

> 并分别使用 prompt-owner Connection 与 selected Connection 各自当前的
> runtime lease、access policy、allowlist、owner/live-member/channel
> membership 重新授权。

This also feeds Acceptance Criteria #4 and #5: a current-policy denial must be
`unauthorized`, and an unauthorized result must not commit a winner or create
execution resources.

The corrected plan now does the selected-Connection half properly, but still
omits the prompt-owner policy half:

- `design.md` Decision 4 says the shared route performs operator auth,
  delivering-lease validation, and delivering-Connection lookup/disabled
  checks. Those checks prove the prompt-owner adapter lease, but they do not
  invoke `SlackConnectionAccessDecider` or re-evaluate the prompt-owner's
  current policy, allowlist, live-member status, or channel membership.
- Decision 4 steps 7–8 resolve and evaluate only the **chosen** Connection.
  Step 4 checks actor identity, but actor binding is not current policy
  authorization.
- Decision 8 explicitly passes no interaction lease context to selection
  handling and describes the delivering lease only as a shared route gate.
  It defines no prompt-owner access-decision step.
- `specs/slack-agent-selection-action/spec.md` specifies current-policy and
  own-lease evaluation only for the chosen Connection. It has no requirement
  or scenario for a prompt-owner policy/allowlist/member/channel change
  between render and click.
- T-003 likewise tests the delivering lease gate and selected-Connection
  authorization, but contains no prompt-owner current-policy re-authorization.

Concrete failure case: Connection A wins and posts the chooser while the
sender is allowed. Before the click, A's policy is narrowed, the sender is
removed from A's allowlist, or A can no longer verify the sender/channel.
Connection B remains valid and permits the sender. The planned pipeline passes
A's lease gate and actor check, authorizes under B, and commits B as winner,
even though the issue requires A's current authorization to deny the click as
`unauthorized`. That is a direct behavioral violation, not a speculative
hardening opportunity.

The plan must add prompt-owner re-authorization before winner commit using the
prompt-owner's own currently validated lease context and
`SlackConnectionAccessDecider`, separately from the selected Connection's own
lease/policy evaluation. A prompt-owner denial must return a visible
`unauthorized` result with no selection mutation, winner, provider inbox
entry, Session, Turn, or AgentJob. The specs and T-003 need deterministic
coverage for prompt-owner policy/allowlist/live-member/channel-membership
changes; when prompt owner and selected Connection are the same, one equivalent
current evaluation may satisfy both roles.

## Previous finding dispositions

### MF-1 — More than five candidates: FIXED

The artifacts now consistently implement the issue boundary:

- two to five eligible candidates render signed controls plus readable text;
- more than five render no interactive control, no truncation, no automatic
  choice, and no pagination;
- the once-only readable fallback requires an explicit single-Bot re-mention;
- readable summary/re-mention guidance is present for clients without
  interaction support.

This is stated in `proposal.md`, Design Decision 2 and its risk entry, the
prompt spec's dedicated candidate-count requirement/scenarios, and T-002's
description, acceptance criteria, and tests. No remaining contradictory
large-button-list path was found.

### MF-2 — Selected Connection used the prompt-owner lease: FIXED

The artifacts now resolve the selected Connection's own active lease at click
time from its connection target, re-prove it through
`ValidateRuntimeLeaseAsync`, build the selected Connection's
`SlackLeaseContext` from that lease, and return visible `unavailable` for a
missing/expired/superseded/invalid lease. They explicitly prohibit using the
delivering prompt-owner lease for selected-Connection authorization.

The action spec and T-003 include both the successful cross-Connection case and
the selected-lease-missing/selected-policy-denial cases. The proposed mechanism
matches the current per-target lease model (`ISlackLeaseStore.GetActiveAsync`,
`SlackAdapterLeaseService.ValidateRuntimeLeaseAsync`, and
`ResolveRuntimeLeaseBotTokenAsync`). This disposition does not cure MF-4:
validating the prompt-owner lease is not the same as re-evaluating the
prompt-owner access policy.

### MF-3 — Action lifetime and retention bounds: FIXED

The plan now uses the issue-pinned five-minute signed-action lifetime, settles
expired pending prompts without a new grace regime, and reaps finished records
under the existing `SlackProviderOptions.SlackEventRetentionWindow` (30-minute
default), with no new long-term audit archive. The old 24-hour action lifetime,
seven-day retention, and 24-hour grace are gone from the operative artifacts.

## Re-review checks

- **Every prior must-fix disposition:** checked; MF-1, MF-2, and MF-3 are fixed
  properly as described above.
- **Regressions introduced by those fixes:** checked; no must-fix regression was
  found in candidate rendering, selected-Connection lease handling, action
  expiry, retention, task ordering, or task/spec anchors.
- **Pre-existing problem missed previously:** FAIL due to MF-4. The earlier
  coverage/correctness verdict focused on the issue's explicit
  selected-Connection lease criterion and incorrectly treated the shared
  prompt-owner route lease gate as completing the other half of the Product
  Shape's “分别…重新授权” requirement. Re-reading the canonical issue and
  comparing that wording directly with `SlackInteractionRoutes` and
  `SlackConnectionAccessDecider` made the distinction clear: lease validation
  proves the adapter's authority, but does not re-evaluate the sender under the
  prompt-owner's mutable access policy. That is why the earlier per-dimension
  sweep did not catch this pre-existing omission.

## Observations

1. The action spec still says the signed payload binds the chooser message
   identity, while Design Decision 3 signs the original message identity and
   enforces chooser-message identity through the acked outbox provider
   identity. The design gives an implementable authoritative mechanism and
   T-003 follows it, so this remains a wording consistency observation rather
   than a must-fix issue.
2. The issue groups failures into `unavailable`, `unauthorized`, and `stale`,
   while the plan keeps finer existing outcomes such as `expired`,
   `invalid_action`, `no_longer_valid`, `connection_disabled`, and setup nudge.
   Design Decision 4 now maps those outcomes explicitly enough to verify the
   required behavior.
3. The additive migration still needs careful sentinel handling for old
   pre-fact ambiguity rows: “non-nullable facts” and “no backfill” do not alone
   prove that an old row cannot enter selection execution. T-002 requires such
   rows to start no execution, which is sufficient plan coverage, but the
   implementation must make that guard structural and test it.

<promise>FAIL</promise>