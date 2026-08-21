# Review: Issue 618

Review round: first review of the product change at `05264091c` against the live issue acceptance criteria and the issue-618 specs/design.

## Must-Fix Findings

### 1. Manager credentials do not authorize the requested project or workspace target

`AuthResolutionMiddleware` validates a Manager lease and re-authenticates only the lease's actor and Enrollment (`packages/server/src/Mohist.Server/Auth/Identity/AuthResolutionMiddleware.cs:90-134`). `ManagerCapabilityAdmissionMiddleware` then checks only whether the URL maps to an allowlisted route (`packages/server/src/Mohist.Server/Auth/Identity/ManagerCapabilityAdmissionMiddleware.cs:20-41`). It never calls the target-aware `ManagerActorAccessDecider.AuthorizeAsync`; that overload is used for ingress and the retired tool path, not for Manager CLI HTTP calls (`packages/server/src/Mohist.Server/Slack/Services/ManagerActorAccessDecider.cs:56-84`).

Consequently, a valid Manager lease issued for workspace `T1` can request `/api/slack-manager/status?workspaceTeamId=T2` because the handler trusts the query value (`packages/server/src/Mohist.Server/Api/SlackManagerIngressRoutes.cs:70-79`). The same lease can select an arbitrary `{projectRef}` and reach Agent creation or Slack Connection mutations: the project filter only resolves existence (`packages/server/src/Mohist.Server/Api/ProjectResolutionEndpointFilter.cs:26-62`), while the handlers mutate the resolved project without Manager target authorization (`packages/server/src/Mohist.Server/Api/AgentDefinitionRoutes.cs:21-37`, `packages/server/src/Mohist.Server/Api/SlackManagerRoutes.cs:26-42,151-169`).

This violates issue Acceptance Criterion 6 and the Manager CLI capability requirement that current actor, Enrollment, Project, workspace, Agent, and Connection authorization reject cross-workspace or unauthorized targets before lookup/mutation. The Manager admission boundary must bind every supplied workspace/project/resource target to the current lease origin and reauthorize it immediately before the service call, without weakening ordinary operator/Web authorization.

### 2. The generic Manager shell can retrieve both raw credentials from the broker

`ManagerExecutionBoundary.environment()` exposes `MOHIST_MANAGER_BROKER` to every command in the Manager runtime (`packages/runner/src/runtime/manager-execution-boundary.ts:80-96`). The broker accepts any local socket client that sends JSON with `kind` equal to `management` or `reply` and returns the corresponding plaintext credential; it does not authenticate that the caller is the generated `mo` launcher (`packages/runner/src/runtime/manager-execution-boundary.ts:222-249`). The launcher itself uses exactly that unauthenticated protocol (`packages/runner/src/runtime/manager-execution-boundary.ts:175-203`).

A model-issued generic shell command can therefore read the broker path from its environment, connect to the socket with the same JSON payload, and obtain either bearer. This defeats the stated private launcher boundary and exposes a credential to a generic command/model-visible process. It violates issue Acceptance Criterion 4 and the execution-credentials spec requiring each credential to be available only to the scoped `mo` child, never to a generic shell or command transcript. The broker needs an unforgeable launcher-only authorization boundary, or the bearer retrieval must be moved into a process boundary that generic commands cannot access.

### 3. Follow-up Manager turns receive no capability grant and run through the shared runtime

The poll path mints a grant only for `WorkDispatch` responses (`packages/server/src/Mohist.Server/Api/RunnerRoutes.WorkDispatchResponses.cs:12-57`). The ordinary follow-up path sends only `SlackExecutionContext` (`packages/server/src/Mohist.Server/Api/AgentSessionFollowupDispatcher.cs:54-70`), and its wire contract has no Manager grant or execution-boundary field (`packages/server/src/Mohist.Server/Contracts/RunnerControlContracts.cs:77-85`). On the Runner, `followup-handler.ts` resolves the normal runtime, builds the follow-up prompt, and calls `callFollowup` without a `ManagerExecutionBoundary` (`packages/runner/src/server/followup-handler.ts:117-118,215-217,300-301`).

Thus the second and later turns cannot receive fresh management/reply credentials, do not get the scoped Pi command executor, and use the shared OpenCode runtime rather than an isolated per-execution server. The same gap applies to a Session continuation after recovery. This violates issue Acceptance Criteria 4 and 5 and T-003's per-follow-up/recovered-execution requirements. Follow-up dispatch needs its own fresh dual lease, poll/control transport that keeps the grant non-durable, scoped runtime setup, redaction, and cleanup.

### 4. Credential expiry only fails the current turn; it does not create the required fresh recovery transition

The Server returns `manager_credential_expired` from lease validation (`packages/server/src/Mohist.Server/Slack/Services/ManagerExecutionCredentials.cs:405-409`), and the Runner boundary throws when its local grant expires (`packages/runner/src/runtime/manager-execution-boundary.ts:95-101`). The resulting AgentJob is reported as an ordinary failed result and the boundary is released; there is no Manager-specific durable expiry transition that reauthorizes the immutable Slack origin, creates exactly one replacement/new turn, and issues new credentials. No other changed path schedules such a transition.

This violates issue Acceptance Criterion 5: after credential expiry the system must reauthorize from the immutable origin and issue a new credential without reusing or replaying the interrupted mutation. It also violates the expiry-recovery scenarios in `specs/manager-execution-credentials/spec.md`. Expiry must be modeled as a durable recovery outcome, including no automatic replay of an uncertain state-changing CLI call.

### 5. Follow-up terminal delivery loses the originating message identity and cannot close Manager liveness

Manager ingress stores receipt/progress under the actual inbound message identity (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:155-177`). However, the Session follow-up terminal event sets `messageTs` to `null` and carries only the Session-level conversation/thread metadata (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:3881-3929`). `SlackTerminalDeliveryHandler` then substitutes `terminal:{jobKey}` as the source message and derives the Manager progress key from that synthetic identity (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:36-48`).

For a follow-up, that synthetic key cannot match the receipt/working row created for the actual Slack message, and the reaction target is not the user's message. Successful, failed, cancelled, or unknown follow-ups can therefore leave the real message in receipt/working state without its terminal reaction. This violates issue Acceptance Criterion 8 and the Manager reaction-liveness spec's terminal convergence requirement. The follow-up terminal fact must carry the triggering message/origin (or resolve it durably) and use the same logical execution identity for finalization.

### 6. Ingress projects liveness and reports acceptance even when the Session coordinator rejected dispatch

`SlackManagerConversationService` returns `NotAccepted` for empty input and for `SessionActivityUnknownException` (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerConversationService.cs:49-52,108-112`). The ingress has already enqueued receipt and working projections (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:155-177`), but after processing it checks `response.Accepted` only when deciding whether to update the mapping (`:185`). It then unconditionally marks the inbox dispatched and returns an `accepted` result (`:208-209`).

Those messages have no accepted Agent turn and no terminal event, so their liveness remains stuck indefinitely. This violates issue Acceptance Criteria 1 and 8: an accepted request must have a real Agent execution, and every failure/unknown path must close with one terminal reaction. The coordinator rejection must either remain a rejected, non-dispatched ingress without accepted liveness or be converted into one explicit terminal/unknown convergence path before the inbox is marked dispatched.

### 7. Runner capability gating advertises unsupported Manager execution on Windows

`buildRegistrationState` advertises every Manager runtime capability unconditionally (`packages/runner/src/runtime/registration-state.ts:26-34`), but `ManagerExecutionBoundary.startBroker()` always throws on Windows because the required named-pipe adapter is not implemented (`packages/runner/src/runtime/manager-execution-boundary.ts:207-219`). The Server therefore considers such a Runner eligible, sends it Manager work, and the host refuses the work only after beginning its local journal (`packages/runner/src/runtime/host.ts:766-786`).

This violates T-003's capability/version-gating requirement and the issue's execution-boundary criterion: a runtime that cannot establish the boundary must not receive Manager dispatch. Advertise capabilities only when the platform implementation exists, or reject/gate the Runner before Manager work is claimed and leave the durable job recoverable rather than stranded.

### 8. The lease store and deployment epoch are process-local despite the required shared restart boundary

The production registration creates an in-memory singleton dictionary and a process-local epoch for each Server host (`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:226-233`). The implementation explicitly documents that this is process-local and that production topologies may replace it later (`packages/server/src/Mohist.Server/Slack/Services/ManagerExecutionCredentials.cs:119-124`); no shared lease store or atomic shared epoch is provided by this change.

With multiple Server instances, a grant issued by one instance is unknown to another, and restarting one instance does not invalidate grants held by another instance. That breaks the single authoritative restart boundary and fail-closed cross-instance validation required by the execution-credentials design, and can prevent recovery from obtaining a valid grant. This is incomplete relative to T-003 and the design's restart requirements; the lease hashes and epoch must use one shared runtime store/epoch before Manager work is enabled in a multi-instance topology.

## Observations

- The retired `SlackManagerToolTurnProcessor`, `SlackManagerToolExecutor`, and execution-fence store remain as unregistered compatibility source. That is consistent with the migration notes, but the old protocol should not be allowed back into service registration.
- `AgentSessionGrain` still extracts `assistantText` for follow-up delivery events (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:3919`), and `SlackTerminalDelivery` still carries that field. The current terminal handler does not use it to author a reply, so this is not an additional verdict-changing finding, but it leaves the retired extraction path and unnecessary model-output surface in the durable event contract.
- `ManagerExecutionLeaseStore.RemoveExpired` is implemented but no expiry sweeper is registered. Validation rejects expired values, so this is primarily runtime-store cleanup and observability debt rather than a separate acceptance failure.

## Dimension Checks

- Issue acceptance criteria: checked adversarially; criteria 1, 4, 5, 6, and 8 have the must-fix failures above. Criteria 2, 3, and 7 have implementation coverage in the inspected paths, but their end-to-end behavior is not sufficient to offset those failures.
- Coverage: checked; initial natural-language sessions, CLI catalog, initial poll grants, and initial liveness are present, but follow-up grant/recovery/expiry and cross-target authorization paths are incomplete.
- Correctness: checked with failure cases for cross-workspace targets, generic shell access, follow-up execution, fast/rejected dispatch, and follow-up terminal delivery; each produced a concrete broken or missing path above.
- Consistency: checked; ordinary Session and Slack anchor primitives are reused, but Manager admission does not reuse the target-aware authorization contract and follow-ups do not reuse the Manager execution boundary.
- Tests: checked. The available verification completed without failures: Server Unit 3,120/3,120, Server Spec 3,709/3,709, CLI 1,950/1,950, and Runner boundary/context 17/17. The test platform ignored the requested filters and ran the full assemblies, and the passing suites contain no cases for the valid-lease cross-target, broker extraction, follow-up grant, expiry recovery, rejected-dispatch liveness, or follow-up origin cases.

**Verdict: FAIL**
<promise>FAIL</promise>
