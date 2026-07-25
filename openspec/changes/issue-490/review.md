# Review — Issue #490 (评论提及 / comment mention)

**Reviewer:** implementation-stage review (reviewer, not fixer)
**Scope:** the changed files in this branch (`git diff origin/master --name-only`), judged against
issue #490's acceptance criteria and the plan artifacts under `openspec/changes/issue-490/`.
**Verdict:** **FAIL** — the headline capability of issue #490 is not implemented; only the event
source (T-001) and a partial launcher foundation (a slice of T-002) are present.

## Summary of what shipped

The change splits cleanly along the two planned tasks (`tasks.json`):

- **T-001 (Comment-added event emission) — fully delivered and correct.** `IssueGrain.AddCommentAsync`
  now appends a standalone `com.mohist.issue.comment-added` CloudEvent (lineage-stamped via
  `IssueLineage.BuildExtensions`) inside its existing save transaction (EpicGrain direct-emit
  pattern), and pokes the dispatcher after commit. `EventCatalog.ReverseDns.IssueCommentAdded` is
  registered. `IssueCommentAdded` payload + `IssueCommentAddedEventFactory` live outside the
  `IssueEvent` union / serializer (per design D2). T-001 has unit tests
  (`IssueCommentAddedTests.cs`) and high-integration spec tests (`IssueCommentEventSpecs.cs`)
  covering all seven scenarios in `specs/issue-comment-event/spec.md`. Verified passing locally:
  UnitTests 1354/1354, SpecTests 3016/3016.
- **T-002 (Mention dispatch) — only the launcher foundation was built.** What exists:
  - `AgentSessionResolver.CommentSessionId` / `CommentJobKey` — the comment-anchored stable key
    (`projectId\ncommentId\nagentId`), design D3.
  - `IAgentLauncher.LaunchMentionAsync` + `AgentLauncher.LaunchMentionAsync` implementation — the
    workspace-optional manual-path launch (design D1) with the comment-anchored idempotency key.
  - `GenericAgentSessionMetadata.TriggerCommentId` constant (design D6) — merged into the launch's
    trigger labels alongside `TriggerEventId`.

## Findings (must-fix before merge)

### F1 — The `comment-mention` capability is unimplemented: no `MentionDispatchHandler` exists

**Severity:** blocker (feature is non-functional)

The issue title is *评论提及：在 issue comment 里 @ Agent 启动* — start an Agent by `@`-mentioning
it in an issue comment. **Writing `@supervisor` in a comment launches nothing in this change.**
The system handler that subscribes to `com.mohist.issue.comment-added`, parses `@<token>` mentions,
applies loop prevention, resolves names to active Agents, and calls `LaunchMentionAsync` is
**completely absent** from the branch.

Confirmed by:

```
$ rg "MentionDispatchHandler" packages/
(no matches)
```

The change emits the event (T-001) and supplies the launcher's mention entrypoint
(`LaunchMentionAsync`, `CommentSessionId/JobKey`, `TriggerCommentId`) but **wires nothing to them**.
The launcher foundation is dead code with no caller in the production tree; the only references to
`LaunchMentionAsync` outside its own definition are the `RecordingAgentLauncher` stub that throws
`NotSupportedException` (`packages/server/tests/Mohist.Server.SpecTests/Support/RoutingDispatchTestSupport.cs:369-376`).

As a result, every acceptance criterion of T-002 that names observable behavior fails. None of
the spec scenarios in `specs/comment-mention/spec.md` are implemented or tested:

| Spec scenario | Implemented? | Tested? |
|---|---|---|
| Mentioning one Agent launches it | no | no |
| Prompt preserves the mention token | no | no |
| No mention means no launch | no | no |
| Mention is delimited by whitespace or punctuation | no | no |
| Mention matching is case-insensitive | no | no |
| Repeated mention of one Agent launches once | no | no |
| Distinct mentions each launch | no | no |
| Agent-authored comment does not trigger (loop prevention) | no | no |
| Human-authored comment triggers normally | no | no |
| Unknown name launches nothing | no | no |
| Archived Agent is not resolved | no | no |
| Event redelivery does not relaunch | no | no |
| Idempotency is scoped to the comment | partial (stable-key infra exists; no caller) | no |
| Mention is a single job | no | no |
| Mention launches on a backlog issue | no | no |
| Mention launches on a terminal-run issue | no | no |
| Mention provenance is recorded | partial (label exists; no caller) | no |

T-002 acceptance criteria that cannot be satisfied (per `tasks.json`):

- AC 1 — human `@`-mention launches Agent once with verbatim prompt and issue context (no handler).
- AC 2 — token parsing delimited / case-insensitive / deduped by resolved Agent id (no parser).
- AC 3 — comment whose author matches an active Agent name is never scanned (no loop prevention).
- AC 4 — `@`-ing an unknown / archived name launches nothing and logs (no handler).
- AC 5 — redelivery of the same comment launches at most once, distinct comments launch
  independently (stable-key plumbing exists but is uncalled).
- AC 6 — a mention produces exactly one AgentJob and creates no WatchEntry / routing subscription
  (no handler).
- AC 7 — the launched AgentJob's trigger labels carry the `comment-added` event id **and** comment id
  (label constant exists; never written by any production path).
- AC 8 — spec tests seed Agent + comment and assert the full comment→launch chain (no tests).
- AC 9 — a muted Agent is still launched when explicitly `@`-mentioned (no handler, so the behavior
  is undefined, not "still launched").
- AC 10 — `design/agent-mentions.md` reconciled to the workspace-optional decision (see F2).

**Action for the fix task:** build `MentionDispatchHandler`
(`[Subscription(Type = EventCatalog.ReverseDns.IssueCommentAdded)]`, `ICloudEventHandler`) per
design D1/D4/D5/D7 and `tasks.json` T-002 description: load project active Agents once; if
`author` matches an active Agent name (case-insensitive) skip scanning; otherwise parse `@<token>`
mentions, resolve each via `AgentQuerier.GetByNameAsync` (active only), launch each resolved Agent
once via `IAgentLauncher.LaunchMentionAsync`. Add the spec + unit tests called for in T-002 AC 8.
This is the bulk of T-002; without it the feature does not exist.

### F2 — `design/agent-mentions.md` was not reconciled (T-002 AC 10, explicit deliverable)

**Severity:** blocker (explicit task deliverable + spec-first rule violation)

T-002's `notes` field carries an explicit deliverable:

> DELIVERABLE: also reconcile the upstream design/agent-mentions.md wording (the '复用路由启动的
> 解析管线 … workspace 解析 … preflight' sentence and the Status底座) to the workspace-optional
> Decision 1, per AGENTS.md's差距-footnote convention — the plan deviates from that doc's literal
> wording and the doc must match the implemented behavior.

T-002 AC 10 mirrors this:

> The upstream design/agent-mentions.md is reconciled to the workspace-optional decision: the
> 'reuse routed pipeline / workspace resolution / preflight' wording and the Status底座 no longer
> contradict the implemented manual-path behavior (差距 footnote per AGENTS.md).

The branch does **not** touch `design/agent-mentions.md`:

```
$ git diff origin/master --stat -- design/ docs/
(no output)
```

The upstream doc still says (at `design/agent-mentions.md:44-45`):

> 启动复用路由启动的解析管线：issue 上下文、workspace 解析、触发标签（记 `comment-id` 与事件
> id）与路由启动一致 …

… which directly contradicts design.md Decision 1 in this same change (mention uses the manual,
workspace-**optional** path, not the routed path that requires a nonterminal run + workspace).
AGENTS.md's spec-first rule says the doc must describe the target and gap footnotes record current
divergence; neither has been applied. The fix task must edit `design/agent-mentions.md` to align
the launch-path wording with Decision 1 and add the 差距 footnote.

### F3 — `AgentQuerier.GetByNameAsync` is case-sensitive; the spec requires case-insensitive resolution

**Severity:** must-fix (blocks AC 2 "Mention matching is case-insensitive" once F1 is built)

`specs/comment-mention/spec.md` *Token parsing* mandates case-insensitive resolution (scenario
"Mention matching is case-insensitive"). `self-review.md` carries this as N3 ("correctly flagged
as a build-time note in T-002"). The current `AgentQuerier.GetByNameAsync`
(`packages/server/src/Mohist.Server/Agent/Services/AgentQuerier.cs:33-39`) is a direct EF
`==` query:

```csharp
var row = await db.Agents.AsNoTracking()
    .FirstOrDefaultAsync(agent => agent.ProjectId == projectId && agent.Name == name);
```

SQLite's default `==` on text is case-sensitive (and Postgres `text` `==` is case-sensitive too),
so `@SuperVisor` would not resolve an Agent named `supervisor`. The fix task that builds
`MentionDispatchHandler` must either change this query to a case-insensitive comparison (e.g.
`EF.Functions.Like(agent.Name, name)` after lowercasing both sides, or a client-side ordinal
case-insensitive fallback as `GetByIdAsync` already does) or do the case-folding in the handler
before calling the querier. Calling out explicitly because it is silently invisible until F1 lands
and a spec test for the case-insensitive scenario is added.

## Notes (non-blocking, informational)

### N1 — `RecordingAgentLauncher.LaunchMentionAsync` throws; will need a recording stub when F1 is built

`packages/server/tests/Mohist.Server.SpecTests/Support/RoutingDispatchTestSupport.cs:369-376`
throws `NotSupportedException` for `LaunchMentionAsync` (matching the existing `LaunchAsync` stub,
which the comment says "deliberately captures routed launches only"). This was added only to keep
the build green after `IAgentLauncher` gained the new member; it is not itself a bug. But once
`MentionDispatchHandler` exists and the T-002 spec tests use `RoutingDispatchTestSupport` to drive
the handler, this fake must be extended to capture mention launches (a `_mentionLaunches` bag +
`RecordedMentionLaunch` record) the same way it already records routed launches. Mentioning here so
the fix task doesn't rebuild a parallel fake infrastructure.

### N2 — T-001 looks solid

For completeness: the T-001 deliverable (the `issue-comment-event` capability) is correctly
implemented and matches its spec. Specifically checked and confirmed:

- `IssueGrain.AddCommentAsync` (`packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:1415-1455`)
  stages the comment row and the CloudEvent on the same `MohistDbContext` and commits both in one
  `SaveChangesAsync` (auto-transaction), so persistence-before-observable ordering holds; the
  dispatcher poke is fire-and-forget after commit.
- The envelope's `source` is `IssueEventPersistence.IssueSource(projectId, number)`, so
  `EventStore.AppendAsync` routes the row into the `IssueEvents` table and `ListUndeliveredAsync`
  picks it up — the event is dispatchable, not just persisted.
- `IssueCommentAddedEventFactory.Build` stamps `projectid`/`issue` always and `epic` only when
  present (never an empty value); `subject` = issue number; verbatim payload.
- `EventCatalog.All` includes the new constant (`EventCatalog.cs:62`), so
  `EventCatalogTests.All_ContainsEveryReverseDnsConstant` continues to pass.
- All seven scenarios in `specs/issue-comment-event/spec.md` are covered by
  `IssueCommentEventSpecs.cs` (including the "issue body edit with `@` does not emit" and "create
  does not emit" source-exclusivity scenarios).

No defects found in T-001. The fail verdict is solely about the missing T-002 deliverables (F1, F2)
plus the case-insensitivity prerequisite (F3).

<promise>FAIL</promise>
