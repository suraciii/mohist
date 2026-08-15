# Design: Turn-attributed history projection

## Existing durable facts

The current Session aggregate is sufficient for identity and lifecycle facts:

- `AgentSession.Status.CreatedAt` is the durable Session creation instant.
- `AgentSessionInputRecord` retains `Id`, `Sequence`, `RecordedAt`, text,
  acceptance, and optional `JobId`.
- `AgentTurnRecord` retains `Id`, `Sequence`, `InputIds`, `JobId`, status,
  result, `RecordedAt`, and `UpdatedAt`.
- Transcript turn rows retain `SessionId`, `Sequence`, `StartedAt`, and
  `UpdatedAt`; public context references are available without exposing a
  filesystem workspace path.

These facts are read-side inputs only. The history projector must not replace
the Session or Job lifecycle authority.

## Missing lineage

`AgentSession.Status.UsageSummary` is cumulative for the Session. It has no
TurnId, source revision, or delta/absolute marker. Runtime `usage.updated`
events are persisted as transcript parts under a fixed `usage` correlation key;
later writes can merge the payload and do not provide a stable, replayable
usage fact per Turn. An event timestamp alone cannot repair this because a
Session may contain overlapping or restarted runtime turns.

Therefore the following mapping is forbidden:

```text
for each Turn:
    turn.usage = session.status.usageSummary
```

It would make one Session's spend appear once per Turn and would make exported
history disagree with the actual execution.

## Required source change before implementation

The execution boundary must persist a turn-scoped usage fact whenever a
runtime usage update is accepted. The minimum fact is:

```text
TurnUsageFact {
  sessionId,
  turnId,
  revision,       // strictly increasing within (sessionId, turnId)
  recordedAt,
  semantics,      // delta or absolute; one value for the whole contract
  inputTokens,
  outputTokens,
  totalTokens,
  cachedReadTokens,
  cachedWriteTokens,
  thoughtTokens,
  costAmount,
  costCurrency
}
```

The writer must reject a usage update whose `turnId` cannot be resolved from
the acknowledged Session turn binding. A replay of the same source revision
must be idempotent. The source and the canonical AgentTurn lifecycle need not
become one aggregate, but the fact must carry both durable identities so the
read path can join them without guessing.

## Future public projection

After the source change, the projector can produce one row per
`(sessionId, turnId)`:

```text
HistoryItem {
  id, sessionId, inputIds, turnId, jobId,
  task, context, status, outcome, result,
  startedAt, endedAt, durationMs, model,
  usage, sessionLink
}
```

The Server DTO, Web TypeScript type/query, and CLI JSON/table output must use
the same field names and null semantics. The `usage` object is Turn-scoped;
absence means no attributable usage was recorded, not zero spend. A history
row links back to the existing unified Session route using `sessionId`.

## Read freshness and failure behavior

The first implementation should read the canonical Session/Turn state and the
durable TurnUsageFact rows in one read operation. If a terminal Turn exists but
its usage projection is not yet available, return a row with `usage: null` and
an explicit attribution state, or keep the row out of the public history
endpoint according to the product decision. It must never fall back to the
Session cumulative usage. A later projection checkpoint can make this an
eventually consistent read without changing the public identity contract.

## Negative contract

Focused tests in this change assert that the current model is not eligible for
the public history route: a Session-only usage summary cannot satisfy a
Turn-attributed usage requirement, and a transcript usage part without a
durable TurnUsageFact identity is rejected. This protects the boundary while
the source-side slice is implemented separately.
