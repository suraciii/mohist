# Review

## Must-Fix Findings

### M-001: Manager ingress can admit an explicitly unknown event as human work

`packages/server/src/Mohist.Server/Api/SlackManagerIngressRoutes.cs:181-184` only permits a missing `senderSlackUserId` exception for `senderKind=bot`; it does not reject an explicit `senderKind=unknown` when a sender ID is present. More importantly, `packages/server/src/Mohist.Server/Slack/Services/SlackManagerIngressService.cs:58-69` never checks an explicit non-Bot sender kind before calling `SlackProviderInboxStore.AcceptAsync`. A request such as `senderKind: "unknown", senderSlackUserId: "U_UNKNOWN", isDirectMessage: true` therefore proceeds through Manager authorization and can create durable work-input state as if it were human input.

This violates the `Preserve existing non-managed Slack ingress behavior` criterion: unknown sender events must retain unknown-sender validation/ignore behavior and must not bypass required identity rules. Treat the explicit sender kind as authoritative at Manager ingress (while retaining the compatibility fallback only when the field is omitted), and add a regression asserting that an unknown event cannot create an inbox row or reach conversation processing.

### M-002: The required supported-fixture App-ID contract test is absent

`packages/mohist-slack/src/adapter-events.test.ts:404-414` tests a generic App-less Bot and explicitly accepts `authorBot.appId === null`. It does not construct a Manager- or Agent-shaped supported Mohist fixture with both App-ID sources missing and fail the contract test. Consequently, a regression that removes `event.app_id` and `bot_profile.app_id` from a supported fixture can pass the current suite, even though T-001 and the normalization acceptance criterion require that case to be a release-blocking contract failure.

Add explicit supported Manager and Agent fixture contract checks that require a matchable `authorAppId` from one of the two allowed sources. Keep the generic third-party App-less Bot test separate so preserving unrelated Bot behavior remains covered.

## Dimension Checks

- **Issue acceptance criteria:** FAIL. Managed-Bot attribution, early suppression, side-effect avoidance, identity propagation, and normal human/third-party paths are implemented, but M-001 violates the unknown-sender preservation criterion and M-002 leaves an explicit required contract test uncovered.
- **Coverage:** FAIL. The added Server and adapter tests cover the primary managed, lifecycle, conflict, redelivery, no-side-effect, and acknowledgement paths, but do not cover the two cases above.
- **Correctness:** FAIL because M-001 permits an explicit unknown classification to enter Manager durable admission.
- **Consistency with surrounding code:** checked, no issue. The shared admission service, additive DTO fields, workspace-scoped identity matching, and early Connection branch follow the existing service and ingress conventions.
- **Tests and verification:** checked. `npm --prefix packages/mohist-slack run test` passed 7 files and 75 tests; adapter test typecheck passed; the Server build passed; the Server spec run passed all 3,701 tests; and `git diff --check` passed. These results do not cover the missing cases above.

## Observations

- `packages/mohist-slack/src/adapter-events.ts:30` prefers `event.api_app_id` over the outer `api_app_id`. Normal Slack Socket Mode payloads use the outer value, and the current tests cover that shape, but a conflicting duplicate would not preserve the outer receiving identity as unambiguously as the design describes. This is not counted as a must-fix for the current issue because the duplicate is outside the exercised Slack payload shape.
- The focused `dotnet test` filter was ignored by the repository's Microsoft Testing Platform runner, so the command executed the complete Server specification assembly rather than only the two new classes. The broader result was successful.

<promise>FAIL</promise>
