## Context

Issue 617 updates the executable Slack collaboration contract so that it matches the
Slack behavior already documented by the product. The current contract is an
embedded Markdown asset at
`packages/server/src/Mohist.Server/Agent/Services/Assets/mohist-slack-collaboration.skill.md`.
The Server embeds this asset, resolves it through
`SlackCollaborationSkillCatalog`, and computes a lower-case SHA-256 hash over the
exact UTF-8 instructions. Each Slack launch or follow-up carries that Skill,
its version and hash, plus a Server-selected `SlackReplyAnchor`.

The Runner currently validates the versioned context and anchor before invoking
an Agent runtime, then inlines the Skill and exposes only the anchor as Slack
system facts. Normal Web, CLI, and Workflow envelopes do not receive these
facts. Session input provenance and the existing Slack startup context preserve
the workspace, conversation, thread and initiating message across follow-ups
and recovery.

The asset already requires explicit Slack reply actions, self-contained result
messages, no empty acknowledgements, delegator mentions, and use of the
Server-provided destination. Its silence rule does not yet make a direct human
question an exception, and its recovery rule is not explicit enough about
rebuilding state without interruption or recovery narration. It also does not
preserve every qualifier in the documented collaboration-rules section: the
restriction to mention people only when they need to act or notice the result,
and the rule that fine-grained progress belongs in the Web session timeline.
The change is therefore an embedded contract update with contract-test coverage,
not a new Slack delivery or persistence subsystem.

## Goals / Non-Goals

**Goals:**

- Make a direct human question require an explicit, useful Slack reply, even
  when the Agent has no additional information. A concise statement that there
  is nothing additional to add is valid; an acknowledgement-only message is
  not.
- Preserve silence for non-question turns that produce no conclusion, result,
  failure reason, or next step.
- Define recovery precedence: restore state from durable Session records and
  the available Slack thread/startup context, continue with the existing
  Session and reply anchor, and do not announce the restart, recovery, or
  compaction. An unanswered direct question still requires a reply.
- Keep reply authorship with the Agent's explicit Slack reply action and keep
  destination selection with the Server-provided anchor.
- Keep the injected Skill in one-to-one correspondence with all six ordered
  rules in `docs/slack.md#slack-collaboration-rules-for-agents`, including the
  audience-appropriate mention restriction and the Web-session-timeline rule
  for fine-grained progress.
- Version the changed Skill, publish its recomputed content hash, and verify
  the asset identity, body, and hash at the Server/Runner contract boundary.
- Keep the collaboration Skill scoped to Slack and preserve existing non-Slack
  execution envelopes.

**Non-Goals:**

- Adding a deterministic natural-language question classifier or a
  Server-generated fallback reply. The Agent applies the collaboration rule
  using the conversation and recovered context.
- Changing Slack outbox delivery, reply authorization, liveness projection,
  duplicate reconciliation, thread mapping, or the explicit reply action.
- Introducing a database schema, public API, Agent definition, credential
  flow, or new recovery store.
- Changing the AgentSession recovery state machine, runtime compaction
  implementation, or the way thread history is fetched and bounded.
- Applying Slack instructions or reply anchors to Web, CLI, or Workflow
  executions.

## Decisions

### 1. Keep one Server-owned embedded Skill as the executable source of truth

Update the existing managed Markdown asset in place and bump its Skill asset
version from `1.0.0` to `1.1.0`. The catalog will continue to resolve the
embedded resource at runtime and compute the lower-case SHA-256 from the exact
instruction bytes; no manually copied hash is introduced. The updated text
will state the response priority explicitly:

1. A direct human question always gets a useful reply through the explicit
   Slack action.
2. A conclusion, result, failure reason, or required next step gets a reply.
3. A non-question with no new information may end silently.
4. Recovery is internal: reconstruct state first, then apply the rules above;
   recovery itself never creates a status announcement.

The updated asset will retain all six documented rules as six corresponding
instruction blocks in the same order, without dropping qualifiers. In
particular, it will say that a person is mentioned only when they need to act or
notice the result, that a narrative reference needs no mention, and that
fine-grained progress belongs in the Web session timeline. This keeps the
contract inspectable and evolvable while ensuring every Slack execution receives
the same rules. Hard-coding the rules in the Runner or copying them into the
Slack adapter was rejected because it would duplicate behavior, bypass normal
Skill injection, and make the two execution paths drift. Updating only
documentation was rejected because it would not change Agent behavior.

### 2. Treat direct-question and recovery rules as ordered exceptions to silence

The Skill will explain that silence is valid only after checking whether the
turn contains a direct question or useful result. The direct-question rule
wins over both ordinary silence and recovery silence. When there is no new
information, the reply must communicate that fact and, where applicable, the
next known state or action; it must not be only `got it`, `understood`, or
`confirmed`.

The Skill will also tell the Agent to use the durable Session transcript,
accepted input provenance, and the available Slack thread/startup context to
reconstruct work after restart, Session recovery, or context compaction. It
will not add an interruption preamble or ask the human to restate the task
merely because process state was lost. A Server-side question classifier or a
system-generated "recovered" message was rejected because it would require
duplicating conversation semantics and would violate Agent-owned reply
authorship.

### 3. Preserve the context wire version and strengthen identity validation

`AgentSlackExecutionContext.CurrentVersion` remains `1` because the serialized
shape, anchor fields, and dispatch scope do not change. Only the embedded Skill
body and Skill asset version change. The Runner will continue to reject an
invalid Slack context before runtime invocation, checking:

- context version and all non-empty reply-anchor fields;
- exact Skill name `mohist-slack-collaboration` and a non-empty asset version;
- non-empty instructions; and
- a lower-case SHA-256 equal to the UTF-8 digest of those exact instructions.

The Runner will inline the validated body and expose only the anchor in
`[mohist-system-facts]`. It will not hard-code the content hash in the Runner;
the Server is the asset authority and the hash binds the published metadata to
the delivered body. Bumping the wire version or pinning a single hash in every
Runner release was rejected because neither is required by the shape change and
both would make rolling deployment and rollback unnecessarily brittle.

### 4. Reuse the existing Slack dispatch injection points

The Server will continue constructing the context at root launch and follow-up
boundaries from Server-owned provenance. `AgentJobGrain` will persist the
context with a launch plan, and `AgentSessionFollowupDispatcher` will rebuild
it from persisted Slack provenance for each follow-up, preserving the existing
Session and reply target while using the current dispatch reference.

`agent-job-executor`, `followup-handler`, and `buildExecutionEnvelope` will keep
using the existing helper path: resolve normal Agent Skills, append the
validated managed Slack Skill only for a resolved Slack context, and add Slack
system facts only in that case. This avoids changing unrelated execution
behavior and keeps direct replies routed through the existing Slack action and
outbox.

### 5. Expand contract tests around the asset and boundary

Server contract tests will continue deriving the expected hash from the
resolved embedded asset and will assert the new version and required language
for direct questions, useful no-additional-information replies, recovery
silence, explicit reply action, anchor use, self-contained results, and no
empty acknowledgements. In addition, the asset test will use an explicit,
ordered six-entry rule checklist matching every bullet in
`docs/slack.md#slack-collaboration-rules-for-agents`: speaker/useful conclusion
or valid silence; no empty acknowledgements with the direct-question exception;
delegated-result callback plus mention only when someone needs to act or notice
the result; self-contained/proportionate replies with fine-grained progress in
the Web session timeline; the Server-provided reply anchor; and silent resume
from durable state and the thread. The checklist must fail if any documented
rule or qualifier is absent. The tests must not assert one exact prose answer.

Runner tests will cover a valid v1 context with the updated Skill, mismatched
hash, wrong Skill name, incomplete anchor/Skill metadata, and rejection before
runtime invocation. Envelope tests will continue proving that Slack facts and
the managed Skill are present for Slack dispatches while a normal dispatch
remains unchanged. Existing Slack/session specs remain the place to verify
that root, follow-up, provenance and Server-selected reply targets are not
altered.

## Risks / Trade-offs

- [Risk] An Agent may misclassify a direct question and remain silent. -> The
  Skill gives direct questions explicit precedence over silence, contract tests
  lock the wording and action requirement, and no generic acknowledgement is
  accepted as a fallback.
- [Risk] A "nothing additional" answer may degrade into an empty
  acknowledgement. -> Require a substantive statement about the information
  state or next action and retain negative tests for acknowledgement-only
  content in the asset contract.
- [Risk] A restart could produce duplicate or misrouted replies. -> Reuse the
  durable Session input identity, Server-generated provenance, dispatch
  reference, existing outbox reconciliation, and the same reply anchor; the
  Skill must never select a destination from memory.
- [Risk] Newlines or build encoding changes could alter the asset hash. -> Read
  the embedded resource rather than a checkout path, hash the exact UTF-8 body
  in the catalog, and assert the computed hash in Server tests and dispatch
  validation tests.
- [Risk] Malformed or tampered Slack context could invoke an Agent with an
  untrusted Skill body. -> Validate version, exact Skill identity, required
  fields, and body hash before runtime invocation; fail the dispatch closed.
- [Risk] A Slack-specific Skill could leak into another execution envelope. ->
  Keep the existing resolved-context branch as the only injection path and
  assert byte-level preservation of the normal envelope in Runner tests.
- [Risk] A recovery path may lack enough durable or thread context to answer
  correctly. -> Do not fabricate an answer or recovery narration; use the
  existing recovery failure policy, report a concrete failure/required action
  when one is known, and preserve the direct-question obligation whenever the
  question remains available.

## Migration Plan

1. Update the embedded Skill Markdown and change the catalog asset version to
   `1.1.0`. Build the Server so the new resource is embedded and its hash is
   recomputed from the final bytes.
2. Add/update Server contract tests and Runner context/envelope tests, including
   the six-rule docs-parity checklist, malformed-context rejection, and
   non-Slack invariance. Run the focused Server and Runner suites before
   deployment.
3. Deploy the Server and Runner normally. The context wire version stays at v1,
   so old and new binaries can exchange the same shape during a rolling
   deployment. The Skill version and hash travel with each Slack dispatch;
   queued work does not require a data migration.
4. Verify a known-answer direct question, a direct question with no additional
   information, a non-question acknowledgement, and a recovered Slack turn.
   Confirm that the first, second, and recovered question produce Agent-authored
   replies, while the non-informational acknowledgement remains silent. For the
   recovered question, confirm the first reply contains no interruption or
   recovery narration.
5. Roll back by redeploying the previous embedded asset and catalog version.
   No database rollback is needed. Existing v1 contexts remain structurally
   compatible, and any newly dispatched context uses whichever asset the
   Server currently publishes.

## Open Questions

- What exact concise phrasing should the Agent prefer for the
  no-additional-information case? The design intentionally leaves wording
  flexible as long as the reply is useful and not acknowledgement-only.
- Should future telemetry distinguish a direct-question reply from a normal
  result reply? This is not needed for the contract and is deferred so no
  provider or execution schema changes are introduced.
- Should a future context version carry an explicit turn classification or
  recovery marker? The current design keeps both as Agent-level collaboration
  semantics and avoids adding fields until a deterministic producer and
  consumer contract exists.
