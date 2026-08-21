# Self-Review: Issue 618 Plan Artifacts (Round 1)

This is the first review of the plan. I read the canonical issue body and acceptance criteria before reviewing `proposal.md`, `design.md`, `tasks.json`, and all four capability specs. I also checked the current Server, CLI, Runner-contract, Slack outbox, liveness, Manager ingress, and Manager test surfaces.

## Must-Fix Findings

### MF-1. The Manager command bridge has no non-overlapping boundary for the required reply action

**Evidence:**

- `specs/manager-command-capability/spec.md:2` and `tasks.json` T-003 require the Manager capability to expose exactly nine logical operations and reject every operation outside that allowlist.
- `design.md:80-81` says the Manager execution bridge constrains requests to that allowlist and rejects unknown command names, arbitrary endpoints, and other unrestricted access.
- `design.md:100-106`, `specs/manager-slack-reply-liveness/spec.md:2-6`, and T-005 simultaneously require the Agent to publish the final conversational response through `mo slack message send`.
- The artifacts never state whether `mo slack message send` is outside the management capability, nor do they define a separate reply capability, its authorization boundary, or how its Manager grant/Session/dispatch metadata reaches the reply endpoint. The current CLI registers `message send` as a normal `mo slack` command (`packages/cli/Mohist.Cli/MohistCliCommands.Slack.cs:1001-1098`), so an implementation that applies the T-003 bridge filter to all `mo` invocations will reject the required reply. An implementation that exempts it inside the same bridge violates the stated exact allowlist.

This violates issue acceptance criterion 1, which requires the final natural-language result to be published by the Agent through the reply action, and criterion 7, which requires the capability allowlist to exclude unrestricted operations. It also leaves the core security and authorship contract ambiguous: there is no authoritative answer to which capability authorizes the reply, which request fields are trusted from the injected anchor, or which Manager outbox owner and per-input dispatch identity are used.

The plan must explicitly separate the management allowlist from the reply-action capability, or define a precise exception with equivalent non-management authorization. It must specify the exact request/argument mapping for the nine management operations, the reply transport and grant validation, Manager-owned outbox routing, and per-input idempotency. Add tests proving that `mo slack message send` succeeds only for the current Manager anchor while every other `mo` command and arbitrary route remains denied.

## Dimension Verdicts

- **Issue goals and acceptance criteria:** checked first, with one must-fix conflict recorded above.
- **Coverage:** issue found. The plan covers the visible behaviors, but the required Agent reply and strict capability exclusion do not have a complete, non-conflicting contract.
- **Correctness:** issue found. The two literal interpretations of the bridge produce opposite acceptance-criteria failures.
- **Current codebase consistency:** issue found for the same boundary. The existing CLI has a general `message send` command and the existing reply API is separate from the Manager outbox owner, but the plan does not define how that separation is preserved under the new bridge.
- **Task breakdown, ordering, and verifiability:** checked. The dependency graph is acyclic and the five tasks cover all four specs, but T-003 and T-005 need the boundary and contract additions above before implementation is verifiable.

## Observations

1. The current `SlackManagerConversationService` still catches `RuntimeSessionMissingException` and launches `ReplacementManagerSessionId` (`packages/server/src/Mohist.Server/Slack/Services/SlackManagerConversationService.cs:84-91,145-151`). That would create a second Session in one DM. The plan's T-004 acceptance criteria and `design.md:127` correctly prohibit parallel Sessions, so implementation must remove this fallback or reconcile the canonical Session instead of following the current path.

2. The current reply store hardcodes `OwnerKind = connection` for a newly created reply and deduplicates primarily by connection/conversation/thread (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.cs:299-412,658-704`). T-005's per-input promise requires a Manager enrollment owner and a dispatch/input-scoped identity, especially for multiple sequential turns in one DM. The design states the behavioral outcome but does not name this required store-key change.

3. Follow-up terminal events currently carry `messageTs = null` and only the Session's thread label (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:3881-3933`). With Manager progress represented only by reactions, terminal finalization cannot rely on a replaceable progress row to recover the current triggering message. The design says the terminal handler resolves Manager Session/source facts (`design.md:127 and Decision 6`), so T-005 should test that lookup explicitly, including absent progress, restart, and redelivery; otherwise the projector can target the initial root or the handler's synthetic `terminal:<job>` identity.

4. The exact Runner-to-CLI transport remains an open question (`design.md:149-153`). The stated invariant and leakage tests are useful and keep this from being a separate must-fix, but the selected transport must be fixed before implementation because ordinary CLI bearer authentication and general shell execution are different trust boundaries.

5. The current `SlackStatusProjection` still creates a `Working...` progress message and reaction fallbacks (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackStatusProjection.cs:17-68`). T-005 explicitly requires Manager reaction-only acceptance/progress, so its tests must prove that Manager requests do not leave these ordinary text fallback rows even when liveness is retried.

<promise>FAIL</promise>
