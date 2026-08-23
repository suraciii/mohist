# Review — Issue 634

## Verdict: FAIL

## Must-fix findings

### MF-1 — Non-interactive ambiguity claims produce a second Slack message after five minutes

**Violated criteria:** Issue AC 1 (the >5 case posts one readable fallback and no interactive chooser); prompt spec “More than five candidates render the text fallback” (`exactly one readable text fallback message`); prompt spec “Unauthorized senders keep the existing owner-only guidance” (`posted once, unchanged`).

`HandleAmbiguousPromptAsync` always leaves the durable claim in `Pending`, including when `SlackSelectionChooserRenderer.BuildBlocksAsync` returns `null` for more than five candidates or when signing/interaction presentation is unavailable (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.ChannelIngress.cs:418-443`). `HandleAmbiguousNonOwnerAsync` likewise creates a `Pending` claim for the owner-only guidance. The obligation worker expires every `Pending` claim and enqueues a separate settlement message (`packages/server/src/Mohist.Server/Slack/Services/SlackAgentSelectionObligationWorker.cs:74-99`). Consequently:

- a >5 fallback is followed five minutes later by a second “selection expired” message;
- the unchanged once-only non-owner guidance is followed by the same extra message;
- migrated legacy claims are also eligible for this unsolicited expiry outcome.

Non-interactive claims need a durable presentation/state distinction, or the worker must otherwise avoid emitting selection-expiry outcomes for claims that never offered a clickable selection.

### MF-2 — Thread-followup choices skip required executability revalidation before committing the winner

**Violated criteria:** Issue AC 4/5 (current unavailability/staleness is rejected visibly and creates no winner or execution resources); action spec “The chosen candidate's executability is revalidated before work starts”; action spec scenario “The Agent is not executable at click time”.

`SlackAgentSelectionService.HandleAsync` calls `AgentQuerier` and `SlackAdmissionService.AdmitNewWorkAsync` only for `RootLaunch` and `ThreadLaunch` (`packages/server/src/Mohist.Server/Slack/Services/SlackAgentSelectionService.cs:205-223`). A `ThreadFollowup` proceeds directly to the Pending→Decided CAS and follow-up dispatch. If the chosen Agent becomes not configured/not executable while an old thread binding remains, the click can commit a winner and create a provider-inbox/follow-up lineage instead of returning the required setup-nudge/resource-free rejection.

The current-Agent/current-executability check must cover follow-up selections as well as launch selections before `TryDecideAsync`.

### MF-3 — A stale follow-up Session can commit a winner before being discovered

**Violated criteria:** Issue AC 4 (candidate/context changes return stale), AC 5 (stale creates no winner, Session/Turn/AgentJob, or provider inbox entry), and the action spec requirement that a vanished required follow-up target is `no_longer_valid` before commit.

For thread follow-up classification, the service verifies only that a mapping returns a Session id (`SlackAgentSelectionService.cs:176-197`). It does not verify that the mapped Session still exists and is a valid target before `TryDecideAsync` (`:229-239`). The first actual Session access occurs after the winner is committed and after attachment/provider-inbox work begins (`:369-434`). A dangling mapping can therefore produce a durable winner and provider-inbox side effects where the required result is a pre-commit stale/no-longer-valid rejection with no resources.

Validate the required selected Session through the repository’s Session boundary before the CAS, and keep failures on the resource-free rejection side of the fence.

### MF-4 — A committed `ThreadLaunch` can be marked completed without starting the selected message

**Violated criteria:** Issue AC 2 (an authorized selection starts only the selected Bot on the original message), AC 6 (one winner starts one execution), and AC 8 (new-thread launch preserves and routes from the original provenance).

`SlackChannelLaunchService` returns `Bound` without dispatching the current message when another launch binds the selected Connection to the thread between click-time classification and dispatch (`packages/server/src/Mohist.Server/Slack/Services/SlackChannelLaunchService.cs:125-139`). `SlackAgentSelectionService.DispatchAsync` ignores `BoundSessionId` and treats every otherwise-unrecognized launch result as `accepted` (`packages/server/src/Mohist.Server/Slack/Services/SlackAgentSelectionService.cs:334-366`); the caller then marks the selection `Completed` (`:251-254`). In that race, the selected original message is neither launched with its preallocated lineage nor sent as a follow-up—it is silently dropped while the chooser reports success.

The committed dispatch must not be completed unless the original retained message was idempotently accepted into the committed lineage. A post-commit bound result must be handled explicitly rather than falling through to success.

### MF-5 — Recovery terminally settles ordinary transient failures after three attempts

**Violated criterion:** Issue AC 7 (after winner commit, restart recovers the same operation without duplicate execution; a committed selection is resumed or settled only when it cannot produce its execution).

The recovery worker treats every exception identically and changes the operation to `Settled` after three attempts (`packages/server/src/Mohist.Server/Slack/Services/SlackAgentSelectionObligationWorker.cs:162-188`). Three temporary database, Orleans, adapter, or infrastructure failures do not prove that the committed lineage is irrecoverable. This can permanently suppress a valid committed selection after a brief outage, contrary to the durable recovery requirement.

Terminal settlement needs an explicit irrecoverable classification (deleted selected Connection/Agent, invalid committed lineage, etc.). Retryable exceptions must remain `Decided` and continue bounded retries without changing the winner or execution identity.

### MF-6 — The required deterministic acceptance matrix is largely untested

**Violated criterion:** Issue AC 9 (deterministic verification for migration, single-winner race, candidate invalidation, cross-Connection authorization denial, restart recovery, and retention cleanup).

The added tests cover payload rendering/canonical ordering, store persistence and a sequential CAS, one ingress no-work case, and one manually pre-committed root recovery. There is no test that drives `mohist_select_agent` through `SlackInteractionRoutes` or `SlackAgentSelectionService.HandleAsync`; `rg` finds no selection interaction spec analogous to Stop/Retry. Therefore the following issue-required behavior is unverified:

- signature/freshness/chooser-context/actor rejection through the real route;
- prompt-owner and selected-Connection current policy, allowlist, live-member, and channel-membership denial;
- selected Connection soft deletion, binding drift, missing/invalid own lease, and no prompt-owner lease substitution;
- live cross-Project accepted selection;
- root launch, bound thread follow-up, unbound thread launch-and-bind, retained attachments, and original provenance;
- concurrent different-choice clicks, redelivery, failover/lost-response behavior, and exactly-one execution resources;
- real migration application with legacy rows;
- worker expiry/settlement/retention behavior and unrecoverable versus retryable recovery.

Relevant current tests are limited to `packages/server/tests/Mohist.Server.SpecTests/Specs/Slack/SlackMultiAgentIngressSpecs.cs:33-145`, `packages/server/tests/Mohist.Server.UnitTests/Slack/SlackSelectionActionPayloadTests.cs`, and `packages/server/tests/Mohist.Server.UnitTests/Slack/SlackAmbiguousPromptStoreTests.cs:77-245`. Add deterministic route/service specs for the acceptance matrix, including regression cases for MF-1 through MF-5.

## Review dimensions

- **Issue acceptance criteria re-read before diff:** checked.
- **Coverage:** FAIL — MF-1, MF-2, MF-3, MF-4, and MF-6 leave required cases incomplete.
- **Correctness:** FAIL — non-interactive claims emit duplicate outcomes, follow-up validation occurs too late or not at all, a thread-launch race can report success without work, and transient recovery failures can be terminalized.
- **Consistency with surrounding codebase:** checked. The findings above specifically identify where the new selection path diverges from the existing admission, follow-up error-handling, launch-result, and durable-recovery conventions.
- **Tests:** FAIL — MF-6. `dotnet test Mohist.Server.Tests.slnf --no-restore -p:SkipWebBuild=true` passed all available .NET tests, and `git diff --check` passed, but the issue-required selection acceptance matrix is not represented. Go tests could not be executed because `go` is not installed in this workspace.

## Observations

None beyond the must-fix findings.

<promise>FAIL</promise>