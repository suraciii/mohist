# Review: Issue 526 — Slack channel access policies (re-review after fixes)

## Verdict

**PASS.** The previous review failed because the CLI and Web halves of T-003 were
entirely missing. Both are now implemented, tested, and the full suite is green.
All four tasks (T-001 storage + decider, T-002 allowlist/anyone live validation,
T-003 manage-access across server/CLI/Web, T-004 channel turn control) are complete
and meet their acceptance criteria. The two observations from the prior review (O1
Owner display, O4 misleading variable name) are resolved.

## Scope of change reviewed

Server: `AgentConnection.AccessPolicy` + `AccessPolicyKind`, allowlist child store +
cascade registration, `SlackConnectionAccessDecider` (single decision point, no cache),
`SlackConnectionAccessManager` (validate-then-replace), `SlackMemberSearchService`,
the channel ingress routing through the decider at all five sites + the DM gate,
channel turn-control dispatch, the `POST /{id}/manage-access` + `GET /{id}/members`
+ `GET /{id}/access` endpoints, and the EF migration/snapshot.

CLI: `connection edit` `--access-policy` / repeatable `--allow-member` / `--yes`,
routed to manage-access with the Anyone disclosure + confirmation gate +
incompatibility/invalid-policy client-side rejection.

Web: `AccessPolicySection` (policy selector, allowlist editor with debounced member
search → stable ids, removable chips, always-present non-removable Owner chip, Anyone
disclosure confirmation), wired into `ConnectionDiagnosticPage` behind setup completion.

## Verification performed

- `npm run build` succeeds.
- `npm test`: Workflow.Definition 175, Cli.Tests 1504, UnitTests 1723, ArchTests 51,
  SpecTests 3672, Web 5154, Runner 1510, mohist-slack 8 — **0 failures**.
- Web `typecheck` and `check:fsd` (506 modules) + `check:test-boundaries` pass.
- Working tree clean after verification (no Git-visible side effects).

## Acceptance-criteria coverage

T-003 (the previously-missing layer) is now satisfied end-to-end:

- **CLI flags** (`MohistCliCommands.AgentConnection.cs:347`): `--access-policy` +
  repeatable `--allow-member` POST to `/{id}/manage-access`; `--allow-member` with
  `owner_only`/`anyone` and unknown policies are rejected before any HTTP
  (`ManageAccessAsync:419-426`); `anyone` renders the disclosure and gates behind
  `--yes` in non-interactive mode (`:428-451`). Presentation flags still PATCH
  separately and may be combined. Covered by 7 Cli.Tests.
- **Web panel** (`access-policy-section.tsx`): renders current policy, an allowlist
  editor backed by `GET /{id}/members` search resolving to **stable Slack ids**
  (`addMember` stores `member.slackUserId`, never the display name), removable chips,
  an always-present non-removable Owner chip synthesized from
  `connection.ownerSlackUserId`, and the Anyone disclosure with a required
  confirmation checkbox (`canSubmit` blocks until `confirmedAnyone`). Display
  name/avatar are presentation only. Covered by 6 unit + 1 MSW integration test.
- **E2E** (`SlackAccessPolicyT003Specs` + `SlackAccessPolicyT002Specs`): setting an
  allowlist via the endpoint accepts a listed member's next invocation; removing the
  member rejects the next input. The cross-layer path (CLI → endpoint → invocation)
  is now exercisable.

The server-side T-001/T-002/T-004 work was reviewed previously and remains sound:
single no-cache decision point, safe-deny on Slack API failure, Owner short-circuit,
structural Owner-never-stored invariant, channel turn-control authority by Owner or
session initiator, immediate policy effect.

## Observations (non-blocking)

1. **Manage-access / access endpoints have no Slack-owner bearer.** They are
   project-resolved control-plane routes (consistent with every other management
   route). This is the documented design trade-off (design D4 Open Questions); a
   server-side confirmation token for `anyone` is deferred. Not a defect.

2. **Anyone disclosure is client-enforced.** The disclosure text is returned as a
   contract field and rendered/gated by the CLI (`--yes`) and Web (confirmation
   checkbox); a direct API call bypasses it. Documented choice (design D4 + Risks).

3. **Member-search debounce timer is not cleared on unmount.** A pending debounced
   `searchMembers` could resolve after the section unmounts; React 18+ silently
   ignores the late state update (no crash, no warning). Benign; could clear the
   timer in the unmount cleanup for tidiness.

None of the above is a defect or an acceptance-criteria gap.

<promise>PASS</promise>
