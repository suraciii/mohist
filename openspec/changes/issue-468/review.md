# Review Findings

## P1: Id-less tool updates are counted as separate calls instead of one final transcript part

`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionActivitySummaryReducer.cs:41-45` gives every tool mutation that has neither a tool-call id nor a correlation id a synthetic, incrementing key (`tool:1`, `tool:2`, and so on). The reducer therefore retains each such mutation in `CurrentTurnParts` and counts all of them at lines 77-84.

That is not the normalized transcript's semantics. `TranscriptAccumulator` gives those mutations the common correlation key `tool`, and `AgentSessionTranscriptStore.SavePartsAsync` upserts rows by `(Type, CorrelationKey)` (`packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionTranscriptStore.cs:112-135`). Several id-less updates in the same turn consequently persist as one final `tool` part. Before this change, `TranscriptEventSummaryProjector` reduced that persisted final part and counted one fallback identifier. After this change, the activity card can report multiple tool calls and retain an obsolete failed state when an id-less failed update is followed by an id-less completed update in the same turn.

Use the transcript part's correlation/upsert semantics for the fallback key so same-turn id-less updates replace the current summary part, and add a reducer/grain regression test for failed then completed id-less tool updates. This is required by the durable-summary requirement to preserve final per-turn tool state and existing event-summary semantics.

<promise>FAIL</promise>
