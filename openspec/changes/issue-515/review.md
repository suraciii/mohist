# Issue 515 Review

## Findings

### [P1] Concurrent launches can create multiple sessions for one Connection/thread

Location: `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1099-1100,1435-1457`;
`packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:203`.

The handler reads the thread binding before `LaunchChannelRootAsync`. If two distinct messages
mention the same previously unbound Bot in the same thread while those requests overlap, both reads
see no binding and both call `LaunchConnectionAsync`. The launch idempotency key contains
`MessageTs`, so the two messages create different AgentJobs/Sessions/initial inputs. The append-only
mapping `UpsertAsync` runs only afterward; the loser gets the first session id and restamps its inbox
route, but its already-created Job and Session remain and can still produce a terminal delivery.
This violates the one Agent+thread session boundary and T-002's exactly-one launch behavior. The
thread must be reserved/serialized before launching, or the losing message must be accepted as a
follow-up after the existing binding is established.

### [P1] Ambiguous messages can emit an extra owner rejection from another bound Connection

Location: `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1102-1116,1179-1193`.

Slack delivers the same ambiguous event to every mentioned or thread-bound Connection. With two
Connections that have different Owners, a message from Owner A in a multi-Agent thread is prompted
by Connection A, but Connection B enters the same ambiguous branch, fails its per-Connection Owner
check, and sends `This Slack Connection is available only to its owner.` instead of staying silent.
The symmetric multi-Bot mention has the same problem; a sender who owns neither Connection can receive
multiple rejection messages and no choose-one prompt. An ambiguous message must produce no Agent work
and at most the single choose-one prompt required by the attribution spec. Owner authorization needs
to be applied after global ambiguity handling, or non-winning Connections must ignore the event without
an outbox write. There is no regression test covering different owners in one workspace/thread.

### [P2] The documented implementation-gap section still says Slack is unimplemented

Location: `docs/agent-connections.md:385-390`.

The Slack usage section at line 307 now says channel/thread routing is available, but the document's
`## 实装差距` section still says “Agent 接入、mohist-slack 服务及以上 Slack 行为尚未实装”.
That contradicts the delivered channel-root launch, bound-thread follow-up, multi-Bot attribution,
and Owner-only behavior, and leaves T-003's required implementation-gap update incomplete.

## Verification

- `npm run typecheck -w packages/mohist-slack`: passed
- `npm run test:ci -w packages/mohist-slack`: 8/8 passed
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter FullyQualifiedName~Mohist.Server.SpecTests.Specs.Slack`: the filter is ignored by Microsoft Testing Platform; full suite passed 3541/3541

<promise>FAIL</promise>
