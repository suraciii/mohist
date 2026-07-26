## Findings

### P1: Candidate selection is not the required single database-side query

`AgentSessionQuery.ListStatusCandidatesAsync` first materializes every running Workflow ID for the project (`AgentSessionQuery.cs:108-114`), then issues a second Session query using an in-memory `HashSet` (`AgentSessionQuery.cs:116-128`). This does not implement the planned two-branch query that joins `AgentSessions` to running `WorkflowRuns` at the persistence boundary. A status poll therefore reads and allocates IDs for every running Workflow in the project, including runs with no Session candidates, before it can select a Session. Replace the pre-query plus `IN` set with a single database-side predicate/join (or union of the direct and Workflow branches) so the candidate query's work is bounded by rows that can contribute to the response and the planned one-read boundary is met.

### P1: The Workflow branch materializes Sessions that cannot represent pending work

The non-direct branch only checks `LabelSourceId` (`AgentSessionQuery.cs:121-124`). It does not require the stored `LabelWorkId` projection, although `WorkflowActivityQuerier` unconditionally rejects records with a blank work ID after deserialization (`WorkflowActivityQuerier.cs:90-91`). Consequently, any non-direct historical Session in the project whose source ID names a running Workflow but which has no work ID is counted as a candidate and deserialized on every poll, despite never being eligible for `activeAgents`. This violates the design's explicit candidate restriction to non-direct Sessions with both a source ID and work ID, and makes `amplification.candidates` include work the response cannot process. Add a nonblank `LabelWorkId` predicate to the Workflow branch and cover a Session with a running Workflow source ID but no work ID, asserting it is neither materialized nor counted.

<promise>FAIL</promise>
