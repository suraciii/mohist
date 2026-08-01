# Self-Review: Issue 526 — Slack channel access policies

## Acceptance Criteria Coverage

All seven issue acceptance criteria are represented by a normative spec requirement and a deliverable
task, with test verification inside the task acceptance criteria:

| Issue criterion | Spec | Task |
|---|---|---|
| Owner selects Owner only / Allowlist / Anyone and manages Allowlist by recognizable member info | `connection-access-management` (select policies; manage by stable identity w/ recognizable presentation) | T-003 |
| DM is Owner-only under every policy | `channel-access-policy` (Direct messages remain Owner-only) | T-001, T-002 |
| Unauthorized call is rejected and creates no AgentJob/Session/Input | `channel-access-policy` (An unauthorized invocation creates no Agent resources) | T-001, T-002 |
| Anyone discloses it grants the Agent's configured execution authority | `connection-access-management` (Selecting anyone discloses…) | T-003 |
| Only Owner or the session's Slack initiator can cancel/stop | `channel-session-stop` (A channel-originated stop or cancel is permitted only…) | T-004 |
| Tightening rejects the next call immediately; accepted work not revoked | `channel-access-policy` (Policy changes take effect immediately…) | T-002 |
| Member leaving/invalid no longer authorized; no same-name auto-succession | `channel-access-policy` (Authorization never auto-succeeds by name; Allowlist … stable identity) | T-002 |

## Cross-Artifact Review

- All three capabilities named in `proposal.md` (`channel-access-policy`,
  `connection-access-management`, `channel-session-stop`) have a matching spec file. Every requirement
  has ≥1 `#### Scenario`, uses normative SHALL, and describes target behavior directly (no delta headers).
- The design's decisions map onto the tasks: D1 (storage) + D2 (decider, owner_only/DM) → T-001;
  D2 (allowlist/anyone) + D3 (live member/channel validation) → T-002; D4 (endpoint) + D6 (CLI/Web) →
  T-003; D5 (channel stop) → T-004.
- T-001–T-004 form a valid priority-ordered DAG. Dependencies point only to strictly-lower priorities;
  each task includes fake-Slack + injectable-time test coverage (no standalone test tasks); `npm test`,
  web typecheck/test commands are cited where relevant.
- Critical code references were spot-checked and hold: `AgentInitialLaunchSnapshot.Input.Provenance.MemberId`
  is reachable via `IAgentSessionGrain.GetInitialLaunchAsync()` (`IAgentSessionGrain.cs:104,360`);
  `SlackConversationInfo.IsMember` exists (`ISlackApiClient.cs:117`); `IsEligibleMember` is reusable
  (`SlackOwnerClaimService.cs:234`); no `AccessPolicy` field or `manage-access` endpoint exists today
  (greenfield, matching the proposal/design).
- Non-goals are preserved: the policy never alters Agent Runtime/Skills/repo/tools; no Slack Connect,
  group DM, fine-grained auth, or Slack-membership-as-Mohist-admin.

## Findings (observations, not blockers)

1. **Anyone intentionally excludes guests/restricted members.** The Anyone spec scenario rejects guests,
   via reuse of `IsEligibleMember` (which excludes bot/deleted/guest/restricted/other-team). The issue's
   "频道里任何人都行" could be read to include guests, who are technically workspace members. This is a
   safe, internally-consistent choice (invoking borrows the Agent's full write/tools authority; guests are
   limited-trust identities) and is consistent with how the codebase treats guests elsewhere, but it is a
   product interpretation worth confirming. It is a one-predicate swap within the existing design if product
   wants guests included, so it does not block the build.

2. **Anyone's `conversations.info` check is partly redundant and adds a false-rejection surface.** If the
   Bot received the event it is already a channel member, so `IsMember` is usually true; the extra call is
   defense-in-depth (shared-channel / Slack-Connect edge cases) at the cost of a second Slack API call per
   Anyone invocation and a safe-deny false negative if `conversations.info` lags. This is documented in
   design D3 and Risks; acceptable, flagged for awareness.

3. **The Anyone disclosure is client-enforced, not server-enforced.** The spec's "presented before the
   change takes effect" is satisfied at the Web/CLI layer; a direct API call bypasses it. This is an
   explicit design trade-off (design D4 + Open Questions) with the disclosure returned as a contract
   field. Confirm whether audit/compliance needs a server-side confirmation token (deferred).

4. **Channel-stop redelivery idempotency is not explicitly specified.** `channel-session-stop` does not
   state the outcome of a redelivered stop message (e.g. duplicate reply). The expected implementation
   inherits inbox/outbox dedup, and the "targets only the active Turn" requirement makes the *outcome*
   idempotent, but the build task should confirm duplicate replies are suppressed.

5. **Read-model `allowMembers` Owner inclusion is implicit.** The Owner is never stored (structural
   immovability, design D1), yet the Web panel should show the Owner as present. Whether the read model
   synthesizes the Owner into the returned `allowMembers` for display is not explicit; minor, resolvable
   in T-003.

None of the above is a coverage gap or an internal contradiction. Each is a documented design choice or a
clarification already captured in the design's Open Questions, reversible within the existing structure.

## Verdict

All acceptance criteria are covered by testable specs and deliverable tasks; the design is internally
consistent with verified code references; the task graph is a valid DAG with embedded test coverage. The
plan is ready to build.

<promise>PASS</promise>
