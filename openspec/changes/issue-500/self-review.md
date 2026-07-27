## Review

Reviewed issue 500 against `proposal.md`, `design.md`, `tasks.json`, and both capability specs.

## Findings

### F-01 (SIGNIFICANT): Store decorators cannot identify a deferred persistence operation

**Location:** `design.md` Decision 3; `tasks.json` T-003.

The design proposes decorating `IAgentSessionStore` and `IAgentSessionTranscriptStore`, then using session identity and a monotonic write sequence as the completion observation. That only identifies individual storage calls, not the specific deferred `FlushAsync` operation required by `agent-session-persistence-observation`.

`AgentSessionGrain` calls `_transcriptStore.SaveAsync` from multiple paths: the synchronous input fence in `FlushPendingTranscriptAsync` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1212-1237`), immediate transcript-evidence persistence (`719-759`), and the timer-driven `FlushAsync` (`1240-1304`). These calls share the same session identity and store interface. A decorator cannot determine whether a transcript write after a checkpoint belongs to the timer cycle a test intends to await, an immediate input fence, or an evidence write. It also cannot emit one outcome representing the full state/event-plus-transcript cycle: state persistence can succeed before a later transcript failure.

This violates the spec requirement that a test await the persistence operation it caused and receive a successful result only once all data required for that flush is durable. It can make tests proceed after an unrelated write or report state-save success while the matching transcript remains pending.

**Required plan repair:** Revise the design and T-003 to introduce a correlation boundary that identifies each deferred persistence cycle and its final `FlushAsync` outcome. The signal must distinguish state/event failure, transcript failure, and full success, and it must remain unavailable from the production grain interface without adding production persistence work. Then make the migrated test helper await that correlated cycle rather than arbitrary store writes.

## Verified OK

- The proposal, specs, and T-001 cover all six grain deactivation controls and all four grain-key overrides, while preserving `WorkflowGrain.BindProfileForTest` as an explicit non-goal.
- T-002 covers required workflow-profile, event-push, background-task, and AgentJob collaborators, with explicit preservation of genuinely optional cache and diagnostic side channels.
- The task graph is acyclic: T-001 and T-002 are independent at priority 1, and T-003 correctly depends on lower-priority T-001 because both change the AgentSession grain contract.

<promise>FAIL</promise>
