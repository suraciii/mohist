# Self-Review: Issue 517 (Round 2)

## Review Summary

Re-reviewed all plan artifacts after the round-1 fixes (avatar drift brought into scope,
precedence table split, configure guard reconciled, FixSlackSetup edge case resolved). The
previous four issues are confirmed fixed. This round found one new blocking problem
introduced by the avatar-drift fix: the comparison mechanism is not implementable as
described.

---

## Blocking Problems

### 1. Avatar drift comparison requires two snapshots but the design stores only one

Design D7 (line 133) defines avatar drift as:

> "the `VerifiedBotIconUrl` captured at the latest verification differs from the
> `VerifiedBotIconUrl` stored from the **previous** verification"

But D7 (line 127) also says verification **overwrites** `VerifiedBotIconUrl` on every
successful verification:

> "`VerifyAsync` and `VerifyRotationAsync` capture both `VerifiedBotName` … and
> `VerifiedBotIconUrl` … **on every successful verification**."

After the latest verification overwrites the field, the previous value is gone. The
diagnostic endpoint runs separately from verification (D6: it loads the connection and
probes owner availability; it does not re-run verification or call `bots.info` live — D7
rejected that). So when the diagnostic runs, it sees only the single current
`VerifiedBotIconUrl` with no previous value to compare against.

The name drift works because it compares **two different fields** from two different
sources: `VerifiedBotName` (Slack-side observation) vs `BotName` (operator-configured
value). Avatar drift has no such pair — the design stores only one Slack-side observation
(`VerifiedBotIconUrl`) and explicitly rejects comparing it against the operator-set
`AvatarHash` (line 137):

> "AvatarHash on the Connection is an operator-provided presentation field with no
> guaranteed relationship to Slack's icon URL format, so it is not a reliable comparison
> target."

The spec scenario (line 62) mirrors the same gap:

> "WHEN the Slack-side Bot icon URL captured at the latest verification differs from the
> icon URL recorded at the **previous** verification"

This also requires two snapshots. An implementer following these artifacts would have no
way to detect avatar drift at diagnostic time.

**Fix (recommended):** Align avatar drift with the name-drift pattern — compare
`VerifiedBotIconUrl` (what Slack reports) against `AvatarHash` (what the operator recorded
on the Connection). This uses two fields from two different sources, exactly like
`VerifiedBotName` vs `BotName`. The format difference (URL vs hash) is itself the drift
signal: the diagnostic shows both values honestly and lets the operator decide. Update:
- D7 line 133: change the avatar drift bullet to "`VerifiedBotIconUrl` ≠ `AvatarHash`"
- D7 line 137: remove the rejection of the `AvatarHash` comparison; instead explain that
  the comparison surfaces the difference between what Slack shows and what the operator
  recorded, same pattern as name drift
- Spec line 62: change the scenario to "the Slack-side Bot icon URL differs from the
  Connection's recorded AvatarHash"
- Alternatively, add a second field (`PreviousVerifiedBotIconUrl`) — but this is more
  model complexity for the same outcome.

---

## Non-Blocking Notes

### 2. Migration plan steps omit VerifiedBotIconUrl

Design migration plan step 1 (line 172) says "add `VerifiedBotName` to
`AgentConnectionRow`" and step 5 (line 176) says "`VerifiedBotName` capture in
`VerifyAsync`" — both omit `VerifiedBotIconUrl` and `SlackBotInfo.IconUrl`. The decision
body (D7), tasks (T-004), and risks section all correctly mention both. The migration plan
steps should be updated for consistency.

### 3. Proposal What Changes still mentions AvatarHash comparison target

Proposal line 12 says "Slack 侧返回的 App 名称/头像与 Connection 记录的 BotName/AvatarHash"
— this already references `AvatarHash` as the comparison target for avatar, which aligns
with the recommended fix above (comparing `VerifiedBotIconUrl` vs `AvatarHash`). No change
needed if the fix follows the recommendation; noting for traceability.

---

## Previous-Round Fixes Confirmed

All four round-1 issues are verified resolved:

1. **Avatar drift in scope** — spec, design D7, and tasks T-004 now include
   `VerifiedBotIconUrl` and `SlackBotInfo.IconUrl`; deferral removed from risks and open
   questions. ✅ (mechanism gap is a new issue — see blocking #1 above)
2. **Diagnostic precedence split** — D6 table (lines 107-108) distinguishes
   credential-failure Unhealthy (priority 2) from service-unreachable Unhealthy (priority 3)
   via `HealthReason` content, with explanatory paragraph (line 115). ✅
3. **configure guard reconciled** — proposal line 7 matches design decision (guard +
   redirect, not inline rotation); Open Question removed. ✅
4. **FixSlackSetup edge case** — D1 (lines 42, 48) uses `HasBoundIdentity` check instead of
   SetupProgress values; T-001 acceptance criteria include FixSlackSetup rotation. ✅

---

## Coverage Check (all pass)

| Issue Acceptance Criterion | Spec | Design | Task |
|---|---|---|---|
| Web & CLI diagnostics for 6 states, one next step | `connection-diagnostics` | D6 | T-004, T-005, T-006 |
| Credential rotation + re-verify + reject rebinding | `connection-credential-rotation` | D1 | T-001 |
| Owner transfer, old effective until new claims, no auto-transfer | `connection-owner-transfer` | D2 | T-002 |
| Disable stops input/replies, preserves work; Enable no replay | `connection-lifecycle-control` | D4 | T-003 |
| Delete clears provider records, preserves Agent/work, honest about Slack App | `connection-lifecycle-control` | D5 | T-003 |
| Identity drift (name + avatar) shown honestly, no auto-rewrite | `connection-diagnostics` | D7 (mechanism gap) | T-004 |

## Consistency Checks

- Proposal capabilities (4) ↔ spec directories (4): match.
- Task spec references ↔ spec requirement headings: all 6 valid.
- Task dependency graph: valid DAG, all deps strictly lower priority, no cycles.
- Non-goals aligned across issue, proposal, design, specs.
- Spec formatting: all requirements use `###`, all scenarios use `####`, every requirement
  has ≥1 scenario, normative SHALL/MUST language throughout, no delta headers.

<promise>FAIL</promise>
