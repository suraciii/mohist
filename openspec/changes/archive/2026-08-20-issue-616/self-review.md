# Self-Review: Issue 616 Plan

Review round: 2 (re-review)

The issue was read before reviewing the artifacts with `mo issue view 616 --project proj_f6c141d63b6243bfbb481737b2243b87`. Its goals and acceptance criteria are: prevent a Mohist Bot's own message from triggering another input, prevent one Mohist Bot from becoming another Mohist Bot's input, create no provider-inbox or `SessionInput` record for rejected Bot messages, and preserve human DM, mention, and thread-reply behavior. The current-master re-delivery comment was also checked. The review covered `proposal.md`, `design.md`, `tasks.json`, `specs/slack-bot-message-admission/spec.md`, the prior review, and the current adapter/Server ingress boundaries.

## Verdict

**PASS** — no must-fix problems remain; the plan is ready to build.

## Prior Findings

- **M-001, author identity contract: fixed properly.** `design.md:32-36` now defines the allowlisted author fields, deterministic `event.app_id` and `bot_profile.app_id` fallback, Bot-ID fallback, optional Bot-user propagation, duplicate-field conflict behavior, and the separation from `apiAppId`. The production-shaped Manager and Agent fixtures plus the missing-App-ID contract gate are carried into the normative spec (`spec.md:1-26`) and T-001 (`tasks.json:9-15`). This closes the prior gap without using the receiving App identity as an author.
- **M-002, Agent App eligibility: fixed properly.** `design.md:52` chooses a workspace/enrollment-scoped rule requiring non-deleted, identity-bearing records while intentionally retaining all binding states. The rule is aligned in the spec (`spec.md:43-68`) and T-002/T-003 (`tasks.json:29-36,50-57`), including transition fixtures and deleted/tombstoned rejection. No prior finding remains unaddressed, and the disposition does not introduce a regression against Issue 616's all-Agent-App scope.

## Dimension Checks

- **Issue goals and acceptance criteria:** checked, no issue. T-002 and T-003 cover Manager self-messages, cross-target Agent messages, early ignore, and no durable work side effects; T-003 explicitly preserves human DM, channel mention, and bound-thread behavior.
- **Coverage:** checked, no issue. T-001 covers normalization, both author-payload variants, transport propagation, receiver/author separation, and acknowledgement. T-002 covers workspace attribution, lifecycle/binding transitions, Manager no-side-effects, repeated delivery, and third-party compatibility. T-003 covers both Connection targets, disabled Connections, routing paths, redelivery, and human/third-party regressions.
- **Correctness:** checked, no issue. The plan puts managed admission after authentication, lease, workspace, and stable message identity checks but before disabled auditing, human-sender validation, authorization, inbox insertion, routing, conversation processing, or outbox creation (`design.md:60-66`; `spec.md:70-119`). The exact-identity and conflict rules prevent receiver-based or third-party false matches.
- **Consistency with the current codebase and conventions:** checked, no issue. The proposed envelope and DTO changes match the existing adapter normalizer and Manager projection. The shared read-only service fits the existing scoped Slack services and enrollment/Agent App stores. The planned Connection reorder addresses the current disabled-audit-before-Bot branch at `SlackConnectionRoutes.cs:487-526`, while the Manager change addresses the current sender-required validation and inbox-first flow at `SlackManagerIngressRoutes.cs:167-203` and `SlackManagerIngressService.cs:34-158`.
- **Task ordering, completeness, and verifiability:** checked, no issue. The graph is a complete acyclic `T-001 -> T-002 -> T-003` sequence. Each task has a resolved spec anchor, implementation boundary, acceptance criteria, and focused regression coverage. T-001 establishes the wire contract before Server consumers; T-002 establishes shared attribution before Connection integration.

## Observations

- `design.md:32,42` describes a nested `authorBot.{appId,botId,botUserId,identityConflict}` value, while `spec.md:2` and T-001 use flattened names such as `authorAppId` and `authorBotId`. They describe the same required data and do not block the issue, but implementation should choose one canonical wire shape and use it consistently across TypeScript, HTTP DTOs, and tests.
- The current Manager result model serializes `decision` (`SlackManagerIngressService.cs:215-228`), while the adapter contract consumes `kind`. The plan explicitly requires `kind = ignored` (`design.md:64, tasks.json:32`) and must map that managed result without breaking existing human/claim response compatibility. This is an implementation watchpoint, not a must-fix plan defect.
- The eligibility rule intentionally covers every non-deleted identity-bearing Agent App, while the named transition fixtures emphasize `created`, `deleting`, and binding states. The implementation tests should also exercise any persisted `create_unknown` or `delete_unknown` rows if they can retain identities; the normative rule already determines their behavior, so this does not affect the verdict.

<promise>PASS</promise>
