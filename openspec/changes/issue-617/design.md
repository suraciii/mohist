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
- Construct equivalent contexts for Slack direct-message launches, channel-root launches, and follow-ups. A follow-up keeps the bound thread root and uses its own triggering message and dispatch operation.
- Validate context shape, required values, version, and digest before Runtime invocation. Invalid follow-ups must also be rejected before local input enqueue.
- Inject the Skill as managed execution-definition input and expose only the Server-provided anchor as Slack system facts.
- Prove initial/follow-up parity, replay stability, non-Slack exclusion, and fail-closed behavior with Server and Runner contract tests.

**Non-Goals:**

- Deterministically classifying natural-language questions or guaranteeing that a model follows the Skill.
- Adding Server-authored missing-reply detection, fallback response generation, or copying Runtime output into Slack.
- Changing Agent capabilities, persistent Instructions, Runtime, Model, configured Skills, Session persistence semantics, or public Slack delivery protocols.
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

`SlackExecutionContextFactory` is the only construction path. For an initial launch it consumes the trusted `ConnectionLaunchOrigin` and the pre-minted Session, input, and turn identities. For a follow-up it consumes the persisted Slack input provenance and the follow-up operation identity. A missing thread root means the triggering message is the root, which covers direct messages and channel-root mentions. A follow-up never recomputes the root from the follow-up text or Runtime state.

Initial launches will persist the context on the AgentJob input and copy it into the AgentJob dispatch payload. Follow-ups will rebuild the same versioned Skill from the Server catalog and derive the anchor from durable provenance before creating `FollowupParams`. Non-Slack paths pass no Slack context. The Server dispatch builders must reject or fail the dispatch if a trusted Slack origin/provenance cannot produce a complete context; they must not silently omit it and let the Runner treat it as ordinary work.

Reconstructing the anchor in the Runner was considered but rejected because it would move routing authority across the process boundary and make replay dependent on mutable Runtime state. Carrying only a conversation or thread id was rejected because it cannot identify the triggering input, Session, Connection, or dispatch operation needed for audit and idempotency.

### 3. Keep control data separate from Agent configuration and user text

The Runner will append the managed Skill to the resolved configured Skills for Slack execution only. The prompt will retain the existing user input and Agent Instructions. The execution envelope will contain:

- an execution-definition block holding Agent Instructions and resolved Skills, including the inline managed Slack Skill; and
- a system-facts block containing `source: slack`, context version, and the Server-provided reply anchor.

The Skill body is execution-definition input, not long-lived Agent configuration. The anchor is system-provided fact data, not a value the user can override or a destination selected by the Agent. Agent text, imported thread history, and Runtime output cannot replace it.

Embedding control data into the ordinary user prompt was considered but rejected because it weakens the system/user boundary and can make anchor fields look like user instructions. Persisting the Skill in every Agent definition was rejected because it duplicates configuration and would incorrectly affect Web, CLI, and Workflow execution.

### 4. Validate at both transport and execution boundaries

The Runner will share one parser/validator for initial and follow-up payloads. It will reject a non-object context, unsupported context version, missing or empty anchor fields, missing or empty Skill fields, a non-canonical digest, or a digest that does not match the exact supplied instruction bytes. Version 1 accepts only the published Slack Skill identity; a future protocol or anchor shape requires a context version change.

Validation will occur at the control dispatcher for malformed follow-up parameters and again at the execution entry points (`AgentJobExecutor` and the follow-up handler) before Runtime invocation. The follow-up handler must validate before `enqueueBeforeExecution`; the AgentJob path must return an invalid-dispatch/input result before selecting or invoking a Runtime. This duplicate boundary check protects both direct calls and JSON-RPC paths without making the Runtime understand Slack.

An absent context is valid only for a Server dispatch that is not Slack-originated. The Server's durable origin/provenance checks make omission from a Slack dispatch a construction failure rather than a source conversion. No validation path may use a malformed or incomplete Slack context as permission to continue as non-Slack work.

A signed or separately authenticated Skill payload was considered but is unnecessary for this change because the Server-to-Runner transport and embedded asset are already within the Mohist trust boundary. The digest provides integrity between context construction and validation; transport authentication remains the source-authenticity boundary.

### 5. Preserve reply authorship and delivery boundaries

The Skill tells the Agent to use the existing supplied send action and the anchor in system facts. The Server continues to own Slack delivery intents and liveness, while the Agent owns the reply body. No component will inspect a Runtime result to infer that a reply was intended, classify a direct question, or synthesize a response when the Agent sends nothing.

Adding a Server fallback was considered because it could improve visible response rates, but it would violate the documented silence rule, require unreliable question classification, and change reply authorship. It remains explicitly out of scope.

### 6. Test the contract at the owning boundaries

Server unit tests will lock the asset identity, six rule content, exact digest, context version, and anchor construction. Slack integration specs will cover DM initial launches, channel roots, thread follow-ups, replay equivalence, and absence of secrets or runner-selected destinations. Runner tests will cover malformed contexts, unsupported versions, empty required fields, changed instructions, lowercase hash validation, and non-Slack envelope preservation. Executor and follow-up tests will assert that invalid contexts do not invoke a Runtime or enqueue follow-up input.

A parity test will dispatch one initial input and one follow-up for the same Session and assert the same Skill identity, version, body, and digest with distinct correct anchors. The existing Slack reply action and outbox tests remain regression coverage rather than becoming part of this context contract.

## Risks / Trade-offs

- [Risk] Prompt instructions cannot deterministically force a model to answer a direct question, remain silent, or recover correctly. -> Mitigation: keep the six rules explicit and versioned as a visible Skill, preserve the existing send-action boundary, and avoid claiming delivery or behavioral guarantees that the system cannot enforce.
- [Risk] A changed embedded asset or newline/encoding transformation can invalidate the published digest. -> Mitigation: hash the exact embedded UTF-8 text at resolution time, compare it with the pinned version-to-content mapping, require lowercase hexadecimal output, and test that substituted bytes are rejected; change the Skill version and mapping for intentional content changes.
- [Risk] Missing or stale Slack provenance can produce an incomplete follow-up anchor. -> Mitigation: derive follow-ups only from durable provenance, reject incomplete contexts before enqueue or Runtime invocation, and cover missing-field and replay cases in Server specs.
- [Risk] Adding Slack control data to the common envelope can regress Web, CLI, or Workflow prompts. -> Mitigation: make Slack context optional, append the managed Skill only when resolved, preserve the existing no-Slack envelope path, and assert byte-level behavior for ordinary dispatches.
- [Risk] A fail-closed Runner rejection makes a Slack turn unavailable when the Server and Runner contracts are out of sync. -> Mitigation: deploy Runner support before Server injection, keep context version explicit, return actionable invalid/unavailable results, and monitor rejection diagnostics without logging anchor payloads or secrets.
- [Risk] Internal anchor identifiers could leak into a Slack reply if the model echoes system facts. -> Mitigation: provide only the minimum required facts, state the prohibition in the Skill, never include credentials, and retain existing Slack redaction/rendering tests.

## Migration Plan

This is a wire-contract and embedded-asset change, not a database schema migration. The new context fields are optional for existing non-Slack and legacy persisted records; existing Agent configuration and Session identities remain unchanged.

1. Add the immutable Skill asset, Server catalog, context contract, factory, and contract tests. Keep the published context version at `1` and make the six-rule body the only version-1 body.
2. Deploy Runner support first. It must understand the optional context, validate version 1, compose the managed Skill and system facts, and preserve the no-context path before any Server starts emitting Slack contexts.
3. Deploy Server context construction and dispatch wiring for both initial Slack launches and Slack follow-ups. New Slack dispatches must carry context; old Slack records without sufficient durable provenance fail closed rather than being upgraded from guesses.
4. Verify initial/follow-up parity, invalid-context rejection, non-Slack prompt preservation, and the absence of delivery or reply-authorship changes. Monitor validation failures and Runner availability without recording context contents.

Rollback is ordered in the reverse direction: first disable Server-side Slack context injection and stop creating new version-1 dispatches, then roll back Runner behavior after in-flight version-1 work has drained or been routed to a compatible Runner. Do not roll back only the Runner while Server is still emitting version-1 contexts. Reverting the embedded asset or contract without a versioned replacement is not a valid rollback because it could change the digest of already-created payloads.

## Open Questions

- No blocking design questions remain for version 1. The direct-question exception and model compliance remain intentionally instruction-level behavior rather than deterministic Server policy.
- A future change should decide whether a dedicated `invalid-slack-context` failure category is needed for operator metrics instead of reusing the existing invalid-input/unavailable results.
- If the Server-to-Runner trust boundary changes, revisit whether digest integrity is sufficient or whether the context needs a transport-authenticated signature.
