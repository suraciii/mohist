## Context

Issue 617 adds a Slack-specific collaboration contract to Agent execution. Slack ingress already creates durable Session inputs and preserves provider provenance, while initial launches and follow-ups use separate Server-to-Runner paths. The implementation must make those paths carry the same managed Skill and a Server-selected reply anchor without changing the Agent's persistent definition or Slack delivery ownership.

The Server is the authority for Slack workspace, conversation, thread, member, Connection, Session, and dispatch identities. The Runner is responsible for validating dispatch input and composing the Runtime prompt. `docs/slack.md` is the behavioral source of truth: it defines the six rules for useful replies, direct-question answers, silence, delegation callbacks, self-contained conclusions, anchor use, and silent recovery.

The main constraints are:

- The Skill payload is immutable for a published version: the catalog pins a version-to-content digest mapping and rejects embedded bytes that drift from it; the published digest is the SHA-256 of the exact UTF-8 instruction bytes.
- Every Slack initial launch and follow-up must carry a complete context; it must never degrade into a non-Slack execution when context is missing or malformed.
- Non-Slack execution must retain its existing prompt, configured Skills, and envelope behavior.
- Reply authorship remains with the Agent's existing `mo slack message send` action. Runtime output, progress, and missing sends are not converted into Slack messages by this change.

Stakeholders are Slack users and Agent authors, the Server and Runner maintainers, and operators who need invalid execution context to fail visibly and safely.

## Goals / Non-Goals

**Goals:**

- Publish `mohist-slack-collaboration` version `1.0.0` as one embedded Server asset containing all six collaboration rules.
- Publish the Skill name, version, instructions, and lowercase SHA-256 digest as one immutable payload, with a production-enforced version-to-content lock.
- Define a versioned Slack execution context containing the Skill and a complete reply anchor with workspace, conversation, thread root, triggering message, initiating member, Connection, Session, and dispatch identities.
- Construct equivalent contexts for Slack direct-message launches, channel-root launches, and follow-ups. A follow-up keeps the durable bound thread root, including the initial DM message, and uses its own durable triggering message and dispatch operation; a batched turn uses its first durable input as its deterministic representative trigger.
- Validate context shape, required values, version, and digest before Runtime invocation. Invalid follow-ups must also be rejected before local input enqueue.
- Inject the Skill as managed execution-definition input and expose only the Server-provided anchor as Slack system facts.
- Prove initial/follow-up parity, replay stability, non-Slack exclusion, and fail-closed behavior with Server and Runner contract tests.

**Non-Goals:**

- Deterministically classifying natural-language questions or guaranteeing that a model follows the Skill.
- Adding Server-authored missing-reply detection, fallback response generation, or copying Runtime output into Slack.
- Changing Agent capabilities, persistent Instructions, Runtime, Model, configured Skills, Session queue/turn semantics, or public Slack delivery protocols. The append-only source/root provenance needed to enforce this contract is in scope.
- Changing outbox ownership, liveness projection, thread mapping, reply authorization, or Slack rendering.
- Introducing a new DSL or a user-configurable version of the collaboration rules.

## Decisions

### 1. Use one embedded, versioned Server asset as the canonical Skill

The Skill body will live in the Server's embedded asset set and be resolved through a small catalog with the fixed identity `mohist-slack-collaboration` / `1.0.0`. The catalog owns an explicit version-to-content table; version `1.0.0` is pinned to the canonical digest `de3272639a1d390f3dcf915e65b6c057bf0b9eb91c51545572eb1e484c8c1a22`. Resolution reads the exact embedded text, hashes its exact UTF-8 bytes with SHA-256, and fails closed if the computed digest differs from the pinned mapping. It returns the pinned identity, body, and lowercase hexadecimal digest together. A wording or byte change therefore fails asset resolution until a new published version and mapping are added; it cannot silently change an existing payload.

This keeps the behavioral contract independent of a Runner's local filesystem, user-installed Skills, or a particular Runtime. A user-installed Skill was considered but rejected because it is optional, mutable, and not Server-controlled. Hardcoding the rules in the Runner was also rejected because it would duplicate the product source of truth and make Server/Runner releases drift.

### 2. Carry a versioned context with an immutable anchor and Skill snapshot

The Server contract will use a version-1 `AgentSlackExecutionContext` containing:

- `SlackReplyAnchor`: `workspaceId`, `conversationId`, `threadRootMessageId`, `triggeringMessageId`, `initiatingMemberId`, `connectionId`, `sessionId`, and `dispatchRef`.
- `SlackCollaborationSkill`: `name`, `version`, `instructions`, and `contentHash`.

Every initial AgentJob dispatch and follow-up transport also carries a required `executionSource` discriminator with exactly `slack` or `non-slack`. The Server persists that source on the AgentJob input and on the follow-up/session dispatch state. `slack` requires a complete context; `non-slack` requires no Slack context. The discriminator is control data and is not user text.

`SlackExecutionContextFactory` is the only construction path. For an initial launch it consumes the trusted `ConnectionLaunchOrigin` and the pre-minted Session, input, and turn identities. The effective bound thread root is `origin.ThreadTs ?? origin.MessageTs`, so a DM and a channel-root launch both persist their initial message as the root. That effective root is retained in durable Session provenance/state for later follow-ups. For a follow-up it consumes the durable Slack source, the persisted bound root, the representative input's durable message provenance, and the follow-up operation identity. A missing persisted root or any other required provenance is a construction failure; the factory never falls back to the current text or a nullable incoming `ThreadTs`.

A follow-up turn may contain multiple queued inputs under the existing Session batching behavior. The representative is the first `InputId` in the persisted `turn.InputIds` order. Its durable Slack `MessageId` becomes `triggeringMessageId`, the turn's operation becomes `dispatchRef`, and every invocation still retains the joined input texts. This gives one combined invocation one deterministic triggering message while preserving the bound root; if the representative or root cannot be resolved, the Server fails the dispatch before sending it to the Runner. Splitting or guessing a destination is not permitted.

Initial launches persist the complete context on the AgentJob input and copy it into the AgentJob dispatch payload. Follow-ups use the catalog's pinned published Skill mapping, never a newly computed digest from mutable bytes, and construct a fresh anchor from the durable source/root/provenance before creating `FollowupParams`. The initial persisted Skill snapshot and every follow-up payload must therefore match the locked published name, version, body, and digest. Non-Slack paths carry the explicit non-Slack source and no Slack context. Server dispatch builders must reject or fail the dispatch when the source/context pair or trusted Slack origin/provenance is incomplete; they must not silently omit context and let the Runner treat Slack work as ordinary work.

Reconstructing the anchor in the Runner was considered and rejected because it would move routing authority across the process boundary and make replay dependent on mutable Runtime state. Carrying only a conversation or nullable incoming thread id was rejected because it cannot identify the triggering input or preserve a DM's bound root, Session, Connection, and dispatch operation needed for audit and idempotency.

### 3. Keep control data separate from Agent configuration and user text

The Runner will append the managed Skill to the resolved configured Skills for Slack execution only. The prompt will retain the existing user input and Agent Instructions. The execution envelope will contain:

- an execution-definition block holding Agent Instructions and resolved Skills, including the inline managed Slack Skill; and
- a system-facts block containing `source: slack`, context version, and the Server-provided reply anchor.

The transport-level `executionSource` discriminator is validated before composition. It is not exposed as a user-controlled instruction; valid Slack executions expose only the existing Slack system facts.

The Skill body is execution-definition input, not long-lived Agent configuration. The anchor is system-provided fact data, not a value the user can override or a destination selected by the Agent. Agent text, imported thread history, and Runtime output cannot replace it.

Embedding control data into the ordinary user prompt was considered but rejected because it weakens the system/user boundary and can make anchor fields look like user instructions. Persisting the Skill in every Agent definition was rejected because it duplicates configuration and would incorrectly affect Web, CLI, and Workflow execution.

### 4. Validate at both transport and execution boundaries

The Runner will share one parser/validator for initial and follow-up payloads. It will validate the required `executionSource` discriminator together with the context: `slack` requires a present, valid context, while `non-slack` requires an absent context. An omitted/unknown source, a Slack source with an omitted or null context, or a non-Slack source carrying Slack context is invalid. The validator will also reject a non-object context, unsupported context version, missing or empty anchor fields, missing or empty Skill fields, a non-canonical digest, or a digest that does not match the exact supplied instruction bytes. Version 1 accepts only the published Slack Skill identity; a future protocol or anchor shape requires a context version change.

Validation will occur at the Runner control dispatcher for malformed follow-up parameters and source/context mismatches, then again at the execution entry points (`AgentJobExecutor` and the follow-up handler) before Runtime invocation. The follow-up handler must validate before `enqueueBeforeExecution`; the AgentJob path must return an invalid-dispatch/input result before selecting or invoking a Runtime. This duplicate boundary check protects both direct calls and JSON-RPC paths without making the Runtime understand Slack.

The Server must materialize the source discriminator even when recovering a legacy record. It may classify a legacy record as non-Slack only from trusted durable non-Slack source data; a record with Slack ConnectionOrigin, Slack Session source, or Slack provenance and no complete context fails closed. No validation path may use a missing or malformed Slack context as permission to continue as non-Slack work.

A signed or separately authenticated Skill payload was considered but is unnecessary for this change because the Server-to-Runner transport and embedded asset are already within the Mohist trust boundary. The digest provides integrity between context construction and validation; transport authentication remains the source-authenticity boundary.

### 5. Preserve reply authorship and delivery boundaries

The Skill tells the Agent to use the existing supplied send action and the anchor in system facts. The Server continues to own Slack delivery intents and liveness, while the Agent owns the reply body. No component will inspect a Runtime result to infer that a reply was intended, classify a direct question, or synthesize a response when the Agent sends nothing.

Adding a Server fallback was considered because it could improve visible response rates, but it would violate the documented silence rule, require unreliable question classification, and change reply authorship. It remains explicitly out of scope.

### 6. Test the contract at the owning boundaries

Server unit tests will lock the asset identity, pinned v1 digest, six rule content, exact digest, and context version. A catalog test will substitute the embedded body under version `1.0.0` and assert resolution fails. Slack integration specs will cover DM initial launches, channel roots, thread follow-ups, durable DM roots, multi-input representative anchors, replay equivalence, source/context omission, and absence of secrets or Runner-selected destinations. Runner tests will cover source/context pair mismatches, malformed contexts, unsupported versions, empty required fields, changed instructions, lowercase hash validation, and non-Slack envelope preservation. Executor and follow-up tests will assert that invalid Slack source/context pairs do not invoke a Runtime or enqueue follow-up input.

A parity test will dispatch one initial input and one follow-up for the same Session and assert the same Skill identity, version, exact body, and digest after the initial snapshot crosses the AgentJob boundary and the follow-up resolves the pinned catalog, with distinct correct anchors. A separate batched follow-up test will assert first-durable-input representative selection and bound-root preservation. The existing Slack reply action and outbox tests remain regression coverage rather than becoming part of this context contract.

## Risks / Trade-offs

- [Risk] Prompt instructions cannot deterministically force a model to answer a direct question, remain silent, or recover correctly. -> Mitigation: keep the six rules explicit and versioned as a visible Skill, preserve the existing send-action boundary, and avoid claiming delivery or behavioral guarantees that the system cannot enforce.
- [Risk] A changed embedded asset or newline/encoding transformation can invalidate the published digest. -> Mitigation: hash the exact embedded UTF-8 text at resolution time, compare it with the pinned version-to-content mapping, require lowercase hexadecimal output, and test that substituted bytes are rejected; change the Skill version and mapping for intentional content changes.
- [Risk] Missing or stale Slack provenance can produce an incomplete follow-up anchor, especially when a DM has no incoming thread timestamp or several inputs share one turn. -> Mitigation: persist the effective initial root, select the first durable input as the batched representative, reject missing source/root/provenance before dispatch, and cover DM and multi-input cases in Server specs.
- [Risk] Adding Slack control data to the common envelope can regress Web, CLI, or Workflow prompts. -> Mitigation: make the execution source explicit, require the source/context pair, append the managed Skill only for `slack`, preserve the explicit non-Slack no-context path, and assert byte-level behavior for ordinary dispatches.
- [Risk] A fail-closed Runner rejection makes a Slack turn unavailable when the Server and Runner contracts are out of sync. -> Mitigation: deploy Runner support before Server injection, keep context version explicit, return actionable invalid/unavailable results, and monitor rejection diagnostics without logging anchor payloads or secrets.
- [Risk] Internal anchor identifiers could leak into a Slack reply if the model echoes system facts. -> Mitigation: provide only the minimum required facts, state the prohibition in the Skill, never include credentials, and retain existing Slack redaction/rendering tests.

## Migration Plan

This is a wire-contract and embedded-asset change, not a relational database schema migration. The append-only source discriminator, effective bound-root provenance, and context fields live with existing AgentJob/Session durable state; existing Agent definitions and Session queue/turn semantics remain unchanged.

1. Add the locked Skill asset/catalog, context contract, source discriminator, bound-root provenance, factory, and contract tests. Keep context version `1` and make the six-rule body the only version-1 body.
2. Deploy Runner support first. It must validate the explicit source/context pair, reject Slack omission/null and non-Slack contamination, compose the managed Skill and system facts, and preserve the explicit non-Slack no-context path before Server emits new Slack contexts.
3. Deploy Server source/context construction and dispatch wiring for initial Slack launches and Slack follow-ups. Every new dispatch carries the source marker. Server materializes the marker for legacy records only from trusted durable origin data; records that prove Slack origin but lack a complete context, bound root, or representative provenance fail closed rather than being upgraded from guesses.
4. Verify initial/follow-up parity, replay stability, DM and batched anchors, source/context rejection, non-Slack prompt preservation, and the absence of delivery or reply-authorship changes. Monitor validation failures and Runner availability without recording context contents.

Rollback is ordered in the reverse direction: first disable Server-side Slack context injection and stop creating new version-1 dispatches, then roll back Runner behavior after in-flight version-1 work has drained or been routed to a compatible Runner. Do not roll back only the Runner while Server is still emitting version-1 contexts. Reverting the embedded asset or contract without a versioned replacement is not a valid rollback because it could change the digest of already-created payloads.

## Open Questions

- No blocking design questions remain for version 1. The direct-question exception and model compliance remain intentionally instruction-level behavior rather than deterministic Server policy.
- A future change should decide whether a dedicated `invalid-slack-context` failure category is needed for operator metrics instead of reusing the existing invalid-input/unavailable results.
- If the Server-to-Runner trust boundary changes, revisit whether digest integrity is sufficient or whether the context needs a transport-authenticated signature.
