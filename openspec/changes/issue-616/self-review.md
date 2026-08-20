# Self-Review: Issue 616 Plan

Review round: 1 (full sweep)

The canonical issue record was read first with `mo issue view 616 --project proj_f6c141d63b6243bfbb481737b2243b87`. The issue's product goal is to reject every Mohist Manager/Agent Bot message before work admission, prevent self and Bot-to-Bot triggering, create no provider-inbox or work-input state, and leave human flows unchanged. The issue comment requiring a current-master re-delivery was also checked; the active change contains only plan artifacts and no unrelated implementation branch changes.

## Verdict

**FAIL** — two must-fix problems remain in the plan. The final marker is `FAIL`.

## Must-Fix Findings

### M-001: Author identity extraction is not a closed contract

The plan's managed-Bot predicate requires a preserved author identity, but the source and precedence of that identity are left unresolved. `design.md:32` says the envelope should carry the author App identity and available Bot/user identifiers, while `design.md:74` names `event.app_id` and `bot_profile.app_id` only as possible variants. `design.md:92` leaves which variant is guaranteed, the precedence, and the availability of a Bot-user identity as an open question. No task resolves that question or names the production fixture/contract that makes the normalizer complete.

This is not merely an implementation detail: the same design explicitly says that a Bot with no usable author identity is treated as non-managed (`design.md:48-50,74`), and the spec repeats that such a Bot is not managed (`spec.md:46-52`). An actual Mohist Bot event that uses an unhandled Slack variant would therefore miss the shared suppression rule and would not receive the definite ignored outcome required by the issue's product shape. That leaves the self-loop and cross-Bot guarantees dependent on an unverified payload assumption, violating Issue 616's goals and acceptance criteria that a Bot's own reply and another Mohist Bot's message never become new input.

**Required disposition:** make the author-identity contract buildable before implementation: identify the supported Slack payload fields and deterministic precedence, define how App ID/Bot-user ID disagreement is handled, and add representative fixtures proving that Manager and Agent Bot messages emitted by the supported Mohist Slack Apps produce a matchable author identity. If Slack does not guarantee a matchable author identity for the subscribed event, the plan must define a different authoritative mechanism within the issue's scope; silently classifying that event as unrelated is insufficient.

### M-002: Managed Agent App eligibility is contradictory and unresolved

The issue covers messages from "any Agent App Bot," but the plan has no settled definition of which persisted Agent App identity is authoritative for admission. `design.md:50` excludes deleted, unbound, and otherwise no-longer-registered identities, and `design.md:93` explicitly asks whether to match only `AppLifecycle = created` plus `BindingState = bound` or any non-deleted identity-bearing registration. T-002 nevertheless hardcodes the stricter `created/bound/non-deleted` filter (`tasks.json:29-36`) without resolving the open question or explaining why an unbound or binding-transition Agent App Bot is outside Issue 616's scope.

A real Bot from an Agent App in a lifecycle/binding race can therefore be classified as an unrelated third-party Bot and reach the existing target-specific branch. That contradicts the issue's unconditional Mohist-Bot product goal and the acceptance criterion that one Mohist Bot cannot become another Mohist Bot's input. It also makes the normative spec and the task acceptance criteria disagree about which "registered" Agent App identities are managed.

**Required disposition:** choose and state one authoritative eligibility rule, align `design.md`, the spec, T-002/T-003, and the regression fixtures with it, and cover the lifecycle/binding transition that the rule intentionally accepts or rejects. The chosen rule must be justified against the issue's "all Agent App Bot" scope and must remain workspace-scoped and exact-identity based.

## Dimension Checks

- **Issue goals and acceptance criteria:** **FAIL** — M-001 and M-002 leave the self-message and cross-Mohist-Bot suppression guarantees conditional on unresolved identity and lifecycle decisions.
- **Coverage:** **FAIL** — the artifacts cover the happy path and no-side-effects path, but do not cover the production author-payload variants or the unresolved Agent App eligibility states needed to establish that every in-scope Mohist Bot is covered.
- **Correctness:** **FAIL** — the proposed safe fallback for absent/unrecognized author metadata and the unbound/lifecycle filter can route an in-scope Mohist Bot through the non-managed branch rather than the required early managed-Bot admission decision.
- **Consistency with the current codebase and conventions:** checked, no issue. The proposed files and boundaries match the existing adapter normalizer, explicit Manager transport projection, scoped Server services, enrollment/App stores, and early Connection route boundary. The current Connection order confirms why disabled auditing must remain after the new predicate (`SlackConnectionRoutes.cs:489-526`).
- **Task ordering, completeness, and verifiability:** **FAIL** — the three-task DAG is acyclic and ordered (`T-001 -> T-002 -> T-003`), but two behavior-defining open questions have no decision or verification task. The plan is not build-ready until those decisions are made and tested.

## Observations

- The current Manager route rejects an empty `SenderSlackUserId` before calling `SlackManagerIngressService` (`SlackManagerIngressRoutes.cs:176-203`), and the service accepts the inbox before actor/conversation work (`SlackManagerIngressService.cs:43-58,75-158`). T-002 correctly points the new admission branch ahead of both. The plan should preserve compatibility for existing direct human callers that omit `senderKind`, as `design.md:42` requires.
- The current Manager service result serializes `decision`, while the adapter's ingress contract consumes `kind`. T-002 explicitly requires a `kind = ignored` result, so this is an implementation watchpoint rather than a separate must-fix finding; the plan should map the managed result without regressing existing human/claim response compatibility.
- The current adapter already acknowledges after a definite ingress result and drains existing deliveries afterward (`packages/mohist-slack/src/adapter.ts:372-389`). Preserving that drain is consistent with the issue: draining an older delivery is not creating a response for the ignored Bot event.
- The task graph parses successfully, contains three tasks, has no missing dependencies, and is acyclic. All three task spec anchors resolve to requirements in `specs/slack-bot-message-admission/spec.md`.

<promise>FAIL</promise>
