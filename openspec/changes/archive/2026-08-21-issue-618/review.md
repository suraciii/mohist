# Review: Issue 618

Review round: re-review of the current product change against the live issue acceptance criteria and the plan/spec artifacts under `openspec/changes/issue-618/`. The prior review was a PASS; I rechecked its dispositions against the current tree and checked for regressions.

## Previous Finding Dispositions

- **Direct Manager-broker requests:** fixed properly. `ManagerExecutionBoundary` now authenticates the Linux Unix-socket peer as the generated launcher before admitting requests, while retaining catalog, kind, frozen-working-directory, budget, and output-redaction gates. The integration coverage exercises valid direct management and reply requests and confirms that no child is spawned.
- **Token-bearing child cleanup:** fixed properly. Manager CLI children are tracked and terminated with bounded SIGTERM/SIGKILL escalation before broker and execution-directory cleanup.
- **Runner-loss Manager recovery:** fixed properly. Server-side Runner loss enters `Unknown`, delivers the initial terminal state, revokes the interrupted work leases, creates one inspection-only recovery turn, and suppresses replay of the uncertain original prompt. The recovery turn is part of the normal follow-up dispatch contract and receives fresh grants.
- **Same-user ordinary credential bypass:** fixed properly for the current Runner boundary. Manager child environments remove ordinary CLI credential variables and redirect `HOME` to an empty execution home; the real Manager CLI uses the Runner-owned credential proxy rather than an inherited bearer. The integration test checks environment and credential-file fallback behavior.
- **Follow-up expiry, completion, epoch, cancellation, recovery, and duplicate-grant cleanup:** checked, no unresolved must-fix issue found in the current implementation. Follow-up boundaries are registered, disposed, and revoked on terminal/cancellation paths; epoch invalidation covers the shared registry; expiry routes through durable inspection recovery; and Runner-loss/expiry recovery does not replay an uncertain mutation.

## Dimension Checks

- **Acceptance criteria: checked, no must-fix issue.** Natural-language Manager turns use ordinary Slack Sessions and the published collaboration Skill; replies are Agent-authored through the separate Manager reply action; managed Bot messages are suppressed before work admission; credentials are ephemeral, route-scoped, reauthorized, and kept out of model-visible/durable surfaces; protected operations and arbitrary routes are denied; and successful, failed, cancelled, unknown, and recovered outcomes converge to one terminal reaction without synthesized text.
- **Coverage: checked, no must-fix issue.** Current tests cover initial and follow-up Session routing, replay during launch, missing-runtime replacement, managed-Bot admission and redelivery, exact management envelopes and denials, authoritative results, reply anchor/ownership/idempotency, credential issuance and validation, leakage/redaction, ordinary credential fallback isolation, expiry and recovery dispatch, Runner-loss recovery, epoch invalidation, cancellation, terminal liveness, absent progress, duplicate terminal delivery, and silence.
- **Correctness: checked, no must-fix issue.** The Server remains authoritative for origin, actor/enrollment, target authorization, command results, outbox ownership, and liveness. The Runner carries the execution contract and uses isolated Manager runtime boundaries; it does not interpret model prose as management instructions or a reply.
- **Consistency with the surrounding codebase: checked, no additional criterion-level issue.** The change reuses the existing Session grain, Slack execution context, CLI/Web application services, outbox, managed-Bot admission, and liveness projection. Ordinary Slack Connection and protected CLI/Web paths retain their existing behavior.
- **Tests: checked, no issue.** The recorded verification in the current change history reports passing Server build/unit/spec, CLI, Runner typecheck/build/full suite and Manager boundary integration checks, plus adapter, formatting, file-size, and repository verification gates. The worktree is clean and `git diff --check` passes.

## Observations

1. Manager execution is intentionally Linux-only because the peer-authenticated Unix-socket boundary has no Windows named-pipe implementation. The issue acceptance criteria do not require cross-platform Manager execution; deployment must provide the required Linux peer-inspection facilities.
2. `ManagerExecutionLeaseStore.RemoveExpired` is present but no obvious production sweeper call site was found. Expired credentials are rejected and the retained store contains hashes/metadata rather than plaintext, so this is cleanup/observability debt, not an acceptance-level failure.
3. The retired Manager parser/executor/fence source remains in the repository as unregistered compatibility source. The current runtime does not resolve it, and the plan explicitly permits retaining the old fence schema/source during rollout; this does not reintroduce the old model-output protocol.
4. A few shared helper names and catalog mirrors require ongoing synchronization as the CLI evolves. The current tests cover the relevant Manager vocabulary and do not demonstrate a criterion-level mismatch.

<promise>PASS</promise>
