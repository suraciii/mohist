# Self Review

## Verdict
FAIL

This is the first review. I fetched issue 617 and read its goals, acceptance criteria, and non-goals before reviewing the proposal, design, tasks, and specs.

## Must-Fix Findings

### 1. An absent Slack context can still become ordinary non-Slack work

**Violates:** issue acceptance criterion 5; `specs/slack-skill-injection/spec.md` requirement and the "anchorless or incomplete context" scenario, which require a Slack-origin execution with missing context to be rejected rather than treated as non-Slack. It also violates the design's explicit constraint that a Slack execution must never degrade into non-Slack execution (`design.md:7-12, 68-74`).

The plan makes `slackExecutionContext` optional and defines an absent context as valid for non-Slack dispatches (`design.md:53, 74`; `tasks.json:T-002`, criteria 35-37). The shared validator can reject a malformed object, but it cannot distinguish a missing Slack context from a legitimate non-Slack dispatch. The current transport contract has an optional `FollowupParams.SlackExecutionContext` (`packages/server/src/Mohist.Server/Contracts/RunnerControlContracts.cs`), and the Runner's absent-context branch proceeds without Slack injection (`packages/runner/src/runtime/slack-execution-context.ts`, `readSlackExecutionContext`; `packages/runner/src/server/followup-handler.ts`). The follow-up target carries no independent source discriminator. The initial AgentJob input likewise retains the context but not an independent Slack-origin marker after the launch command is materialized.

Therefore, if a Slack context is omitted, dropped, or set to null, the described Runner checks have no way to fail closed; the request can invoke the Runtime as ordinary work. Saying that Server construction "must" reject omission is not enough unless the persisted/transported dispatch retains enough source information to enforce that invariant at every boundary. Add an explicit Slack/non-Slack discriminator or make the Slack dispatch contract structurally require the context, persist that source fact for AgentJob recovery, validate the source/context pair at the control dispatcher and execution entry points, and add tests for omitted/null context on both Slack initial and follow-up paths. Preserve the existing no-context behavior for genuinely non-Slack executions.

### 2. Batched Slack follow-ups have no defined complete anchor

**Violates:** issue acceptance criterion 1; `specs/slack-skill-injection/spec.md:17-22`, requiring every Slack follow-up to carry a complete anchor that preserves the bound thread root and identifies its triggering message; and `design.md:22-24, 51-53`, which promises equivalent follow-up contexts derived from durable provenance.

The current Session model can aggregate multiple queued inputs into one follow-up turn. `AgentSessionFollowupDispatch` exposes `InputTexts` as a list, and `AgentSessionGrain.BeginNextFollowupDispatchAsync` sets `InputId` and `Provenance` only when `turn.InputIds.Count == 1`; for multiple inputs both are null (`packages/server/src/Mohist.Server/Sessions/Domain/AgentSession.cs:508-523`; `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1054-1100`). Existing session specs explicitly cover turns with two input IDs (`packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionFollowupGrainSpecs.cs:117, 250, 645-688`). The planned parity test covers one initial input and one follow-up, so it does not exercise this valid Slack path.

With the planned current shape, a batched Slack follow-up either omits context or has no unambiguous triggering message. In addition, Slack input provenance stores the incoming `ThreadTs`; for a DM follow-up it is null, so simply applying the factory's "missing thread root becomes triggering message" rule does not preserve any bound root if the follow-up contract requires one. The plan must specify the behavior for a multi-input turn and for the DM bound-root case: for example, split dispatches, define one deterministic representative input and anchor for a combined invocation, or reject the combination before dispatch. Whichever contract is chosen must use durable identities, preserve the required root, and be covered by Server integration tests with multiple queued Slack inputs and a DM follow-up.

### 3. Same-version Skill immutability and initial/follow-up parity are not enforceable

**Violates:** issue acceptance criterion 4's version/content-digest contract and the immutable-payload requirement in `specs/slack-collaboration-skill/spec.md:1-9`; it also contradicts the plan's own immutable-version goals (`design.md:20-21, 38-42`; `tasks.json:T-001`, criteria 11 and 14).

The plan says the catalog computes the SHA-256 from the currently embedded asset and says follow-ups rebuild the Skill from that catalog (`design.md:40, 51-53`). It does not specify a pinned published digest/content mapping or a persisted Skill snapshot that follow-ups must reuse. A deployment can therefore change the embedded bytes while leaving `1.0.0` unchanged: an already persisted initial Job retains the old body/digest, while a later follow-up resolves the new body/digest. That directly breaks the promised same-version parity. A test that only computes `SHA-256(instructions)` and compares it with a digest returned by the same resolver proves self-consistency, but does not detect an unauthorized byte change under the same version.

Define an enforceable immutability mechanism: pin the canonical digest (and version-to-content mapping) so a changed asset fails validation unless the version changes, or persist and reuse the published Skill snapshot for all follow-ups. Add a regression test that changes or substitutes the asset under the existing version and proves it is rejected, plus a replay/initial-follow-up test that verifies the persisted version, body, and digest remain identical across the dispatch boundary.

## Dimension Verdicts

- **Issue goals and acceptance criteria:** checked after rereading the issue; must-fix gaps are listed above.
- **Coverage:** incomplete. The normal DM initial, channel-root initial, single-input follow-up, non-Slack, digest, and malformed-context cases are described, but missing-context source discrimination and valid batched follow-ups are not covered; same-version immutability is asserted but not operationalized.
- **Correctness:** the proposed envelope separation, Server-owned anchor, shared validation boundary, and reply-authorship boundary are directionally correct. The three cases above can produce behavior that violates the acceptance criteria, so the plan is not build-ready.
- **Current codebase consistency:** checked. The concerns are grounded in the existing optional `FollowupParams` contract, Runner absent-context branch, AgentJob context representation, and the Session grain's multi-input follow-up behavior. The planned use of the existing reply action and configured Skill resolution otherwise follows local boundaries.
- **Task breakdown, ordering, and verifiability:** ordering T-001 before T-002 is coherent and the stated test locations are useful. Completeness is insufficient until T-002 defines the source discriminator and batched-anchor contract, and T-001 defines a production-enforced version/content lock rather than only a computed-hash test.

## Observations

- The plan should add an explicit document-parity check or a clearly named six-rule test matrix. `docs/slack.md` is declared authoritative, but the proposed asset tests are described as canonical-content checks and do not say how drift between the document and embedded Skill is detected.
- The existing Slack documentation permits a system-authored fallback for an Agent crash or complete non-response, while the task wording says a missing Agent send action never becomes a Server-authored reply. Tests should scope that assertion to behavior introduced by this change so they do not accidentally redefine the existing delivery contract.
- The digest provides transport/content consistency within the stated Mohist trust boundary, not independent authenticity. The design acknowledges that boundary and explicitly declines signatures, so this is a documented trade-off rather than a must-fix for issue 617.
- T-002 combines Server contracts, launch persistence, follow-up routing, Runner parsing, two execution boundaries, deployment sequencing, and broad integration coverage. The scope is still understandable, but the revised task should name the source and multi-input decisions explicitly to keep implementation and review verifiable.

<promise>FAIL</promise>
