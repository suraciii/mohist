# Self-Review: Issue 616

Re-review. The current issue details were read from `mo issue view 616 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body,comments,attachments,feedback`. The issue requires preventing Mohist Bot self-triggering and Mohist Bot-to-Bot triggering, forbids provider inbox and `SessionInput` side effects, preserves human DM/mention/thread behavior, and explicitly keeps non-Mohist-managed third-party Bot behavior out of scope.

## Previous Finding Disposition

The previous review's must-fix finding was that the plan applied the ignored admission rule to every Slack Bot classification. That finding is fixed properly. The revised proposal limits admission to Bot events attributable to a Mohist-managed Manager App or Agent App. `design.md` now carries author App metadata separately from the receiving `apiAppId`, resolves the active workspace Manager identity plus non-deleted identity-bound Agent App identities, and matches by author App ID or managed Bot user ID. The specification and T-002/T-003 explicitly require unmatched third-party Bots to retain their target-specific pre-616 behavior, and T-001 requires that third-party Bot events reach Server rather than being dropped in the adapter.

This disposition holds against the issue: the revised plan covers Manager and Agent self-messages and cross-Mohist-Bot messages while preserving the issue's explicit third-party-Bot boundary. No regression from this fix creates a must-fix problem.

## Must-Fix Findings

None.

## Dimension Checks

- **Issue goals and acceptance criteria:** Checked, no issue. The early managed-identity admission prevents self-triggering and Bot-to-Bot input; the plan requires no provider inbox, `SessionInput`, AgentJob, session, follow-up, mapping, or outbox side effects; and it includes human DM, mention, and bound-thread regression coverage. The plan is stronger than the issue where it also forbids a disabled-Connection audit row for an ignored managed Bot, which does not conflict with the issue's goals.
- **Coverage:** Checked, no issue. T-001 covers authoritative human/bot/unknown normalization, Bot author identity propagation, acknowledgement, and third-party preservation. T-002 and T-003 cover both ingress targets, missing sender IDs, disabled Connections, existing sessions and follow-ups, cross-Agent Bot authors, no-side-effects assertions, human/unknown regressions, and unmatched third-party Bots.
- **Correctness:** Checked, no issue. The proposed order validates operator access, the runtime lease, and stable message identity before a read-only workspace identity lookup; a matching managed Bot then returns ignored before disabled auditing, authorization, claims, routing, session work, inbox admission, and outbox creation. A non-matching Bot remains on the existing target-specific path, so the revised identity test is sufficient to avoid broadening the policy.
- **Consistency with the current codebase and conventions:** Checked, no issue. The identity sources already exist in `SlackWorkspaceEnrollment` and `ManagedSlackAgentApp`, including App IDs, Bot user IDs, workspace ownership, and deletion state. The stores support read-only queries, the adapter already acknowledges successful ingress results and only renders a Web response for backpressure, and the plan requires no schema or dependency change.
- **Task breakdown, ordering, and verifiability:** Checked, no issue. T-001 supplies the wire contract before the two Server ingress tasks, and each task has concrete acceptance criteria and focused regression output. The no-side-effects tests use baseline state and sentinel text, while the third-party fixtures verify the repaired scope boundary.

## Observations

- The current Manager result record serializes its `Decision` property as `decision`, while the adapter transport type and plan examples use `kind`. A successful Manager response is still acknowledged by the current adapter because only `backpressured` affects rendering, so this is not a must-fix for issue 616; implementation should nevertheless pin the ignored wire mapping in the route/transport test.
- T-002 introduces the shared managed-identity resolver, while T-003 depends only on T-001 despite requiring that shared resolver. The implementation order should make that dependency explicit, or place the resolver in a small prerequisite task. This does not make the plan incomplete because both tasks already require the same resolver contract.
- `senderBotAppId` extraction is described using metadata such as `bot_profile.app_id`. The adapter tests should enumerate the supported author metadata locations, including any event-level App ID, while continuing to exclude the outer `api_app_id`. The required author-identity contract and Bot-user-ID fallback are already present, so this is an implementation-hardening observation.

## Verdict

PASS. The previous must-fix scope problem is resolved; no must-fix problem remains relative to the issue's goals or acceptance criteria.

<promise>PASS</promise>