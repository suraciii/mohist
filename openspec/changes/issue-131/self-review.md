# Self Review: Issue 131

## Findings

1. **Blocking: the plan's check support conflicts with the stated product contract, and it does not reconcile that conflict.**
   The issue description requires Agent references for both tasks and checks, and the proposed OpenSpec consistently implements both. However, the issue's current supervisor note identifies [`docs/actions/agent.md`](../../../docs/actions/agent.md) as the finalized product contract; that document defines `mohist/agent` only for a Workflow task, including its usage, snapshot, and failure sections. The plan calls that document controlling (`design.md`, line 5) but neither updates it nor records the intentional expansion. Implementation would therefore make checks work while the product spec tells users the Action is task-only. Resolve the source-of-truth discrepancy and add the resulting documentation work to `tasks.json`.

2. **Blocking: the server-side Agent lookup has no defined behavior for a templated `with.name`.**
   `design.md` resolves `name` at dispatch before producing the Runner envelope, while retaining Runner as the sole template-rendering boundary. The current Action validator skips type validation for any input containing a template token ([`ActionContractValidator.cs`](../../../packages/server/src/Mohist.Server/Workflow/Services/ActionContractValidator.cs), line 179), so the proposed virtual manifest would accept `name: ${{ ... }}` even though only `prompt` is declared renderable in the spec. The Server would then query the literal template text and incorrectly report `agent_not_found`. Specify and test one unambiguous rule: reject templated `name` values during profile validation, or define a Server-side rendering/snapshot mechanism for the reference without moving prompt rendering out of Runner.

3. **Required clarification: the documented name-or-id resolver order is inconsistent with the planned wording.**
   `design.md` says the translator resolves `name` "as id first or name" (line 44), but the canonical `AgentRefResolver` treats `agent_*` references as ids and otherwise resolves by name before an id fallback. The design also says it will reuse that canonical resolver. State the actual resolver contract and add a focused test for it; otherwise an implementation can silently choose a different Agent when a name and legacy/non-prefixed id collide.

<promise>FAIL</promise>
