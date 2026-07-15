# Review Report

## Result: FAIL

Focused acceptance evidence exists for stable response identity, source parity, idle conflicts, missing-runtime errors, and runner request/result shapes in `AgentSessionRecoveryApiSpecs.cs` and `session-command-handler.spec.ts`. The repaired server suite passes 2,777 specs. The candidate still has unresolved recovery reliability, UI availability, and merge-gate defects.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test fixtures for the runtimeSessionId wire contract
  Evidence: `RunnerRoutes.cs:322` and `RunnerRoutes.cs:411` correctly require `runtimeSessionId`, but 20 server spec payloads omitted it and `AgentJobGrainSpecs.cs:334` constructed an `agent-launch` source without the mandatory agent label. This caused 21 server spec failures.
  Verification: `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj -p:SkipWebBuild=true --no-restore` passed 2,777/2,777.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: docs/cli-reference.md implementation-gap note
  Evidence: The gap note said Reset establishes a replacement Runtime Session, while `packages/runner/src/runtime/host.ts:149-155` deliberately returns `unavailable` until issue-409 implements runtime operations. Updated the note to state that limitation.
  Verification: `git diff --check` passed; the complete test run confirms all runtime operation tests retain their existing behavior.
  Status: resolved

## Blocking Items

- [ID: item-3]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs
  Evidence: `BeginSessionCommandAsync` persists a Compact reservation at `AgentSessionGrain.cs:211-241`. A subsequent Compact only receives `recovery_in_progress` (`:211-220`), and a Reset cannot take over the Compact reservation. The persisted `StartedAt` in `AgentSession.cs:277-282` has no expiry or reconciliation path. A server failure after this save and before dispatch therefore leaves Compact permanently unavailable. [disallowed:recovery behavior and durable state semantics]
  SuggestedAction: Add a durable command-outcome/retry protocol, including a safe lease/reconciliation policy for an interrupted Compact reservation. Do not clear an uncertain reservation without coordinating the runtime effect.
  Verification: Persist a Compact reservation, simulate process loss before dispatch, then retry after reactivation. The retry must either resume the original operation exactly once or recover to a usable state without duplicate compaction.
  Status: unresolved

- [ID: item-4]
  Severity: blocking
  Scope: reset dispatch timeout and runtime replacement safety
  Evidence: `RunnerSessionCommandDispatcher.cs:9,33-55` turns an unanswered invocation into `unavailable` after 15 seconds. `AgentSessionRecoveryRoutes.cs:120-125` immediately abandons that reservation. If the runner completes the timed-out Reset afterward, it can create a replacement Runtime Session whose result is discarded; a user retry gets a new operation id and can create another replacement. `session-command-handler.ts:42-70` only remembers reset operations in process memory, so a runner restart also loses deduplication. This can orphan physical sessions and weakens the expected-binding data-safety guarantee. [disallowed:data safety and retry protocol]
  SuggestedAction: Retain and replay the same durable operation until its outcome is known, and make runtime replacement idempotent across runner restarts using that operation identity.
  Verification: Delay a Reset handler beyond the dispatcher timeout, retry the HTTP request, and repeat with a runner restart. Assert one physical replacement and one lineage append.
  Status: unresolved

- [ID: item-5]
  Severity: warning
  Scope: packages/runner/src/server/session-command-handler.ts
  Evidence: Compact requests carry an operation id (`:113-117`), but `registerSessionCommandHandler` deduplicates only Reset (`:42,49-69`). A duplicated Compact delivery directly invokes the runtime handler again, so a runtime summarize operation can execute twice while the server records only one compaction. There is no duplicate-Compact test.
  SuggestedAction: Deduplicate Compact and Reset uniformly by session id plus operation id, retaining an in-flight and completed result with bounded, durable recovery semantics.
  Verification: Concurrently invoke the same Compact operation twice and assert one runtime handler call, one compaction fact, and one transcript record.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: generic session recovery UI
  Evidence: `AgentSessionQuerier.cs:239-248` reports every bound, nonterminal generic session as `running`, even when its activity window has expired. `SessionDetailShell.tsx:195-200` passes that status to `SessionRecoveryActions.tsx:23-30`, which disables Compact and Reset for `running`. The grain permits recovery once the actual Session status is idle (`AgentSessionGrain.cs:343-348`), so users cannot recover an idle Agent-launch session from the web UI.
  SuggestedAction: Expose a server-owned active/recovery-availability value for generic-session summaries and drive the action state from it instead of the workbench list status.
  Verification: Advance fake time past the idle threshold for an Agent-launch session, render its page, and assert both recovery controls are enabled and target the canonical route.
  Status: open

- [ID: item-7]
  Severity: blocking
  Scope: changed web test files and the default test gate
  Evidence: `npm test` fails at `packages/web` test-boundary enforcement before Vitest runs: `useWorkflowRunSessions.test.tsx` is 345 lines versus the 300-line limit; `GenericSessionPage.test.tsx` is 459 lines versus its 436-line baseline; `useSessionTimeline.dom.test.ts` is 1,101 lines versus its 1,058-line baseline. All three files are part of this candidate. [disallowed:broad test refactor]
  SuggestedAction: Split each subject into focused test files and remove the size-budget violations without expanding the baseline.
  Verification: `npm test` must complete successfully. The direct Web suite already passes 335 files and 4,692 tests, but that does not replace the required boundary gate.
  Status: open

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-8]
  Severity: info
  Scope: docs/epics.md
  Evidence: `docs/epics.md:30` documents Epic priorities as `p0` through `p3`, while `docs/epics.md:57` says `p0-p4`; the CLI validates `p0|p1|p2|p3` in `packages/cli/Mohist.Cli/MohistCliCommands.Epic.cs:74`.
  SuggestedAction: Correct the field table to `p0-p3`.
  Status: pre-existing

- [ID: item-9]
  Severity: warning
  Scope: docs/workflow-profiles.md
  Evidence: `docs/workflow-profiles.md:60-67` exposes action identifiers, runtime-variable keys, recovery handler details, and `gh` commands in a product document, contrary to the documentation boundary in `AGENTS.md`.
  SuggestedAction: Move implementation contracts to `design/` and rewrite the user-facing page in product and domain terms.
  Status: pre-existing

<promise>FAIL</promise>
