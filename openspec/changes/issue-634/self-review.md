# Self-Review — Issue 634 plan (re-review)

Reviewer: pi. This is a disposition re-review, not a second full sweep. I first
re-read the canonical issue with:

```bash
mo issue view 634 --project proj_f6c141d63b6243bfbb481737b2243b87 --json number,title,body,comments,attachments,feedback,updatedAt
```

I then verified the previous review's findings against the current
`proposal.md`, `design.md`, `tasks.json`, and all three specs, inspected the
MF-4 disposal changes, and traced the resulting design through the current
workspace-wide candidate, project-scoped Connection, lease, and launch paths.

## Verdict: FAIL

The four previously reported must-fix findings are now disposed correctly, but
one pre-existing must-fix problem remains: the plan cannot select a candidate
Connection belonging to a different Mohist Project from the prompt owner, even
though the current ambiguity domain is workspace-wide and includes such
Connections.

## Must-fix findings

### MF-5 — Candidate identity loses Project ownership, so cross-Project candidates cannot be selected

The issue does not limit a multi-Bot chooser to Connections in one Project. Its
Acceptance Criteria #2 requires an authorized choice to start the **selected
Bot**, #3 requires the selected Connection to use **its own** current lease and
access policy, #8 requires the original root/thread provenance to route to that
selected Connection, and #9 requires deterministic verification of
cross-Connection authorization.

The current codebase deliberately discovers ambiguity across the whole Slack
workspace, not just the inbound Connection's Project:

- `AgentConnectionStore.ListBoundBotsByWorkspaceAsync` filters by
  `WorkspaceTeamId` without filtering `ProjectId` and returns each
  `WorkspaceBoundBot.ProjectId` (`packages/server/src/Mohist.Server/Agent/Services/AgentConnectionStore.cs:131-151`).
- Channel ingress passes that workspace-wide set into
  `SlackMultiAgentRoutingPolicy` (`SlackConnectionRoutes.ChannelIngress.cs:119-135`).
- Project isolation is otherwise explicit: `AgentConnectionStore.GetAsync`
  resolves a Connection only when both `projectId` and `id` match
  (`AgentConnectionStore.cs:58-67`), and runtime lease targets are keyed by
  both Project and Connection.

The plan drops the Project half of candidate identity at every durable and
action boundary:

- Design Decision 2 derives candidates from `MentionedWorkspaceBots`, but
  Decision 3 signs only an ordered **connection-id** set and a
  `ChosenConnectionId` (`design.md:150-165`).
- The claim stores only `MentionedConnectionIdsJson`, not durable candidate
  Project/Connection references.
- Decision 4 step 8 resolves the chosen lease at
  `connection:{ProjectId}:{ChosenConnectionId}` (`design.md:231-235`), but the
  selection service is entered through the prompt-owner's project-scoped
  interaction route and the payload/snapshot contains no chosen Project id.
- T-002 and T-003 likewise specify only candidate Connection ids and never add
  a global-to-owning-Project resolution step or a durable selected Project id
  (`tasks.json:29`, `tasks.json:54-65`).

Concrete failure case: Bot A belongs to Project PA and Bot B belongs to Project
PB, both are active identity-bound Bots in the same Slack workspace. One
message mentions both. A wins the workspace-wide chooser claim, so the click
arrives through PA's interaction route. Choosing B leaves the planned service
with PA plus B's Connection id. The normal project-scoped Connection lookup
cannot resolve B, and the planned lease key becomes
`connection:PA:B` rather than B's actual `connection:PB:B`. The click therefore
returns a stale/unavailable-style rejection instead of starting B, even when B
has a valid lease and both policies authorize the actor.

That directly violates Acceptance Criteria #2, #3, and #8; it also leaves AC
#9's cross-Connection verification incomplete for a reachable codebase case.
This is not optional cross-Server coordination: both Connections are on the
same Mohist Server and are already placed in the same ambiguity set by the
current workspace-wide lookup.

The plan must preserve a complete candidate reference sufficient to recover
and authorize the chosen Connection in its owning Project — for example,
ordered `(ProjectId, ConnectionId)` references in the durable snapshot and
signed payload, with the chosen Project recorded at winner commit. Selected
Connection lookup, lease resolution, policy evaluation, admission, launch or
follow-up dispatch, pre-allocated identity, and recovery must all use that
selected Project. Specs and T-003 need deterministic coverage where prompt
owner and selected Connection belong to different Projects in the same Slack
workspace, including successful execution and selected-policy/lease rejection.
Constraining candidates to the prompt owner's Project would not satisfy the
issue or current workspace-wide routing behavior.

## Previous finding dispositions

### MF-1 — More than five candidates: FIXED

The proposal, Design Decision 2, prompt spec, and T-002 now consistently state:
2–5 candidates render signed controls with readable text; more than five
render no interactive control, no truncation, no automatic choice, and no
pagination, and require an explicit single-Bot re-mention. The readable text
also covers clients without interaction support.

### MF-2 — Selected Connection used the prompt-owner lease: FIXED within the represented candidate scope

The plan now resolves and validates the selected Connection's own active lease,
builds its own `SlackLeaseContext`, forbids substitution of the prompt-owner
lease, and returns visible `unavailable` for a missing or invalid selected
lease. The action spec and T-003 cover same-Project cross-Connection success and
failure. MF-5 is a separate identity/scope omission: the own-lease mechanism
cannot reach a selected Connection whose owning Project was discarded.

### MF-3 — Action lifetime and retention bounds: FIXED

The plan uses the issue-pinned five-minute action lifetime, settles expired
pending prompts without a new grace regime, and cleans finished records under
`SlackProviderOptions.SlackEventRetentionWindow`, with no long-term audit
archive.

### MF-4 — Prompt-owner current-policy re-authorization: FIXED

The pending-click pipeline now evaluates the prompt-owner through
`SlackConnectionAccessDecider` under its route-validated current lease before
candidate commit, separately from the selected Connection's own lease and
policy evaluation. The action spec and T-003 cover policy narrowing, allowlist
removal, live-member failure, channel-membership failure, and same-Connection
de-duplication, all with no winner or execution resources on denial.

## Re-review checks

- **Every prior must-fix disposition:** checked; MF-1 through MF-4 are fixed as
  described above.
- **Regressions introduced by the fixes:** checked; no must-fix regression was
  found in candidate-count handling, five-minute expiry/retention, either
  Connection's authorization order, same-Project lease handling, task order,
  or spec anchors.
- **Pre-existing problem missed earlier:** FAIL due to MF-5. The earlier
  codebase sweep verified that candidate discovery is workspace-scoped and
  that leases are per target, but it incorrectly treated Connection id alone
  as sufficient durable identity and did not trace the selected candidate's
  owning `ProjectId` from `WorkspaceBoundBot` through the signed payload,
  project-scoped interaction route, `AgentConnectionStore.GetAsync`, lease
  target, and launch/recovery calls. The MF-2/MF-4 rework made the singular
  `ProjectId` in the selected-lease key explicit, which exposed the omission.
  That explains why the previous per-dimension verdicts did not catch this
  reachable failure.

## Observations

1. The action spec still says the signed payload binds the chooser message
   identity, while Design Decision 3 signs the original message identity and
   enforces chooser identity through the acked outbox provider identity. The
   design provides an implementable mechanism, so this is wording consistency,
   not a must-fix issue.
2. The additive migration still needs explicit sentinel/default handling for
   pre-fact rows. “Non-nullable facts” and “no backfill” do not by themselves
   make an old row structurally incapable of selection execution, although
   T-002 does require that behavior and a deterministic test.
3. The plan equates render-time candidates with mentioned, enabled,
   identity-bound workspace Bots, while the issue uses the term “eligible” and
   separately requires current availability and authorization at click time.
   The click-time checks make the intended behavior plausible, but the exact
   render-time eligibility definition would benefit from being stated
   explicitly.
4. The concurrency scenario describing “two users” clicking different choices
   is slightly misleading because actor binding permits only the original
   sender. Same-actor double clicks, Slack redelivery, and adapter failover are
   the meaningful CAS race, and those are already covered.

<promise>FAIL</promise>