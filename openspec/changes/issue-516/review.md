# Review — Issue 516 (thread discussion as agent startup context)

Reviewer role: reviewer, not fixer. Findings only. This review judges the
implementation (commits `d97818e79` T-001 + `26f72a68d` T-002) against the issue
acceptance criteria and the plan artifacts under `openspec/changes/issue-516/`.

## Verdict

**FAIL** — Finding F1 defeats the feature in production: the thread-history
reader discards every prior message whenever the mention message is present in
the fetched `conversations.replies` page, which is the normal case. The feature
silently launches with no startup context for the exact scenario it targets.

## F1 — BLOCKER: Reader discards all prior messages when it reaches the mention

**Where:** `packages/server/src/Mohist.Server/Slack/SlackThreadHistoryReader.cs`,
`ReadAsync`, lines 88–95.

```csharp
foreach (var message in response.Messages ?? [])
{
    if (message is null || string.IsNullOrWhiteSpace(message.Ts))
        continue;
    if (string.Equals(message.Ts, mentionTs, StringComparison.Ordinal))
        return Empty(collected.Count);   // <-- discards `collected`
    collected.Add(message);
}
```

**What is wrong.** `conversations.replies` (called with `ts = <thread root>`)
returns the thread parent **and every reply**, oldest first, **including the
mention message itself** — the mention is a reply in the thread. So on the
feature's target scenario (first `@Bot` in an existing thread) the page that the
bot fetches contains: root, prior human replies, then the mention. The loop
collects the root + prior replies into `collected`, then on reaching the mention
executes `return Empty(collected.Count)`. The `Empty` factory (lines 170–171)
builds `Array.Empty<SlackConversationMessage>()`, so **every prior message
collected so far is thrown away** and the outcome is `Empty`.

The caller then maps `Empty` to `startupContext = null`
(`SlackConnectionRoutes.cs:1243–1245`) and launches on the task text alone —
byte-for-byte the pre-feature behavior. In production the agent never receives
any thread history; the entire capability is a no-op for the common case (any
thread shorter than the pagination depth cap). The same discard happens on
multi-page threads: any prior messages collected in earlier pages are dropped
the moment the mention is seen in a later page.

The root-mention path happens to be correct only because there the mention *is*
the root, so `collected` is genuinely empty when the equality hits.

**Why the tests did not catch it.** Both fakes serve `conversations.replies`
pages that **omit the mention message**:

- Spec fake `RecordingSlackApiClient.ConversationsRepliesAsync`
  (`tests/Mohist.Server.SpecTests/Support/RecordingSlackApiClient.cs:26–38`)
  returns whatever is queued, and the spec tests queue pages containing only
  prior messages — never the mention. Example:
  `SlackThreadContextSpecs.FirstMentionInExistingThread_ImportsThreadHistory_AsStartupContext`
  queues two messages (`...000110`, `...000120`) while the mention is
  `...000200`; the mention is never returned, so the loop never hits the
  equality branch and falls through to `Imported`. Real Slack would return the
  `...000200` message in the same page.
- The unit test `SlackThreadHistoryReaderTests.ReadAsync_StopsAtMentionMessage`
  (lines 46–64) **actively encodes the bug as expected behavior**: it feeds a
  page containing an `"older"` message followed by the mention, then asserts
  `Assert.Equal(Empty, result.Outcome)` and `Assert.Empty(result.Messages)`. The
  `"older"` message is prior discussion that should be imported; the test
  asserts it is discarded.

**Fix direction (for the follow-up task).** When the mention is reached and
`collected.Count > 0`, return `Imported(collected, …)` rather than `Empty(…)`.
The tests must be corrected to mirror real Slack: every fake `replies` page for
an existing-thread scenario must include the mention message, and the assertions
must verify the prior messages survive. Consider filtering by timestamp
(`message.Ts < mentionTs`) instead of exact-equality-stop — that also resolves
F4 below.

This violates acceptance criteria AC‑1 and AC‑2 of the issue (the user cannot
see the imported scope, and no history is imported at all), and slack-thread-context
requirements R1 ("SHALL read the bounded thread history … AND SHALL supply that
history") and R3.

## F2 — Problem: Depth-cap exhaustion silently imports the oldest slice, not the most-recent discussion

**Where:** `SlackThreadHistoryReader.ReadAsync`, the loop fallthrough at lines
103–109 (reached when the `for` loop runs `depthCap` iterations without ever
seeing the mention or an empty `nextCursor`).

**What is wrong.** If a thread is longer than `StartupContextPaginationDepthCap`
pages (default 10 × 200 = 2000 messages), the reader fetches only the **oldest**
pages, never reaches the mention, exits the loop, and returns `Imported` with
the oldest messages. `ApplyBudget` then drops the oldest of those and keeps a
**middle slice** — not "the most recent discussion" the spec requires
(`slack-thread-context/spec.md` R3: "SHALL retain the most recent messages up to
the limit … SHALL NOT drop newest messages to retain older ones").

Design D5 states completeness requires that "pagination completes"; a depth-cap
hit means pagination did **not** complete (there was still a `nextCursor`).
Treating that as a complete `Imported` read contradicts D5 and silently violates
the truncation ordering for long threads.

**Fix direction.** Either refuse on depth-cap exhaustion (return `Refused`, no
`AgentJob`, consistent with D5's completeness contract), or explicitly mark the
result as depth-limited so the truncation semantics are not violated. At
minimum, distinguish "reached the end of pagination" from "gave up at the cap".

## F3 — Minor: `ReleaseAsync` on the refuse path is dead code; the reservation does not exist yet

**Where:** `SlackConnectionRoutes.ReadThreadHistoryIfAnyAsync`
(`SlackConnectionRoutes.cs:411–433`) calls
`req.ThreadLaunchReservations.ReleaseAsync(...)` when the read returns `Refused`.

**What is wrong.** The history read is performed at `SlackConnectionRoutes.cs:1235`,
**before** `LaunchChannelRootAsync` is called. The launch reservation is only
created inside `LaunchChannelRootAsync` at line 1541 (`ReserveAsync`), i.e.
**after** the read. On the refuse path the code returns at line 1240 without
ever entering `LaunchChannelRootAsync`, so no reservation exists; the
`ReleaseAsync` call deletes zero rows. The refuse path is functionally correct
(there is nothing to clean up, so a re-mention is not blocked), but the code,
the design risk bullet ("On refusal the launch reservation must be released"),
the progress note ("releases the unbound reservation"), and the spec test name
`ReadFailureThenReMention_ReLaunchesAfterReservationReleased` all imply a
reservation is being released when it is not. This is misleading rather than
incorrect; flag it so the follow-up either removes the dead call or moves the
reservation to before the read so the release is meaningful.

## F4 — Edge case: post-mention messages are imported if the mention is absent from the fetched pages

**Where:** `SlackThreadHistoryReader.ReadAsync`, the same equality-stop at line
92.

**What is wrong.** Detection of "the mention" relies on exact `ts` equality. If
the mention message is absent from the fetched pages (it was deleted between
event delivery and the history read, or is not visible to the bot at read time),
the equality is never hit; the reader paginates to the end and returns
`Imported` with **every** fetched message — including messages timestamped
**after** the mention. That violates design D6 ("scope: all Bot-visible thread
messages strictly before the mention, by timestamp"). A timestamp-based filter
(`message.Ts < mentionTs`) instead of equality-stop would fix F1 and F4
together.

## F5 — Cosmetic: indentation regression in AgentSessionGrain.cs

**Where:** `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs`,
lines 2180–2192. The `if (inputMatch is not null)` block lost its enclosing
indentation — the `if` keyword sits at column 0 and the braces/inner block are
over-indented relative to the method body. Compiles, but is clearly unintended
and inconsistent with the surrounding style.

## What is solid (does not need action)

- The Agent API startup-context channel (T-001) is correct: `StartupContext`
  threads through the launch chain as append-only `[Id(n)]` ids, is excluded
  from `AgentLaunchCoordinatorCodec.Fingerprint` (verified at
  `AgentLaunchCoordinatorTypes.cs:234–261` — only Prompt/AgentRef/Runtime/…/
  attachments/origin are hashed), is persisted on the plan, and is composed at
  dispatch via `AgentStartupContextComposer` while `Prompt`/`Text` stay
  task-only. `AgentStartupContextLaunchSpecs` and `AgentStartupContextAuditSpecs`
  cover this layer convincingly.
- First-launch-only is structurally enforced (`RecordFollowupTurnCommand` has no
  `StartupContext` member; the audit spec asserts this via reflection).
- The read-only-background composition framing mirrors the proven runner
  pattern; Instructions/Runtime/Model/Variant/Skills are provably unchanged.
- Refuse-on-Slack-error / refuse-on-transport-failure behavior is correct, and
  the empty-mention rejection is preserved on the thread-context path.

<promise>FAIL</promise>
