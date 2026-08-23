# Review — Issue 634 (re-review)

## Verdict: FAIL

## Must-fix findings

### MF-1 — The `ThreadLaunch`/`Bound` recovery fix can complete the operation without delivering the original message

**Violated criteria:** Issue AC 6 (one committed winner starts one execution), AC 7 (post-commit restart resumes the same operation without duplicate or lost execution), and the execution-attribution requirements that the committed dispatch kind and pre-allocated execution identity remain the durable authority.

When a committed `ThreadLaunch` encounters a newly bound Session, `SlackAgentSelectionService.DispatchAsync` changes the dispatch to a follow-up and computes a new `ThreadFollowup` identity from that bound Session (`packages/server/src/Mohist.Server/Slack/Services/SlackAgentSelectionService.cs:403-420`). Those Session/Input/Turn values and the effective follow-up dispatch are not persisted back to the selection decision; the claim still records `ThreadLaunch` and its original launch ids.

More importantly, `DispatchFollowupAsync` creates the provider-inbox row before calling `AcceptFollowupAsync` (`SlackAgentSelectionService.cs:465-487`). If the process fails after `AcceptAsync` but before the Session accepts the follow-up, recovery re-enters the committed `ThreadLaunch` path, sees the inbox row's non-null route through `FindMessageRouteSessionIdAsync`, returns `already_accepted`, and lets the worker mark the selection completed (`SlackAgentSelectionService.cs:359-371`; `SlackAgentSelectionObligationWorker.cs:124-129`). The resulting operation has a winner and a completed inbox/selection record but no SessionInput/Turn for the retained original message.

The bound-race path must preserve a durable execution authority and recovery must idempotently prove or perform Session acceptance; existence of a routed inbox row alone is not proof that the message reached the Session. Add a deterministic crash-window test for failure between inbox acceptance and `AcceptFollowupAsync`.

### MF-2 — The prior deterministic-verification finding is only partially disposed

**Violated criteria:** Issue AC 9 (deterministic verification of migration, authorization rejection, restart/recovery, and retention cleanup), issue AC 8 (root, existing-thread Session, and new-thread launch routing), and the explicit T-002/T-003/T-004 test acceptance criteria in `openspec/changes/issue-634/tasks.json`.

The expanded `SlackMultiAgentIngressSpecs` now verifies a cross-Project root selection, a concurrent CAS race, several rejection cases, transient recovery, and one artificial `ThreadLaunch`/`Bound` recovery race. However, the required matrix still has material holes:

- No test applies `20260913000000_AddSlackSelectionFacts` to a pre-change database containing an existing `SlackAmbiguousPrompts` row. The store tests use `TestSqliteDatabase.CreateModelSchema()` and therefore do not verify the additive migration, its legacy defaults, or that a migrated pre-fact row cannot execute and is cleaned up safely. No test references `AddSlackSelectionFacts` or its migration id.
- There is no successful live selection-route test for an already-bound `ThreadFollowup`, an unbound existing-thread `ThreadLaunch`, or an unmentioned multi-bound-thread follow-up. The only accepted live route test is the channel-root case; the bound-race test manually commits a claim and bypasses click-time classification. Retained attachment routing is likewise not exercised.
- The plan-required current-policy matrix is not covered: the new policy test changes `OwnerSlackUserId`, but does not deterministically exercise allowlist removal, live-member loss/unverifiable identity, or channel-membership loss/unverifiable conversation for the prompt owner and selected Connection.
- Cleanup is tested directly at the store boundary, but there is no worker-level deterministic test proving that only `Completed`/`Settled` rows beyond `SlackEventRetentionWindow` are reaped while `Pending`/`Decided` rows survive.

These were part of the previous MF-6 finding and are not covered by the stated disposition. Add the missing migration and end-to-end/fake-port scenarios required by the issue and task acceptance criteria.

## Previous finding dispositions

- **Previous MF-1 — duplicate expiry messages for non-interactive claims:** fixed. Expiry detects whether the durable prompt payload actually contains `mohist_select_agent`; non-interactive and legacy claims settle without an added Slack outcome, and ingress no longer re-enqueues a settled claim.
- **Previous MF-2 — follow-up executability not revalidated:** fixed. Agent lookup and admission now run for every dispatch kind before the decision CAS.
- **Previous MF-3 — dangling follow-up Session discovered after commit:** fixed. The live path resolves the canonical Session and verifies its selected Agent/Connection before CAS; recovery validates the committed target as well.
- **Previous MF-4 — `Bound` result silently treated as success:** not safely fixed. The message is now redirected to a follow-up, but the new crash window and non-persisted effective lineage are MF-1 above.
- **Previous MF-5 — transient exceptions settled after three attempts:** fixed. Exceptions leave the operation `Decided`; only an explicit terminal recovery result settles it.
- **Previous MF-6 — deterministic acceptance matrix missing:** partially fixed, but still fails for the cases in MF-2 above.

## Re-review checks

- **Issue acceptance criteria re-read:** checked before reviewing the follow-up diff.
- **Every previous finding:** checked; dispositions are recorded above.
- **Regression check:** FAIL — MF-1 is a regression introduced by the `Bound`-race fix.
- **Coverage:** FAIL — MF-2 leaves issue- and plan-required scenarios unverified.
- **Correctness:** FAIL — MF-1 can terminally complete a committed selection without creating the selected message's execution input.
- **Consistency with surrounding codebase:** checked. The new follow-up target validation and exception retry semantics follow existing boundaries; MF-1 is specifically inconsistent with the provider inbox's meaning, where insertion precedes actual Session dispatch.
- **Tests:** FAIL on required coverage, despite the available suite being green. `dotnet test Mohist.Server.Tests.slnf --no-restore -p:SkipWebBuild=true` passed (Workflow Definition 178, Orleans 2, Arch 53, Unit 3799, Spec 3073), and `git diff --check` passed. Go tests could not run because `go` is unavailable in this workspace.

## Observations

None beyond the must-fix findings.

<promise>FAIL</promise>