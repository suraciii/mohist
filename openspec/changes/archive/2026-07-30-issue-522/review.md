# Review: Issue 522

## Result

No merge-blocking findings.

Confirmed launch-Turn stops now release only their durable stop claim and leave the AgentJob to record the authoritative terminal verdict. Pre-dispatch Runner unavailability abandons only claims that could not have reached the Runner. A terminal Turn with a persisted pending operation replays that same operation through the Runner journal before the claim is released, preserving the session-scoped abort fence. The cancel-operation journal's restart and corrupt-state coverage uses an injected in-memory filesystem.

The requested `mo issue show` syntax is not supported by the current CLI; `mo issue view 522` reports the issue as ready and in-progress/check. Focused Server coverage for these paths passes 21/21, and runner typecheck plus focused journal coverage passes 3/3.

<promise>PASS</promise>
