## Findings

### F1: Event bus source inventory remains unplanned

`design/eventbus.md` currently says Session events do not enter the bus and lists neither Epic nor AgentJob in its event inventory. This contradicts the dispatcher implementation, which persists and reads `AgentSessionEvents`, `EpicEvents`, and `AgentJobEvents` alongside WorkflowRun and Issue events. The proposal and spec require the document to become a trustworthy description of all durable producers, but T-004 only requires wording for backoff, pokes, retry reset, FIFO blocking, and the metric. It does not explicitly require correcting the inventory table.

An implementation can therefore satisfy every stated T-004 acceptance criterion while leaving the document internally inconsistent: it can say all durable producers poke the dispatcher while its table still says Session is excluded and omits Epic/AgentJob. Add a T-004 acceptance criterion requiring the event inventory to enumerate the five durable origins consistently with `IEventStore.ListUndeliveredAsync`, and require the document review to verify the table and prose agree.

<promise>FAIL</promise>
