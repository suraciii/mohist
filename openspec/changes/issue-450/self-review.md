# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Runner/Server execution and transcript persistence behavior, repository architecture, and testing rules. This review modifies no other file.

## Findings

### F-1 High: Check-stage `mohist/pi` execution has no lease acquisition path

The plan registers `mohist/pi` in the shared Action registry and requires check contexts to carry an already-held `WorkflowSessionTurnLease` (`design.md:66-76`; `tasks.json:156`). D6 defines acquisition only around an Inline Agent task lifecycle owned by `WorkExecutor` (`design.md:121-125`). Current check execution resolves arbitrary registered Actions and runs checks concurrently (`packages/runner/src/runtime/check-execution.ts:39-68`), while Server check parsing does not exclude `mohist/pi`.

No task defines a logical Session identity, lease acquisition/release, cleanup ownership, or same-session serialization for checks. Registering the Action therefore makes a path callable without the held token its type requires and can overlap Prompts for one logical Session. Either reject `mohist/pi` in check definitions before dispatch and remove it from check contexts, or explicitly specify check work ownership/session naming and assign per-check lease coordination plus concurrent same-session tests.

### F-2 High: Admitted OpenCode checkpoints have no restart transition

Every Workflow OpenCode/Pi binding owns the same manifest and active-turn checkpoint, even though this issue connects only Pi's full runtime observer (`design.md:9,156-162`; `tasks.json:102-105,160`). T-004 defines restart handling for prepared-but-not-admitted checkpoints and admitted Pi checkpoints only (`tasks.json:109`). A Runner crash after the OpenCode Action marks admission but before checkpoint closure leaves no specified transition.

Builders must currently choose between quarantining the stream forever, incorrectly invoking Pi repair, discarding the checkpoint, or expanding into OpenCode transcript reconciliation. Define the admitted-OpenCode rule consistent with the accepted crash-redelivery limitation: drain its durable pre-admission facts, record/retain an execution-outcome-unknown diagnostic if required, close the checkpoint without invoking either Runtime, and allow separately redelivered Workflow work to follow normal at-least-once behavior. Add crash-after-OpenCode-admission, startup drain, later same-session admission, and OpenCode-to-Pi rebind tests.

### F-3 High: Pending transcript evidence cannot identify or reconstruct the exact Action turn

The revised durable acknowledgement protocol commits pending evidence before responding (`design.md:168`; `specs/pi-workflow-session/spec.md:154`; `tasks.json:74`), but its key `{ actionStreamId, sequence, factIndex }` identifies an Action fact, not the transcript turn that must receive it. Current `session.input` opens a turn with prompt metadata, text/reasoning facts need text delta/correlation/timestamps, and transcript idempotency is scoped to the selected turn (`packages/server/src/Mohist.Server/Sessions/Services/TranscriptAccumulator.cs:59-99,194-230`; `packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionTranscriptStore.cs:45-87`). Existing `AgentSessionTranscriptEvidence` lacks the operation shape and stable turn target, and its retry path always uses `StartNewTurn: false` with `TextDelta: null` (`AgentSession.cs:324-330`; `AgentSessionGrain.cs:657-707`).

"Extend the evidence model" does not select a safe addressing protocol. Persist a deterministic Action-turn key from `session.input` through all facts and the exact projection operation (`start-turn` or `append-part`), prompt metadata, text delta, correlation identity, payload, and timestamps. Transcript storage must enforce idempotency against that stable turn key so a crash after transcript save but before evidence removal cannot attach/duplicate evidence on a later turn. Add a two-successive-turn test with transcript failure/reactivation between turns and a crash between transcript save and evidence-removal commit.

## Structural Checks

- `tasks.json` parses as valid JSON; all seven task IDs and dependencies resolve and the graph is acyclic.
- All referenced spec files and requirement anchors resolve.
- All three proposal capabilities and the issue's seven acceptance criteria are represented.
- WorkExecutor-only lease ownership, command admission linearization, Reset fallback removal, and crash-redelivery semantics are now coherent.
- Runtime/outbox no-resubmission is correctly distinguished from accepted at-least-once Workflow redelivery duplication.
- Catalog reporting/UI, Pi AgentJob and Session-command implementation, ACP/RPC, and a generic `AgentRuntime` remain outside scope.

## Verdict

The primary task path is covered, but builders still lack executable contracts for check dispatch, OpenCode checkpoint recovery, and durable transcript turn reconstruction.

<promise>FAIL</promise>
