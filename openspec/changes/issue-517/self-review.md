# Self-Review: Issue 517

## Review Summary

Reviewed all plan artifacts (`proposal.md`, `design.md`, `tasks.json`, `specs/`) against
issue 517's acceptance criteria, domain model, and non-goals. Also cross-checked design
claims against source code (`SlackSetupVerifier.cs`, `ISlackApiClient.cs`,
`AgentConnectionStore.cs`, `SlackOwnerClaimService.cs`, `SlackConnectionRoutes.cs`).

Overall the plan is strong: the four capabilities map cleanly to the six acceptance criteria,
the design decisions are well-justified with alternatives, the task graph is a valid DAG,
and specs use correct normative formatting. However, one spec-design contract contradiction
must be resolved before building.

---

## Blocking Problems

### 1. Avatar drift: spec requires it, design defers it (spec-design contract broken)

The issue's acceptance criterion explicitly includes avatar drift:

> Slack App 名称、**头像** 与 Agent 名称漂移时如实显示差异，不自动改写 Slack 侧。

The `connection-diagnostics` spec captures this as a SHALL requirement with a dedicated
scenario:

> **Requirement:** Identity drift is detected and shown honestly without auto-rewrite
> — "The diagnostic SHALL detect when the Slack-side App or Bot name **or avatar** differs..."
>
> **Scenario: Avatar drift surfaced** — "WHEN the Slack-side Bot avatar hash differs from
> the Connection's recorded AvatarHash THEN the diagnostic surfaces the avatar drift..."

But design D7 explicitly defers avatar drift:

> "Avatar drift is deferred: Slack's `bots.info` returns icon URLs, not a stable hash, so
> a meaningful comparison requires fetching and hashing the image — out of scope for this
> issue."

And design Open Questions lists it as unresolved:

> "Avatar drift (D7): ...defer avatar drift to a follow-up and ship name-only drift in this issue."

I verified the technical constraint: `SlackBotInfo` (`ISlackApiClient.cs:61`) carries only
`Id`, `Name`, `AppId` — no avatar/icon field. So the design's deferral rationale is sound.
But the spec asserts avatar drift as a non-negotiable SHALL, which the design does not
deliver. An implementer following the design would fail the spec; an implementer following
the spec would need to solve a problem the design says is out of scope.

**Fix:** Either (a) update the spec to scope avatar drift to a follow-up issue (e.g., add a
note that avatar drift is deferred and name-only drift ships in this issue, matching the
design), or (b) if avatar drift must ship now, update the design to include an approach
(e.g., extend `SlackBotInfo` to capture the icon URL from `bots.info` and compare URLs, or
fetch+hash the image) and add the corresponding task coverage.

---

## Non-Blocking Notes

### 2. Diagnostic precedence table conflates Unhealthy health reasons

Design D6's precedence table maps `ConnectionHealth == Unhealthy` uniformly to "Credentials
invalid → Rotate credentials" (priority 2). But `SlackSetupVerifier.FailAsync` (line 83-87)
sets `Unhealthy` with reasons that include both credential failures ("Slack rejected the Bot
token") AND service/network failures ("Slack could not be reached. Start mohist-slack and
retry verification."). The latter should not produce a "Rotate credentials" next action —
it is a service issue, not a credential issue. The implementer should refine priority 2 to
check the `HealthReason` content (or introduce a sub-condition) so that service-unreachable
Unhealthy maps to a service next action rather than credential rotation.

### 3. `configure` guard strictness is an open question (acknowledged)

Design Open Question #3 asks whether refusing `configure` on already-bound connections
(redirecting to `rotate-credentials`) is acceptable. The proposal's What Changes says
"configure 对已验证 Connection 执行轮换语义" (configure performs rotation semantics),
while the design chose a separate route with a configure guard. The proposal's Impact
section offered the choice ("新增 rotate-credentials 或扩展 configure 语义"), so this is
within scope, but the divergence between the proposal's What Changes wording and the design
decision should be reconciled when the open question is resolved.

### 4. Edge case: FixSlackSetup with bound identity

A connection that was once `Complete` can regress to `FixSlackSetup` when credentials expire
(`VerifyAsync` runs on every heartbeat and calls `FailAsync` on failure). In this state,
identity is still bound but `SetupProgress` is `FixSlackSetup`, which is NOT in the
configure guard's blocklist (`{ClaimOwner, Complete}`). So `configure` would be accepted,
silently overwriting tokens without synchronous verification. The async `VerifyAsync` on the
next heartbeat would catch identity mismatches via `BindSlackIdentityAsync`'s
`immutable_binding` guard, so it is safe — just not synchronously verified. The design could
clarify that `FixSlackSetup` with bound identity is an expected configure path (fix broken
creds), or that the guard should check bound-identity state rather than SetupProgress values.

---

## Coverage Check (all pass)

| Issue Acceptance Criterion | Spec | Design | Task |
|---|---|---|---|
| Web & CLI diagnostics for 6 states, one next step | `connection-diagnostics` | D6 | T-004, T-005, T-006 |
| Credential rotation + re-verify + reject rebinding | `connection-credential-rotation` | D1 | T-001 |
| Owner transfer, old effective until new claims, no auto-transfer | `connection-owner-transfer` | D2 | T-002 |
| Disable stops input/replies, preserves work; Enable no replay | `connection-lifecycle-control` | D4 | T-003 |
| Delete clears provider records, preserves Agent/work, honest about Slack App | `connection-lifecycle-control` | D5 | T-003 |
| Identity drift (name + avatar) shown honestly, no auto-rewrite | `connection-diagnostics` | D7 (avatar deferred) | T-004 |

## Consistency Checks (all pass except avatar drift above)

- Proposal capabilities (4) ↔ spec directories (4): match.
- Task spec references ↔ spec requirement headings: all 6 valid.
- Task dependency graph: valid DAG, all deps strictly lower priority, no cycles.
- Non-goals aligned across issue, proposal, design, specs.
- Spec formatting: all requirements use `###`, all scenarios use `####`, every requirement
  has ≥1 scenario, normative SHALL/MUST language throughout, no delta headers.
- Design claims verified against source: `SlackBotInfo` lacks avatar field (confirms D7
  constraint); `FailAsync` sets Unhealthy for both credential and service reasons (confirms
  note #2); `ListForAdapterAsync` already filters on Enabled (confirms D4); `GenerateAsync`
  rejects existing owner (confirms D2 gap); `TryClaimAsync` uses `WHERE OwnerSlackUserId ==
  null` (confirms D2 atomic-swap approach).

<promise>FAIL</promise>
