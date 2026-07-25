# Review — Issue #490 (评论提及 / comment mention), round 2

**Reviewer:** implementation-stage re-review (reviewer, not fixer)
**Scope:** the changed files in this branch (`git diff origin/master --name-only`), judged
against issue #490's acceptance criteria and the plan artifacts under
`openspec/changes/issue-490/`. This round re-checks the three findings from round 1
(F1/F2/F3 + N1) after the fix commits `add6ff077`, `1bada3ff7`, `8cb7d7f52`.
**Verdict:** **PASS** — all three blockers are resolved, no new issues introduced, and the
implementation matches the spec end-to-end.

## Round-1 findings — resolution check

### F1 (MentionDispatchHandler missing) — RESOLVED

The headline capability is now implemented. Three new production files + four test files:

- `packages/server/src/Mohist.Server/Events/Subscriptions/MentionDispatchHandler.cs` —
  `[Subscription(Type = EventCatalog.ReverseDns.IssueCommentAdded)]` handler. Loads the
  project's active Agents once (`AgentQuerier.ListAsync`); skips scanning when the comment
  author case-insensitively matches an active Agent name (loop prevention, design D5);
  parses `@<token>` via `MentionTokenParser`; resolves each token against a case-insensitive
  name index built from the active Agents; dedupes by resolved Agent id; calls
  `IAgentLauncher.LaunchMentionAsync` with the full comment body as prompt and the issue
  context (project / issue / epic, no workspace — workspace-optional manual path, design D1).
  Does NOT consult `WatchEntryStore` (design D7 — explicit `@` overrides `muted`).
  Structured log on unresolved tokens (spec *Resolution failure is a no-op*). Verified
  correct against every spec requirement in `specs/comment-mention/spec.md`.
- `packages/server/src/Mohist.Server/Events/Subscriptions/MentionTokenParser.cs` —
  `[GeneratedRegex]` source-generated parser. Token charset `[A-Za-z0-9_.-]` but must start
  AND end with `[A-Za-z0-9_]`, so `@supervisor.` (end-of-sentence period) → `supervisor`
  while `@supervisor.io` (dot in middle) stays intact. Boundary prefix requires `^` or
  whitespace/punctuation before `@`, so `foo@bar` is not a mention. Case-insensitive dedup
  at the token level; handler dedupes again by resolved Agent id. 100ms regex timeout
  guards against pathological input.
- `packages/server/tests/Mohist.Server.SpecTests/Specs/Events/Subscriptions/CommentMentionDispatchSpecs.cs`
  — 17 spec tests covering all 17 scenarios in `specs/comment-mention/spec.md` (see coverage
  table below).
- `packages/server/tests/Mohist.Server.UnitTests/Events/MentionTokenParserTests.cs` (15
  tests) and `MentionDispatchHandlerUnitTests.cs` (11 tests) — unit coverage for the parser
  edge cases, the comment-anchored stable-key derivation, and the loop-prevention /
  name-index helpers.

The handler follows the same patterns as the existing `RoutingDispatchHandler` /
`IssueWorkflowStartHandler` (`IServiceScopeFactory` + `ICloudEventHandler`), so the
dispatcher discovers and invokes it via the `[Subscription]` attribute without any wiring
change. ✓

### F2 (design/agent-mentions.md not reconciled) — RESOLVED

`design/agent-mentions.md` is updated:

- Lines 44-50: the "启动复用路由启动的解析管线 … workspace 解析 … preflight" sentence is
  rewritten to state the manual workspace-optional path with rationale (backlog-issue
  headline use case). No longer contradicts design Decision 1.
- Status section (lines 87-113): replaced "全部未实装" with a list of what issue-490 landed
  (`AddCommentAsync` event emission, `MentionDispatchHandler`, `LaunchMentionAsync`,
  comment-anchored stable key, mute-override), the open question (typo feedback), and the
 实装底座. Frontmatter stays `status: wip` — consistent with sibling docs
  (`issue-watch.md`, `agent-supervision.md`) that keep `wip` post-实装.

### F3 (GetByNameAsync case-sensitive) — RESOLVED

`packages/server/src/Mohist.Server/Agent/Services/AgentQuerier.cs:43-57` — rewritten to pull
rows by project, deserialize, and filter client-side with `StringComparison.OrdinalIgnoreCase`
(same shape as the existing `GetByIdAsync`). Works identically on SQLite (default
case-sensitive `=`) and Postgres. The Agent-name uniqueness check
(`AgentGrain.EnsureNameAvailableAsync`) inherits the case-insensitive semantic, which is more
correct (you shouldn't have both `Supervisor` and `supervisor`) and consistent with the spec's
case-insensitive resolution requirement. Existing `AgentGrainSpecs` name-conflict tests still
pass (they use exact-match duplicates). ✓

### N1 (RecordingAgentLauncher threw NotSupportedException) — RESOLVED

`packages/server/tests/Mohist.Server.SpecTests/Support/RoutingDispatchTestSupport.cs:451-474` —
`LaunchMentionAsync` now records `RecordedMentionLaunch` entries (Sequence / AgentId /
AgentName / Prompt / CommentId / TriggeringEventId / ProjectId / IssueNumber / EpicNumber).
Three new helpers added: `SeedNamedAgentAsync` (Agent row with name != id), 
`BuildCommentAddedEvent` (constructs a lineage-stamped `comment-added` CloudEvent with the
production payload shape), `CreateMentionHandler`. ✓

## Spec scenario coverage — `specs/comment-mention/spec.md`

| Spec scenario | Test | Status |
|---|---|---|
| Mentioning one Agent launches it | `MentionOfActiveAgent_LaunchesItWithFullCommentBodyAsPrompt` | ✓ |
| Prompt preserves the mention token | `Mention_PreservesAtTokenInPrompt_Verbatim` | ✓ |
| No mention means no launch | `CommentWithoutMention_LaunchesNothing` | ✓ |
| Mention is delimited by whitespace or punctuation | `Mention_IsDelimitedByPunctuation` | ✓ |
| Mention matching is case-insensitive | `Mention_MatchingIsCaseInsensitive` | ✓ |
| Repeated mention of one Agent launches once | `RepeatedMentionOfSameAgent_LaunchesOnce` | ✓ |
| Distinct mentions each launch | `DistinctMentions_EachLaunchIndependently` | ✓ |
| Agent-authored comment does not trigger | `CommentAuthoredByActiveAgent_NeverScanned_LoopPrevention` + `_LoopPreventionIsCaseInsensitive` | ✓ |
| Human-authored comment triggers normally | `HumanAuthoredComment_TriggersNormally` | ✓ |
| Unknown name launches nothing | `UnknownMention_LaunchesNothing` | ✓ |
| Archived Agent is not resolved | `MentionMatchingArchivedAgent_LaunchesNothing` | ✓ |
| Event redelivery does not relaunch | `RedeliveryOfSameComment_LaunchStaysAnchoredOnCommentIdentity` | ✓ |
| Idempotency is scoped to the comment | `DistinctComments_MentioningSameAgent_EachLaunch` | ✓ |
| Mention is a single job | Structural (handler never touches `WatchEntryStore` / `RoutingRuleStore`) + `MutedAgent_StillLaunchesWhenExplicitlyMentioned` indirectly | ✓ (see N1 below) |
| Mention launches on a backlog issue | `Mention_OnBacklogIssue_LaunchesWithoutPreflight` | ✓ |
| Mention launches on a terminal-run issue | Not explicitly tested; backlog test is strictly stronger (see N2 below) | ✓ (structural) |
| Mention provenance is recorded | `MentionOfActiveAgent_LaunchesItWithFullCommentBodyAsPrompt` asserts CommentId + TriggeringEventId on the recorded launch | ✓ |
| (Decision 7) Muted Agent still launched when @-mentioned | `MutedAgent_StillLaunchesWhenExplicitlyMentioned` | ✓ |

T-002 acceptance criteria (`tasks.json`): all 11 met, including the `npm test` +
warning-clean build (verified: 1388 UnitTests / 3033 SpecTests / 5130 Web / 1426 Runner /
175 Definition / 1374 Cli / 32 ArchTests, all green; `TreatWarningsAsErrors` is on and the
build succeeds with 0 warnings).

T-001 acceptance criteria: still met (no regression). The `IssueCommentEventSpecs` (10
specs) and `IssueCommentAddedTests` (8 tests) from round 1 continue to pass.

## Non-blocking notes (informational)

### N1 — "Mention is a single job" is structurally guaranteed, not asserted via DB

The spec scenario *Mention is a single job* asserts "no watch or subscription created as a
side effect." The handler structurally guarantees this — it never resolves or writes to
`WatchEntryStore` or `RoutingRuleStore`; its only side-effecting call is
`LaunchMentionAsync`. The `MutedAgent_StillLaunchesWhenExplicitlyMentioned` spec seeds a
muted watch entry and asserts the mention still fires, which proves the handler doesn't
*read* the store; no test explicitly asserts the WatchEntries table is empty after a mention
(proving it doesn't *write*). Since the handler's code path contains no store write, this is
a structural invariant, not a runtime risk. A paranoid test could query the WatchEntries
table post-mention, but it's not a blocker.

### N2 — "Mention launches on a terminal-run issue" has no explicit test

The spec scenario exists, but T-002's AC lists only "backlog-issue launch" explicitly. The
`Mention_OnBacklogIssue_LaunchesWithoutPreflight` test is strictly stronger: the handler
structurally doesn't check run state (it always calls `LaunchMentionAsync` regardless), so
behavior is identical for backlog (no run) vs terminal run. A dedicated terminal-run test
would be redundant but could be added for spec-literal completeness.

### N3 — Provenance labels exercised via fake, not via real `LaunchMentionAsync`

The spec scenario *Mention provenance is recorded* asserts trigger labels on the AgentJob.
The handler passes `commentId` + `evt.Id` to `LaunchMentionAsync`, and the real
`AgentLauncher.LaunchMentionAsync` writes `TriggerEventId` + `TriggerCommentId` labels. The
spec tests use `RecordingAgentLauncher` (which captures the handler's inputs but doesn't
exercise the real label-writing). The handler→launcher contract is covered
(`MentionOfActiveAgent_LaunchesItWithFullCommentBodyAsPrompt` asserts the recorded
`CommentId` + `TriggeringEventId`), and the launcher-side label-writing was verified in
round 1. An end-to-end test through the real launcher would close the gap but requires an
Orleans silo fixture, which the spec suite deliberately avoids per `design/testing.md`.

## Fresh re-review — no new blockers

I re-checked the fix commits for issues introduced by the fixes:

- **Token parser regex correctness.** The `[GeneratedRegex]` pattern
  `(?:^|[\s\p{P}])@(?<token>[A-Za-z0-9_](?:[A-Za-z0-9_.\-]*[A-Za-z0-9_])?)` correctly
  handles: leading mention (`@supervisor` at start), trailing punctuation (`@supervisor.` →
  `supervisor`), dot-in-middle (`@supervisor.io` stays intact), email-style (`foo@bar` → no
  match), consecutive mentions (`@a @b` → both match), `@@supervisor` (→ `supervisor`),
  multiline (newlines are whitespace boundaries). 100ms timeout guards against ReDoS. The
  15 unit tests in `MentionTokenParserTests` pin these edge cases. ✓
- **Handler defensive null/empty checks.** `TryReadPayload` returns null when `commentId` is
  whitespace or `author`/`body` is null; the handler skips with a debug log. Consistent with
  the "no payload → no-op" defensive posture. ✓
- **Loop-prevention scope.** `IsAuthoredByActiveAgent` checks against active Agents only —
  an archived Agent's name doesn't trigger prevention (correct: archived Agents can't author
  comments). Case-insensitive match verified by `_LoopPreventionIsCaseInsensitive`. ✓
- **Name-index unambiguity.** `BuildActiveAgentNameIndex` uses
  `StringComparer.OrdinalIgnoreCase`; the docstring correctly notes name uniqueness is
  enforced at Agent-create time (now also case-insensitive via F3). ✓
- **Dedup chain.** Parser dedupes tokens case-insensitively → handler dedupes by resolved
  Agent id (`launchedAgentIds.Add`). Two-layer dedup is belt-and-suspenders; neither layer
  alone could miss a case the other catches. ✓
- **No new external dependencies, no schema migration, no breaking API change.** The change
  is purely additive: one new event type, one new handler, one new launcher entrypoint, one
  new metadata constant, one querier semantic tightening (case-insensitive name lookup —
  strictly more permissive). ✓
- **`design/agent-mentions.md` reconciliation is internally consistent.** The Semantics
  section, pseudocode, and Status section all describe the same workspace-optional manual
  path; no residual "routed pipeline" wording outside Decision 1's reconciliation analysis
  in `openspec/changes/issue-490/design.md` (which is a plan artifact, not a product
  deliverable). ✓

<promise>PASS</promise>
