# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs`, event dispatch path
  Evidence: Hermes delivery is awaited inline from the CloudEvent subscriber at `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs:60`. The event bus awaits each matching subscription sequentially at `packages/server/src/Mohist.Server/Infrastructure/Events/InMemoryEventBus.cs:77` through `packages/server/src/Mohist.Server/Infrastructure/Events/InMemoryEventBus.cs:85`, and workflow persistence awaits bus publish before returning at `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:63` through `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:72`. Issue event publication also awaits the bus after append at `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:637` through `packages/server/src/Mohist.Server/Issue/Grains/IssueGrain.cs:638`. `HermesWebhookClient` then awaits the external HTTP request and success status at `packages/server/src/Mohist.Server/Notifications/HermesWebhookClient.cs:43` through `packages/server/src/Mohist.Server/Notifications/HermesWebhookClient.cs:44`, with no configured short timeout in `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:72` through `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs:74` or `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistSiloRegistration.cs:49` through `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistSiloRegistration.cs:51`. This violates the issue acceptance criterion that webhook errors/unreachability do not affect workflow/issue execution and explicitly do not block it: a slow or hung Hermes receiver can hold workflow save/issue save command completion until `HttpClient` finishes or times out. [disallowed:reason] Fixing this requires a product behavior decision about asynchronous dispatch, bounded timeout, background queue, or other execution semantics, which is outside the review repair policy.
  SuggestedAction: Decouple outbound webhook delivery from the workflow/issue mutation path, or enforce a deliberately small bounded timeout and prove that event handling cannot stall workflow execution. Keep failures logged/swallowed and preserve the no-child-process boundary.
  Verification: Add a regression spec with a fake delayed `IHermesWebhookClient` or HTTP handler that proves publishing an approval/failure/completion event returns without waiting for slow Hermes delivery, then run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~HermesIssueNotificationSpecs` and `npm test`.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs` failure payload/body
  Evidence: Failure notifications copy `WorkflowRunFailed.Message` directly into `failureReason` at `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs:104` through `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs:106`, then place it verbatim in the raw payload at `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs:23` through `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs:36` and in the chat body via `NormalizeReason` at `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs:14` through `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs:15` and `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs:48` through `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationRenderer.cs:49`. The upstream message can be task/check failure text from `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:55` through `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Task.cs:70` or `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Check.cs:75` through `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Check.cs:96`. The current test only verifies that a benign string, `check task failed`, does not contain the word `stack` at `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs:49` through `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs:64`; it does not prove stack-like multi-line failure text is stripped or summarized. This does not satisfy the product requirement that failure notifications do not include stack traces. [disallowed:reason] Deciding how to sanitize, truncate, or split raw failure fields versus rendered chat body changes payload semantics and security posture.
  SuggestedAction: Define and implement a failure-reason rendering policy that excludes stack traces from the user-facing notification body and, if raw details must remain in payload fields, make that contract explicit and safe. Add tests with stack-like input including `at ...` frames and multi-line exception text.
  Verification: Add renderer/handler specs asserting stack-like failure input is not present in `body` and that the intended `failureReason` contract is followed, then run the focused notification specs and full server tests.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs`
  Evidence: The tests cover payload branches, default filtering, disabled URL, signing, and exception swallowing, but they do not cover the two riskiest acceptance edges: slow/unreachable delivery must not block workflow/issue execution, and failure notifications must not leak stack-like failure details. The current delivery-failure test at `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs:147` through `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs:159` only proves a thrown exception is swallowed after the send attempt is awaited; it does not detect blocking behavior. The failure body test at `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs:49` through `packages/server/tests/Mohist.Server.Tests/Specs/Notifications/HermesIssueNotificationSpecs.cs:64` uses only a benign one-line reason.
  SuggestedAction: Add regression coverage for delayed Hermes delivery and stack-like failure messages. Prefer fake time/fake clients rather than real network or wall-clock assertions.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~HermesIssueNotificationSpecs` and `npm test` after adding the tests.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs` and `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs`
  Evidence: The Hermes handler duplicates most of the Inbox projection identity resolution logic for workflow-run annotations and issue-event extensions. The duplication is understandable for the MVP and keeps this change isolated, but future changes to event identity stamping could drift between Web Inbox and Hermes delivery.
  SuggestedAction: After the Hermes behavior is fixed, consider extracting a small shared resolver for key issue notification event identity if another outbound path or another identity rule change appears.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: verification
  Evidence: `npm test` with a 120s tool timeout completed the .NET/server phase successfully (`3710` passed, `13` skipped) but timed out after entering the web Vitest phase. Standalone verification then passed: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~HermesIssueNotificationSpecs` passed (`9` passed), `npm run test:ci -w packages/web` passed (`4077` passed, `1` skipped), and `npm run test:ci --workspaces --if-present` passed (`63` runner test files, `875` tests). The initial timeout is a review tooling limit, not a candidate failure.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: acceptance criteria satisfied by current snapshot
  Evidence: The candidate adds a server-side Hermes notification handler and HTTP client, binds `Mohist:Notifications:Hermes`, defaults enabled types to `approval_requested`, `workflow_failed`, and `issue_completed` while leaving `issue_started` off by default, includes rendered body and top-level raw fields in `HermesIssueNotificationPayload`, signs with `X-Mohist-Signature` when `Secret` is configured, and documents Hermes setup in `docs/hermes-notifications.md`. These parts are evidenced by `packages/server/src/Mohist.Server/Notifications/HermesNotificationOptions.cs:11` through `packages/server/src/Mohist.Server/Notifications/HermesNotificationOptions.cs:27`, `packages/server/src/Mohist.Server/Notifications/HermesWebhookClient.cs:24` through `packages/server/src/Mohist.Server/Notifications/HermesWebhookClient.cs:51`, `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationPayload.cs:3` through `packages/server/src/Mohist.Server/Notifications/HermesIssueNotificationPayload.cs:29`, and `docs/hermes-notifications.md:7` through `docs/hermes-notifications.md:95`.
  SuggestedAction: Preserve these behaviors while fixing the blocking execution and failure-detail issues.
  Status: out-of-scope

<promise>FAIL</promise>
