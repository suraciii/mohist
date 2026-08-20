# Review

Review round: 3 (re-review)

## Verdict

**PASS** - no must-fix problems remain; the change is ready to merge.

## Prior Findings

- **M-001, explicit unknown Manager sender: fixed properly.** `SlackManagerIngressService.cs:46-49` rejects an explicit `senderKind=unknown` before managed admission, inbox insertion, authorization, or conversation processing. The regression in `SlackManagedBotAdmissionSpecs.cs` verifies that an unknown event carrying a sender ID creates neither inbox nor outbox state. The compatibility fallback for an omitted sender kind remains available for legacy human callers.
- **M-002, supported fixture App-ID contract: fixed properly.** `adapter-events.test.ts:286-379` verifies both supported Manager and Agent-shaped fixtures, requires a non-conflicting author App-ID, and explicitly fails both fixture contracts when `event.app_id` and `bot_profile.app_id` are absent. The separate App-less third-party Bot test remains, so this remediation does not weaken unrelated Bot coverage.

## Dimension Checks

- **Issue acceptance criteria:** checked, no issue. Managed Manager and Agent Bot authors are matched within the active workspace, cross-target authors are suppressed, and managed events return `ignored` before human-sender validation, authorization, routing, inbox admission, or work processing.
- **Coverage:** checked, no issue. The adapter tests cover sender classification, both supported author App-ID sources, optional Bot-user identity, source conflicts, receiver/author separation, raw-field filtering, missing supported identity contracts, transport forwarding, and ignored-result acknowledgement. Server specs cover Manager and Connection self/cross-target events, transition states, disabled Connections, invalid identity ordering, redelivery, no-side-effect behavior, unknown senders, humans, and third-party Bots.
- **Correctness:** checked, no issue. `SlackManagedBotAdmissionService` requires an exact workspace-scoped match to a non-deleted, identity-bearing Manager or Agent App and never uses the receiving App as author evidence. Both ingress routes perform stable identity, workspace, and lease checks before managed admission, while managed admission precedes ingress-specific work. The adapter acknowledges an ignored result once, does not render a user-facing response, and still drains normal deliveries.
- **Consistency with the surrounding codebase:** checked, no issue. The new metadata is allowlisted and additive, the Manager result preserves the existing `decision` field while exposing the adapter-compatible `kind`, and the new admission service follows the existing scoped-service and store conventions. The DTO extraction refactor builds cleanly.
- **Tests and verification:** checked, no issue. `npm --prefix packages/mohist-slack run test:ci` passed typechecking and 7 files / 76 tests. `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj -p:SkipWebBuild=true --no-restore` passed with 0 warnings and 0 errors. The Server spec command passed all 3,702 tests; its requested filter was ignored by Microsoft Testing Platform, so the complete assembly ran. `git diff --check` passed.

## Observations

- `packages/mohist-slack/src/adapter-events.ts:30` prefers a nested event `api_app_id` over the outer Socket payload value. Normal Slack Socket Mode payloads use the outer value and the exercised fixtures cover that shape, so this is not a must-fix for Issue 616. A future malformed-payload hardening change could fail closed or define precedence for conflicting receiving-App fields.
- Unrecognized explicit `senderKind` strings are not rejected at the Server boundary: Connection `NormalizeSenderKind` maps them to `Human` (`SlackConnectionRoutes.cs:1008-1015`), while Manager specifically rejects only the literal `unknown` value (`SlackManagerIngressService.cs:46-49`). Adapter-generated envelopes use the closed `human`/`bot`/`unknown` union, and the omitted-field fallback is intentional for legacy callers, so this does not block the valid Issue 616 contract. Failing closed for a non-null unrecognized value would be a useful protocol-hardening improvement.

<promise>PASS</promise>