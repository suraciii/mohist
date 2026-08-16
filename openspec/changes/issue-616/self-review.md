# Self-Review: Issue 616

First review. The current issue details were read from `mo issue view 616 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body,comments,attachments,feedback`. The issue requires preventing Mohist Bot self-triggering and Bot-to-Bot triggering, forbids provider inbox and `SessionInput` side effects, and preserves human DM, mention, and thread behavior. It explicitly excludes policy changes for non-Mohist-managed third-party Bots.

## Must-Fix Findings

1. **The plan applies the new admission rule to every Slack Bot, not only Mohist-managed Bots.**

   The issue's Product Shape says that non-Mohist-managed Bot messages are out of scope and must retain the existing sender-classification behavior. The plan instead makes this normative for every Bot classification: `proposal.md:8` says to ignore "every normalized Slack event identified as authored by a Bot"; `design.md:45` carries only the three-way `senderKind` value; `design.md:62` sends every `bot` value to `ignored`; and the capability spec requires the same for every event with `senderKind = bot` (`specs/slack-bot-message-admission/spec.md:22-29,37-52`).

   This is not an implementable distinction in the proposed contract. `packages/mohist-slack/src/adapter-events.ts:124-127` classifies any Slack `bot_id` or `bot_message` marker as `bot`, without identifying whether that Bot is managed by Mohist. T-001 only forwards that classification and explicitly does not forward the raw Bot identity. Consequently, T-003 would change the Manager path for third-party Bot events too: the current Manager route requires `senderSlackUserId` before reaching the service (`packages/server/src/Mohist.Server/Api/SlackManagerIngressRoutes.cs:177-201`), whereas the planned early `bot` branch would accept a missing sender as ignored. A third-party Bot with a supplied sender ID would also be treated as non-human by the new branch instead of following the current Manager path's existing validation and authorization behavior.

   This violates the issue's explicit out-of-scope boundary, even though the plan covers the four in-scope acceptance criteria. The plan must define how Mohist-managed Bot identity is established and preserve the existing behavior for non-Mohist-managed Bots, or the issue scope and normative spec must be reconciled before implementation. As written, the plan cannot be built without delivering behavior the issue expressly excludes.

## Dimension Checks

- **Issue goals and acceptance criteria:** Checked. The self-loop, Mohist Bot-to-Bot, no-inbox/no-`SessionInput`, and human-routing goals are represented, but the explicit third-party-Bot scope boundary is contradicted.
- **Coverage:** Checked. The artifacts cover both ingress targets, adapter acknowledgement, no-side-effects cases, and human/unknown regressions. They do not cover preservation of non-Mohist-managed third-party Bot behavior, which is the must-fix gap above.
- **Correctness:** Checked. The proposed early ordering is coherent for Mohist Bot messages: operator authentication and lease validation precede non-persistent identity validation, and the ignored branch precedes inbox, claims, access, routing, and session work. The unqualified `senderKind = bot` rule is incorrect for the issue's narrower Bot scope.
- **Consistency with the current codebase and conventions:** Checked. The plan follows the existing stateless adapter, Server-owned inbox/outbox, and early classification patterns. It does not preserve the Manager route's current distinction between a missing sender and a sender that reaches Manager validation for a third-party Bot, and it leaves the managed-Bot identity source unspecified.
- **Task breakdown, ordering, and verifiability:** Checked. The T-001 -> T-002/T-003 dependency graph is ordered and test-oriented, and the tasks name focused adapter and Server verification. The breakdown has no task or acceptance test for the issue's excluded third-party-Bot behavior, so it is incomplete until the must-fix scope decision is represented.

## Observations

- The Manager service currently returns `SlackManagerIngressResult` with a `Decision` property (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:215-227`), while the adapter `IngressResult` and the plan's ignored-result examples use `kind`. `SlackAdapter.handleEvent` currently acknowledges any successful ingress response and only inspects `kind` for `backpressured`, so this does not by itself block the requested acknowledgement, but T-003 should explicitly lock down the wire mapping for `ignored`.
- `design.md:49` proposes server-side parsing of the classification, while T-003 says a shared parser "may" be used. Requiring one shared parser would better prevent Manager and Connection drift, especially for omitted versus explicit `unknown`, but the current artifacts do specify the important explicit-`unknown` behavior.
- The no-side-effects test strategy is strong for database work state and unique sentinel text. It does not state how tests will observe application logs, even though the proposal and spec prohibit recording ignored text as work input. This is a verification improvement, not a must-fix for the issue's listed acceptance criteria.

## Verdict

FAIL. The plan has one must-fix scope problem: it mandates ignoring all Slack Bot classifications without preserving the issue's explicit non-Mohist-managed third-party Bot boundary.

<promise>FAIL</promise>