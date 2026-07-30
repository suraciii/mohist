# Self-Review: Issue 517 (Round 3)

## Review Summary

Re-reviewed all plan artifacts after round-2 fixes (avatar drift comparison mechanism
corrected to `VerifiedBotIconUrl` vs `AvatarHash`, migration plan steps completed). All
blocking and non-blocking issues from rounds 1 and 2 are confirmed resolved. No new
problems found.

---

## Previous-Round Fixes Confirmed

### Round 1 (all resolved)

1. **Avatar drift in scope** — spec, design D7, and tasks T-004 include `VerifiedBotIconUrl`
   and `SlackBotInfo.IconUrl`; deferral removed from risks and open questions. ✅
2. **Diagnostic precedence split** — D6 table (lines 107-108) distinguishes credential-failure
   Unhealthy (priority 2) from service-unreachable Unhealthy (priority 3) via `HealthReason`
   content, with explanatory paragraph (line 115). ✅
3. **configure guard reconciled** — proposal line 7 matches design decision (guard +
   redirect); Open Question removed. ✅
4. **FixSlackSetup edge case** — D1 (lines 42, 48) uses `HasBoundIdentity` check instead of
   SetupProgress values; T-001 acceptance criteria include FixSlackSetup rotation. ✅

### Round 2 (all resolved)

1. **Avatar drift comparison mechanism** — D7 line 133 now defines avatar drift as
   `VerifiedBotIconUrl ≠ AvatarHash` (same two-source pattern as `VerifiedBotName` vs
   `BotName`). The old "latest vs previous verification" comparison (impossible with one
   field) is gone. Spec scenario (line 62) says "icon URL captured at verification differs
   from the Connection's recorded AvatarHash." Tasks T-004 (lines 77, 83, 94) consistently
   use `VerifiedBotIconUrl ≠ AvatarHash`. Risk entry (line 168) updated. ✅
2. **Migration plan completeness** — step 1 (line 172) mentions both `VerifiedBotName` and
   `VerifiedBotIconUrl` plus `SlackBotInfo.IconUrl`; step 5 (line 176) mentions both. ✅

---

## Coverage Check (all pass)

| Issue Acceptance Criterion | Spec | Design | Task |
|---|---|---|---|
| Web & CLI diagnostics for 6 states, one next step | `connection-diagnostics` | D6 | T-004, T-005, T-006 |
| Credential rotation + re-verify + reject rebinding | `connection-credential-rotation` | D1 | T-001 |
| Owner transfer, old effective until new claims, no auto-transfer | `connection-owner-transfer` | D2 | T-002 |
| Disable stops input/replies, preserves work; Enable no replay | `connection-lifecycle-control` | D4 | T-003 |
| Delete clears provider records, preserves Agent/work, honest about Slack App | `connection-lifecycle-control` | D5 | T-003 |
| Identity drift (name + avatar) shown honestly, no auto-rewrite | `connection-diagnostics` | D7 | T-004 |

## Consistency Checks (all pass)

- Proposal capabilities (4) ↔ spec directories (4): match.
- Task spec references ↔ spec requirement headings: all 6 valid.
- Task dependency graph: valid DAG, all deps strictly lower priority, no cycles.
- Non-goals aligned across issue, proposal, design, specs.
- Spec formatting: all requirements use `###`, all scenarios use `####`, every requirement
  has ≥1 scenario, normative SHALL/MUST language throughout, no delta headers.
- Avatar drift comparison is consistent across spec (line 62: "icon URL … differs from the
  Connection's recorded AvatarHash"), design D7 (line 133: "VerifiedBotIconUrl ≠ AvatarHash"),
  and tasks T-004 (line 83: "VerifiedBotIconUrl ≠ AvatarHash"). All use the same two-source
  pattern as name drift.
- configure guard is consistent across proposal (line 7), design D1 (lines 42, 48), and task
  T-001 (lines 9, 12, 16): all use `HasBoundIdentity` check, not SetupProgress values.
- Diagnostic precedence table (D6) correctly splits Unhealthy by HealthReason content;
  design body and risk entry are consistent.

<promise>PASS</promise>
