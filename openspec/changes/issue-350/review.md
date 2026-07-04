# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: candidate snapshot
  Evidence: The post-build candidate contains no product diff for issue 350. Before this review file was created, `git status --short`, `git diff --name-only`, `git diff --name-only origin/master...HEAD`, and `git diff --name-only origin/master..HEAD` all returned no changed files; `git rev-parse HEAD` and `git rev-parse origin/master` both returned `843102d1a51cfda25b3391366cf120221c5c625f`. Therefore none of the issue acceptance criteria are implemented in the reviewed snapshot: approval payload, failure payload, completion payload, per-type body rendering, start-notification default/off config, webhook address/secret/enabled-types config, delivery failure isolation, or Hermes documentation. [disallowed:reason] Repairing this would require implementing the product feature, public configuration surface, tests, and docs, which is outside the review repair policy.
  SuggestedAction: Rebuild the candidate with actual product changes outside `openspec/changes/issue-350/`, then re-run review against that snapshot.
  Verification: Re-run `git diff --name-only origin/master...HEAD` and confirm it includes the server/docs/test files that implement the issue.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server` outbound Hermes notification path
  Evidence: The existing server only exposes event catalog and Web Inbox paths for the relevant operator events. `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:91` through `packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs:130` list workflow/issue event types, while `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:63` through `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:67` subscribe to failed, approval-requested, work-started, and completed events only for inbox projection. The handler writes `InboxItemDraft` at `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:147` through `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:156` and publishes only an inbox hint at `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:162` through `packages/server/src/Mohist.Server/Events/Subscriptions/InboxProjectionHandler.cs:185`. Search for `Hermes|Webhook|webhook|Outbound|Notify|NotificationOptions|IssueNotification` under `packages/server/src/Mohist.Server` found no Hermes webhook client, notification renderer, signing logic, enabled-type configuration, or payload contract. [disallowed:reason] Implementing the missing outbound network callback is a product behavior and public contract change.
  SuggestedAction: Add a server-side event subscriber for the issue key events that renders type-specific message bodies, builds the Hermes webhook payload with raw fields and pre-rendered body, signs/sends via configured webhook URL, and swallows/logs delivery failures.
  Verification: Add and run server specs covering approval, failure, completion, optional start, disabled URL, enabled-type filtering, signature, and failed delivery behavior.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: configuration contract
  Evidence: The issue requires configurable webhook address, signing secret, enabled notification types, and a default-off `started` notification. The reviewed snapshot has no changed configuration files or option types. Existing searches surfaced no server option model such as `NotificationOptions` or Hermes webhook settings, and no changed CLI/docs/config contract explaining how users set these values. [disallowed:reason] Defining configuration keys and behavior is a public contract and architectural/product decision.
  SuggestedAction: Introduce the minimal configuration surface for Hermes outbound notifications, document the keys, and ensure unset address disables delivery without side effects.
  Verification: Add tests that default approval/failure/completion on, start off, configured start on, and unset URL prevents network calls.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: Hermes template and subscription documentation
  Evidence: The acceptance criteria require a checked-in Hermes-side message template and subscription guide with webhook platform enablement, subscription creation, and secret alignment steps. `docs/**/*hermes*` matched no files, and markdown search for `webhook|Webhook|Hermes|hermes` only found unrelated historical mentions in `design/architecture.md`, `docs/skills.md`, and archived OpenSpec changes. No product documentation was added in the candidate. [disallowed:reason] Writing the required guide is a product documentation deliverable, not a typo or formatting repair.
  SuggestedAction: Add a user-facing doc under `docs/` that includes the Hermes webhook template, subscription setup flow, secret configuration, enabled notification types, and an example payload/body.
  Verification: Re-run documentation search and manually check the guide against the issue's documentation acceptance criterion.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: regression coverage
  Evidence: The reviewed snapshot has no changed tests. Search under `packages/server/tests` for `Hermes|Webhook|webhook|Outbound|Notify|NotificationOptions|IssueNotification` found only unrelated outbound HTTP tracing, Hermes skill-install, and existing notification subscription tests. There is no coverage for payload shape, message rendering branches, failure non-blocking behavior, no-network-when-unconfigured behavior, or start-notification default-off behavior.
  SuggestedAction: Add focused server specs with fake HTTP/message handler and fake configuration covering every acceptance criterion and the no-real-network test constraint.
  Verification: Run `npm test` or the relevant server test command after adding coverage.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `openspec/changes/issue-350/` workflow artifacts
  Evidence: Before this review file was created, `openspec/changes/issue-350/` did not exist and globbing `openspec/changes/issue-350/**/*` returned no files. The requested dependencies included proposal, specs, design, and tasks; they were unavailable in the candidate snapshot, so traceability from issue acceptance criteria to implementation cannot be checked. This is not a product deliverable failure by itself, but it is a workflow evidence gap for this review.
  SuggestedAction: Restore or generate the issue proposal, delta specs, design, tasks, and self-review artifacts before the next review so reviewers can compare intended and actual behavior.
  Verification: Re-run `rg --files openspec/changes/issue-350` and confirm the expected artifacts are present.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: info
  Scope: existing Web Inbox projection
  Evidence: Existing `InboxProjectionHandler` already projects failed, approval-requested, started, and completed events into Web Inbox items and logs/swallows projection exceptions. That behavior appears relevant context but is explicitly not a substitute for the Hermes outbound webhook feature requested by issue 350.
  SuggestedAction: Keep the Web Inbox behavior unchanged while adding the separate Hermes outbound path.
  Status: out-of-scope

<promise>FAIL</promise>
