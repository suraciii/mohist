# Agent Operability

Mohist already provides the software factory: Issues carry requirements,
Workflows move work through stages, Runners execute facts, Workspaces hold the
working state, and people supervise delivery. Agent Operability makes that
existing factory usable by Agents as a first-class operating surface. It does
not replace the Issue or Workflow model and does not create a second task
system.

The capability serves two callers. A configured Mohist Agent is a durable
Mohist resource that can be launched, continued, supervised, and given Skills.
An External Agent is a caller that uses Mohist's documented Skill, CLI, or API
from an existing interaction location; it operates Mohist resources but does
not become a Mohist Agent resource.

Agent Native principles constrain this capability: the Agent must discover
what it can do, read authoritative facts, act within authorization, surface
uncertainty, and return evidence-backed results. Those principles describe how
the Agent operates the product; they are not a replacement for Mohist's
existing delivery features.

## Product Commitments

- **The existing factory remains authoritative:** Agents use the same Project,
  Issue, Workflow, Run, Approval, and evidence model as people and other
  entry points.
- **The existing CLI is enough:** Agents use the same commands as people; this
  capability does not introduce an Agent-only command language.
- **Reads precede writes:** An Agent can read current state, blockers, and the
  next valid action before it mutates or launches work.
- **Authorization is enforced by the product:** Instructions, prompts, and
  Skills explain how to act but never grant authority or bypass invariants.
- **Important writes are recoverable:** Existing asynchronous writes have a
  durable identity and idempotent retry behavior.
- **Confirmation is exceptional:** Only a small set of high-risk operations
  require explicit human confirmation; ordinary workflow actions do not carry
  a generic actor gate.
- **Results return to the originating context:** An Agent can summarize a
  result for Slack, an IDE, or another External Agent while preserving source,
  scope, revision, and evidence.

## Discover Agent Capability

An Agent begins by determining whether Mohist is appropriate for the request.
The existing Skill, CLI help, and API documentation must make these facts
available:

- the work Mohist can and cannot perform;
- the Project, repository, Agent, and permissions required;
- the supported operations and their input contracts;
- the states and evidence the caller can read;
- actions reserved for a person; and
- the recovery path when a response is lost or an outcome is unknown.

This is documentation and help-text work for the first iteration, not a new
catalog system. Structured capability metadata can be added later if real
usage shows that help and Skills are insufficient.

Discovery is not authorization. A Skill may describe an operation, but the
Server still checks the caller, Project grant, resource state, and domain
invariants at the operation boundary. An Agent must decline or redirect work
outside the supported capability instead of manufacturing an Issue or
permission.

## CLI as an Agent Operating Surface

The `mo` CLI is the shared operating surface for people and Agents. Agent
Operability must improve this surface without creating an Agent-only command
language or a second task model. Skills decide which operation is appropriate;
the CLI supplies exact syntax, current fields, authorization errors, and
recovery identities.

### Capability discovery

`mo --help`, group help, leaf help, and `mo skill list/view` remain the
discovery path. First make those existing surfaces complete and consistent;
do not add a metadata catalog, version negotiation, or a new discovery API
until a concrete caller cannot proceed with help and Skill content.

### Current-fact reads

Existing resource commands remain canonical:

```bash
mo issue view <number> --json ...
mo run view <run-id> --json ...
mo run why <run-id> --json ...
mo session view <session-id> --json ...
```

Their field catalogs must expose enough information for one Agent decision
without private UI state or raw-log parsing. The first shared read contract is
small: resource identity, current state, blocker or waiting reason, next
permitted action, and evidence references where they already exist. `mo run
why` is the diagnosis path for a stopped or confusing Run; it must be
discoverable from `mo run --help` and documented with the other Run actions.

The CLI keeps the existing output rule: human output is concise, selected JSON
is stable, and streams are NDJSON. A new generic `mo task` or `mo status`
command is not needed while the existing resource reads can provide this
projection.

### Safe writes and recovery

The first priority is `mo issue start` and the existing Run controls that can
create or stop work. They accept an `--idempotency-key`, return the canonical
resource identity, and replay the original operation when retried with the
same key. Reusing a key with different inputs is a conflict. Extend the same
contract to other mutations only when they become asynchronous or externally
observable; do not pre-design every future command.

### Deferred: resumable observation

`mo run watch` currently polls snapshots. Cursor-based reconnectable streams
are a later option, not part of the first CLI increment. First verify that
current reads and keyed writes solve an observed Agent workflow; only then add
streaming if polling is a demonstrated bottleneck.

### Actionable failures

The current exit-code contract remains: `0` success, `1` operation/domain
failure, `2` local usage failure, and `130` cancellation. Every domain failure
exposed to an Agent must additionally carry a stable code, whether the effect
is known, whether retry is safe, and the next permitted action. Human hints may
remain on stderr; structured callers must not parse prose to decide whether to
retry or hand off. A caller that is interrupted mid-write receives the same
facts: `canceled` or `timeout`, the effect unknown, and the same command under
the same key as the next action.

## Read the Factory

Before an Agent starts or changes work, it can read one consistent projection
of the existing factory. The projection contains, when applicable:

- Project, Issue, Workflow, Run, Job, Session, and stage identity;
- the current goal, scope, revision, and readiness;
- current state and observation time or freshness;
- the next permitted action and, only for a high-risk operation, the required
  confirmation challenge;
- blocker, failure cause, waiting condition, or uncertainty; and
- checks, artifacts, changes, and evidence references.

The projection is a product contract shared by CLI, API, Skills, Web, and
connections. A caller must not need private UI state or raw-log parsing to
answer whether work is waiting, running, complete, failed, partially applied,
or unknown.

The Server remains the state arbiter. Runner observations, Agent statements,
accepted responses, and timeouts are evidence inputs; none independently
changes a confirmed product outcome.

## Operate Existing Work

Agent operations act on the existing Issue and Workflow lifecycle:

1. find or create the appropriate Project and Issue;
2. clarify only information that is still missing from the requirement;
3. verify readiness, scope, permissions, and current revision;
4. launch or continue the existing Workflow/Agent execution;
5. read stage results and checks before choosing the next operation;
6. repair or retry only within the caller's authorization; and
7. stop, request a decision, or hand over when the next action is reserved for
   a person.

An Agent does not create a parallel status model, silently rewrite an Issue's
goal, or treat an Agent Session as a replacement for the Issue and Workflow.
The same domain state machine controls operations whether they originate in a
Mohist Agent, External Agent, CLI, Slack, or Web UI.

Writes that can be retried use a durable operation identity or idempotency key.
A retry with the same identity returns the original operation and observation;
it does not create another Job, Session, Turn, queue entry, or external effect.
When an effect is unknown, the Agent receives an explicit unknown outcome and
must resolve or hand it off before attempting a superseding action.

## Handoff and Supervision

When a task reaches one of the explicitly high-risk operations, the product
requires a confirmation challenge rather than exposing a generic `allowedActor`
field. The challenge is bound to the exact operation and current inputs. The
supported confirmation can be a deliberate repeat of the operation or a
short-lived OTP, depending on the risk; ordinary actions proceed without it.

If an Agent cannot safely continue for another reason, it reports the blocker
and evidence without pretending that every blocker is an Approval Point. A
human handoff may include:

- the user's goal, current scope, and revision;
- the reason the Agent stopped or needs a decision;
- relevant checks, artifacts, and evidence;
- risk, expected impact, and known unknowns;
- available decisions and the consequence of each; and
- the next recovery action after approval, rejection, expiry, or takeover.

The confirmation is bound to the operation, inputs, and evidence revision. A
stale repeat or expired OTP cannot authorize a changed operation. The system
records the confirmation method, selected action, and evidence revision.

A Mohist Agent may continue work according to its configured instructions,
Skills, and supervision policy. An External Agent may request operations using
its granted identity. Neither may convert a description into permission or
close a human-owned decision without the product's explicit authorization.

## Return Results to the Origin

The Agent returns a result that allows the originating conversation to act
without opening the Web UI for routine work. A result states:

- what operation or work identity it refers to;
- the current confirmed outcome and freshness;
- what changed and what was checked;
- what remains unknown, partial, blocked, or waiting;
- the next action and who may take it; and
- links or references to the original evidence.

The result is a projection, not a second source of truth. Summaries must not
upgrade accepted, queued, timed-out, Agent-reported, or unknown work into
completed work. A later, explicit provenance contract can formalize cross-task
reuse; the first iteration only links back to the original resource and
evidence.

## Boundary

Agent Operability does not:

- replace Mohist's existing Issue, Workflow, Project, or Approval features;
- create a separate task or state model for each Agent or entry point;
- turn a Mohist Agent into a general-purpose chat workspace;
- treat Skills, prompts, or Agent self-report as permission or proof of
  completion;
- let an External Agent impersonate a configured Mohist Agent resource; or
- make the Agent the owner of product direction, risk acceptance, or permanent
  stop decisions.

## MVP

### Objective

Let one Agent operate one existing Issue through the existing CLI without
private UI state, duplicate asynchronous work, or prose parsing. The MVP proves
that the Agent can make and recover a next-step decision; it does not attempt
to make every Mohist entry point Agent-native at once.

### One user path

1. An Agent receives an existing Issue and reads its current state.
2. It starts the Issue with a caller-provided idempotency key.
3. It reads the resulting Run and, when needed, uses `mo run why` to diagnose
   a wait or failure.
4. It performs one safe next action: continue, retry, request user input, or
   report that a high-risk operation requires its own confirmation challenge.
5. If the write response is lost, it repeats the same command with the same
   key and receives the original operation instead of creating duplicate work.
6. It returns the confirmed outcome, evidence, and next action to the original
   conversation.

### MVP scope

- **Read:** stabilize the minimum JSON fields for `issue view`, `run view`,
  and `run why`: identity, state, blocker or wait reason, next action, and
  existing evidence references.
- **Write:** add keyed replay to `issue start` and Run controls that create or
  stop work. Same key and same inputs replay; same key with changed inputs
  conflicts.
- **Failure:** return a stable error code, effect certainty, retry safety, and
  next action in structured output. Human-readable hints remain available.
- **Confirmation:** do not add a universal actor field. Only an existing or
  newly introduced genuinely high-risk operation defines a repeat or OTP
  challenge of its own.
- **Verification:** run one real Issue through start, wait or failure, inspect,
  recover, and final result, including a simulated lost response.

### MVP delivery slices

1. **Contract inventory:** record the current CLI fields, exit codes, and
   idempotency behavior; identify only the missing fields and commands.
2. **Read contract:** update the existing field catalogs and help for Issue,
   Run, and `run why` without adding a generic status command.
3. **Write contract:** implement and test keyed replay for Issue start and the
   smallest set of externally observable Run controls.
4. **Failure contract:** add structured error fields and map each MVP failure
   to a safe next action.
5. **End-to-end trial:** execute the one user path and document evidence,
   known limits, and the next justified increment.

### MVP acceptance

The MVP passes only if all of the following are observable:

- an Agent makes its next decision from JSON returned by existing resource
  commands, not raw logs or private UI state;
- retrying a lost `issue start` or Run-control response with the same key does
  not create another operation or external effect;
- changing inputs under an existing key is rejected as a conflict;
- every exercised failure tells the Agent whether the effect is known and what
  safe action is next; and
- the end-to-end trial produces a result that links to the original Issue,
  Run, and evidence.

### Explicitly out of MVP

Capability metadata catalogs, Skill version negotiation, cursor-based watch,
generic Approval packets, a complete partial/unknown taxonomy, cross-task
provenance, and parity trials across Mohist Agent, External Agent API, Web, and
CLI are deferred until the trial demonstrates a concrete need.

## Acceptance Criteria

The first CLI increment is ready when an Agent can operate one existing Issue
without private UI state:

1. `mo issue view`, `mo run view`, and `mo run why` expose the state and next
   action needed for a decision;
2. `mo issue start` and the relevant Run controls replay a lost response using
   the same idempotency key without duplicate execution; and
3. structured failures expose a stable code, whether the effect is known, and
   the next safe action without prose parsing.

Cross-entry parity, resumable watch streams, generic approval decision packets,
rich provenance, and catalog metadata remain follow-up candidates. They are
not acceptance criteria for this increment. High-risk confirmation is only
designed where an existing operation truly needs it.

## Implementation Status

The first Agent Operability increment is implemented. This document states the
product contract; the exact syntax, codes, and field names live in
[the CLI reference](../cli/spec.md), and the rules an implementation must
preserve live in [the design note](design.md).

- `issue view`, `run view`, and `run why` expose the decision facts: identity,
  state, blocker or wait reason, next action, and existing evidence references.
- `issue start` and the eight Run controls accept `Idempotency-Key`. The same
  key with the same inputs replays the recorded outcome; the same key with
  changed inputs is rejected as a conflict; a key whose request is still
  executing reports its own retry signal.
- An accepted Run control answers with the Run resource it changed, so `--json`
  on a control selects the fields `run view` reports instead of returning
  nothing; the one-line human confirmation is unchanged.
- The CLI sends a key for those commands, prints a generated key before the
  request, and re-sends a lost keyed write once.
- Failures on Issue and Run commands report a stable code, whether the effect
  is known, whether retry is safe, and the next action; `--json` selects the
  structured form instead of the two text lines.

Outside this increment, and not silently treated as current requirements:
capability metadata catalogs, cursor-based observation of a Run, a complete
waiting/partial/unknown taxonomy, generic approval packets, cross-task
provenance, and parity trials across Mohist Agent, the External Agent API, Web,
and CLI. A new high-risk operation defines its confirmation method on its own
rather than adding a universal actor field.

## Related Product Documents

- [Product Vision](../../../docs/vision.md)
- [Skills](../../agent/skills/spec.md)
- [Agent Sessions](../../agent/execution/spec.md)
- [Agent Supervision](../../agent/supervision/spec.md)
- [External Agent API](../agent-api/spec.md)
- [The Workflow](../../workflow/execution/spec.md)
- [Issue Management](../../issue/lifecycle/spec.md)
- [CLI Reference](../cli/spec.md)
