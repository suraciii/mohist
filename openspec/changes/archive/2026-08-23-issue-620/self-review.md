# Self-Review: issue-620 (round 2 — disposition verification)

Re-review. This round verifies the dispositions recorded in `disposition.md` against the updated artifacts (`proposal.md`, `design.md`, `tasks.json` incl. new T-007, `specs/slack-failure-retryability/spec.md`, `specs/slack-retry-action/spec.md`, `specs/slack-retry-operation/spec.md`, `specs/slack-retry-attempt-execution/spec.md`) and re-verifies every code claim the dispositions rely on. The issue's User Voice, Product Shape, Domain Model, Acceptance Criteria, and Non-Goals were re-read first; findings below are judged against them, not the plan's own framing.

## Verdict

PASS. Both round-1 must-fix findings are fixed properly against the real codebase, the two courtesy fixes landed correctly, and the fixes introduced no regressions. Remaining items are observations that do not meet the must-fix bar.

## Round-1 findings — disposition verification

### MF-1 (allowlist tokens vs. recorded vocabulary) — FIXED, verified

Every factual claim in the disposition was re-checked against the code:

- `AgentJobFailureReasons` records exactly `runner-unavailable`, `runner-lost`, `report-timeout`, `workspace-unavailable` (`IAgentJobGrain.cs:409-418`) — as claimed.
- `FailureCategoryFromErrorCode` copies the runner's ErrorCode verbatim (`AgentJobGrain.cs:1548-1550`), so the live category vocabulary is the runner's mapped error kinds — as claimed.
- `mapPiErrorKind` (`agent-job-turn.ts:770-774`): `deadline-exceeded` → `timeout`, `missing-session` → `runtime-session-missing`, everything else verbatim; `mapOpenCodeErrorKind` (`agent-job-turn.ts:782-784`): only `unsupported-execution-configuration` → `unsupported_execution_configuration`. Pi's kind union (`runtime/pi/types.ts:10-18`) and OpenCode's kinds (`runtime/opencode/errors.ts`, incl. `generation-drain-timeout:112`, `deadline-exceeded:132`, `unavailable-runtime:159`) match the disposition's list exactly.
- Dispatch preflight records `runtime-unavailable` and `incompatible-execution-configuration` directly (`agent-job-turn.ts:62,70,235-242,250,258`) — as claimed.
- `probe-timeout` and `rate-limited` still have no producer anywhere outside test fixtures and an unrelated HTTP 429 code; `manager-credential-expired` **is** a real recorded category (`agent-job-turn.ts:202`, `followup-handler.ts:653`), and `context_exhaustion`/`context_exhaustion_suspected` are real (`ContextExhaustionClassifier.cs:21-22`) — the permanent list in the spec/design is accurate.

The fix itself is sound and consistent across all four artifacts: design.md Decision 1 (token→producer table with drift-guard note), the `slack-failure-retryability` spec (server-recorded / runner-recorded / reserved scenarios, real permanent examples), tasks.json T-001, and proposal.md all carry the identical retryable set — `runner-unavailable`, `runner-lost`, `report-timeout`, `deadline-exceeded`, `timeout`, `generation-drain-timeout`, `unavailable-runtime`, `runtime-unavailable`, plus reserved `rate-limited`/`probe-timeout`/`retry-safe` — and the invented tokens `deadline` and `runtime-transport-unavailable` are gone from the plan artifacts. Crucially, T-001's classifier-matrix acceptance criterion now pins the real recorded strings and references the `AgentJobFailureReasons` constants directly, so the test cannot pass against the allowlist's own re-typed tokens; this was exactly the requested fix. With this vocabulary, every failure family the issue names has a live producer token except probe-timeout/rate-limited, which no producer emits today and which the spec explicitly marks reserved — satisfying AC 1's requirement that retryable failures actually classify as retryable.

### MF-2 (thread follow-ups record no failure category) — FIXED, verified

- New **T-007** is runner-side (`packages/runner/src/server/followup-handler.ts`), as required, and is **feasible as designed**: the failure call site already extracts the error kind (`readErrorKind` at `followup-handler.ts:580`; `isUncertainFollowupFailure` at `:490-491` recognizes `unavailable-runtime` and `deadline-exceeded`), so the kind is available exactly where `recordFollowupActivity` is invoked for runtime failures. Today `recordFollowupActivity` (`followup-handler.ts:626-674`) sets `failureCategory` only for expired manager credentials (`unknown`) — the gap as described.
- T-007's degradation semantics match code reality: the three no-kind failure paths (observer flush error, rejected call, thrown exception) pass an error without a kind and correctly keep omitting the category; expired manager credentials keep `unknown`.
- The shared-mapping instruction is right: `mapPiErrorKind`/`mapOpenCodeErrorKind` are module-private in `agent-job-turn.ts` today, so T-007's "extract or reuse … rather than duplicating" names the actual work.
- The "no Server change required" claim checks out: `AgentSessionGrain.ResolveFollowupTurnResult` (`AgentSessionGrain.cs:2667-2675`) already reads `failureCategory` from the terminal `session.activity` payload into `turn.Result`, and T-003 forwards `turn.Result` facts into the follow-up delivery envelope (`AgentSessionGrain.SlackDelivery.cs` still nulls both fields today, as described).
- Adversarial check beyond round 1 — is there another production producer of thread-turn failure facts that T-007 misses? No: a server-side follow-up dispatch failure (runner unreachable, delivery throw) calls `ReleaseFollowupDispatchAsync` (`AgentSessionGrain.cs:1228-1246`), which requeues the lease and reschedules — it never terminal-fails the turn. Thread turns reach terminal `failed` only via the runner's terminal `session.activity`, which is exactly the event T-007 instruments. The producer-side chain is complete: T-007 emits → `ResolveFollowupTurnResult` stores → T-003 forwards → presentation classifies. AC 1 and AC 6 now have a real production entry point for thread retries.
- Spec coverage: the new requirement `Failed thread follow-up turns record a failure category` with three scenarios (kind recorded / no-kind omitted / manager-credential-expired keeps `unknown`) anchors T-007; design.md Decision 3, the Non-Goals carve-out, the Risks bullet, and Migration Plan step 2 (additive field, safe in any deploy order) are all updated coherently; proposal.md carries the runner Impact bullet, the capability description, and the adjusted Unaffected line.

### Observations — dispositions hold

- **O1 (`passes` field) — fixed as courtesy, verified:** all seven tasks now carry `"passes": false`; full field set matches the repo convention (checked against issue-627's schema), and all `dependsOn` references resolve.
- **O4 (pending-vs-finished wording) — fixed as courtesy, verified:** T-001 AC3 pins "a loser reading while the winner is still Pending reports the accepted-pending record; a later redelivery reports the finished one", and T-004 AC3 pins the same for interaction replay during the Pending window.
- **O2, O3, O5, O6, O7, O8 — left as recorded, reasons hold:** attachments/startup-context fidelity on retry, execution-definition source ambiguity, open questions (Manager DM, retention window, Owner binding), thread-mapping consequence, transient `AcceptFollowupAsync` accept-failure coverage, and pre-route click safety. None meet the must-fix bar against the issue's ACs; they remain implementation-time concerns.

## Regression check on the fixes

- No dropped-token leftovers (`deadline` bare, `runtime-transport-unavailable`) in any plan artifact; the only occurrences are this review's historical round-1 record and `disposition.md`'s description of the drop, both appropriate.
- Allowlist token lists are byte-consistent across design.md, proposal.md, tasks.json, and the spec (verified by cross-grep).
- All seven spec anchors resolve under the repo's anchor convention (heading text with the `Requirement:` prefix dropped — same convention as issue-505/560/589/627/631).
- Task graph remains coherent with T-007 added: `dependsOn: []` is correct for an independent runner package; T-003's note now cleanly separates the server-side forwarding (specs inject categories) from T-007's production fact source; T-004 correctly does not depend on T-007 (the route is testable with injected facts).
- The migration plan's deploy-order reasoning (new runner + old server → field ignored; old runner + new server → absent category → no button) matches the spec's degradation scenario; no contradiction introduced.

## Observations (do not affect the verdict)

- **O9 — mapping-helper extraction is real work:** `mapPiErrorKind`/`mapOpenCodeErrorKind` are module-private in `runtime/agent-job-turn.ts`; T-007's "extract or reuse" instruction covers it, but the implementer should export/move the helpers first or the "shared mapping, not a copied table" acceptance criterion is untestable.
- **O10 — `runtime-session-missing` is deliberately permanent:** the Pi-mapped form of `missing-session` could be argued transient (a runtime-session rebind may fix it), but the issue does not name it and the design documents the choice with a deliberate-change path; keep in mind if thread retries on Pi surface no button for that class.
- **O11 — reserved-token semantics:** `probe-timeout` and `rate-limited` classify as retryable the moment a producer emits them (per the spec), which is a deliberate forward-looking stance; when a producer lands, the T-001 matrix should gain that producer's constant so drift tests cover it.
- Carried from round 1: O2, O3, O5, O6, O7 (open questions/implementation-time gaps) and O8 (verified-safe) remain recorded there and stand as written.

<promise>PASS</promise>
