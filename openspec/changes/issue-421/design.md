## Context

The proposal defines the motivation and `specs/sub-issue-plan-context/spec.md` defines the required behavior. Today each builtin Plan prompt tells the Inline Agent to run `mo issue show` for the current issue. That gives the agent the child body and comments, but the child's `parentIssueRef` contains only the parent number and title; the parent body is not part of Plan input.

The Issue projection already stores `ParentIssueNumber`, while the parent title and body remain authoritative in the parent Issue state. `WorkflowItemTranslator` currently renders a `WorkItem` into the server-to-runner `WorkDispatch`, and the runner passes the rendered task prompt to `mohist/opencode` unchanged. No dispatch field carries related Issue context.

The principal constraint is `design/issue-breakdown.md`'s Workflow zero-awareness invariant: parent-child organization belongs to the Issue domain, and `WorkflowRun` must remain identical for child and ordinary issues. The change must also avoid loading or transmitting parent comments, attachments, artifacts, sibling data, or lifecycle behavior.

## Goals / Non-Goals

**Goals:**

- Resolve the current parent title and body from the Issue read side when dispatching a child issue's Plan Inline Agent task.
- Carry that data as an optional, typed dispatch field and present it as clearly labelled read-only background before the existing task prompt.
- Preserve the child issue body as scope authority and preserve byte-for-byte prompt behavior when no parent context applies.
- Keep the protocol additive and compatible with in-flight work and mixed server/runner versions.

**Non-Goals:**

- Persist parent data in `WorkflowRun`, task input, AgentSession state, or a new database projection.
- Inject parent comments, attachments, artifacts, sibling issues, or Epic context.
- Add a general related-issue graph, a new workflow template expression, or public CLI/API controls.
- Change parent-child lifecycle, workflow progression, approvals, or any non-Plan task.

## Decisions

### D1. Resolve a minimal parent context through the Issue read side

Add a narrow `IssueQuerier` operation that accepts the current `(projectId, issueNumber)`, reads the child's projected `ParentIssueNumber`, and, when present, deserializes the referenced parent Issue state to return only `Title` and `Body`. The result is a small `ParentIssueContext` value; it has no comments, attachments, artifacts, status, or child collection by construction.

`WorkflowItemTranslator` calls this read operation. The Workflow domain and `WorkflowRun.Metadata` receive no parent field, preserving the established Issue/Workflow dependency direction. Resolution occurs when a dispatch is rendered, so a retry or later Plan task sees the parent's current title and body, and a child detached before a later dispatch is treated as an ordinary issue.

Alternatives considered:

- Calling `IssueQuerier.GetAsync` for the child and parent was rejected because it assembles full issue details, including the excluded collections, only to discard them.
- Adding `Body` to `IssueRow` was rejected because a new projection column and migration provide no value beyond this narrow read.
- Copying parent context into `WorkflowRun` at start was rejected because it makes Workflow aware of Issue organization, persists a stale duplicate, and requires refresh semantics.

If the child declares a parent but that parent cannot be loaded or deserialized, dispatch rendering fails with an actionable consistency error. Silently omitting required background would produce a plausible but invalid Plan.

### D2. Carry a purpose-built optional field through the dispatch boundary

Extend `WorkDispatch` and `WorkDispatchResponse` with optional `ParentIssueContext` using the next Orleans field id. Mirror the field in runner `WorkDispatchResponse`, `RenderedWorkItem`, and `ActionContext`, and map it through `ServerConnection.poll` and `WorkExecutor` without placing it under `with`, `variables`, or `prompts`.

The translator populates the field only when all of these are true:

- the work item is a task;
- `stage` is `plan`;
- `uses` is the Workflow Inline Agent action `mohist/opencode`;
- the current issue has a parent.

Checks, non-Plan tasks, AgentJob dispatches, other actions, and ordinary issues retain `null`/absent context. This server-side gate is the single authority for applicability; the runner consumes the optional field without independently re-deriving Issue or stage rules.

Alternatives considered:

- A generic `RelatedIssueContext[]` was rejected because only one parent relationship is required and a generic graph would enlarge the model without a concrete consumer.
- Putting the data in Workflow variables or adding a `${{ parent.* }}` namespace was rejected because parent context is execution input, not user configuration, and would expand the public workflow language.
- Embedding the context directly into `with.prompt` on the server was rejected because prompt composition belongs to the runtime-specific action and would make the control plane interpret OpenCode prompt structure.

### D3. Compose the OpenCode prompt once, with explicit authority labels

After `mohist/opencode` resolves the task's existing prompt, it prepends a deterministic parent-background block containing JSON-encoded `title` and `body`, followed by an instruction that the block is read-only reference and the current child issue body remains authoritative for delivery scope. The original task prompt follows unchanged. JSON encoding keeps arbitrary Markdown and delimiter-like text inside data fields rather than requiring ad hoc escaping.

When `ParentIssueContext` is absent, the action passes the resolved prompt to `OpenCodeRuntime.runTurn` exactly as it does today. The action does not fetch Issues or infer parenthood. Repeating the block on every applicable Plan turn makes retries, session replacement, and independently dispatched Plan tasks self-contained rather than relying on prior session history.

Alternatives considered:

- Teaching builtin Plan prompts to inspect `parentIssueRef` and run a second `mo issue show` was rejected because that command exposes parent comments and artifacts, depends on agent compliance, and duplicates conditional logic across prompts.
- Injecting only the first Plan turn was rejected because a reset or replacement AgentSession could then lose required context.
- Passing parent data as a separate system message was rejected because the current runtime turn contract has one prompt string; widening the runtime API is unnecessary for this capability.

### D4. Verify both applicability and prompt composition at their owning boundaries

Server specs cover parent lookup and translator gating for child Plan `mohist/opencode`, ordinary Plan, child non-Plan, checks, and unrelated actions. They also assert that the dispatch context shape cannot carry comments or artifacts and that missing referenced parents fail explicitly. Runner tests cover poll mapping, `ActionContext` propagation, exact preservation when context is absent, and submitted prompt content/labels when context is present.

No probabilistic assertion is made about generated Plan prose. The contract is verified at the Inline Agent input boundary: distinct parent-only text must be present in the submitted prompt while excluded data must be structurally impossible to transmit.

## Risks / Trade-offs

- `[Parent body increases prompt size on every Plan turn] ->` Carry only title/body as required; exclude all other parent data and do not duplicate the context in persisted task or session models.
- `[Parent text can contain instruction-like content] ->` Label it as read-only background, JSON-encode the fields, and explicitly state child-scope authority; the parent body remains trusted requirement input at the same trust level as the child body.
- `[Dispatch-time reads can observe parent edits between Plan tasks or redeliveries] ->` Treat current Issue state as authoritative, matching current prompt/resource late-resolution behavior; tests use the latest committed parent state.
- `[A partial rollout may omit the new field] ->` Keep the field optional and additive: old runners ignore it, and new runners preserve existing behavior when an old server does not send it.
- `[Runner-side transport adds several pass-through fields] ->` Use one purpose-built value object and keep all applicability logic in the translator, avoiding duplicated policy in each transport layer.

## Migration Plan

1. Add the Issue read operation, optional server dispatch field, and server specs without changing persisted Issue or Workflow state.
2. Add the matching runner wire/internal types, mapping, prompt composer, and runner tests.
3. Update the canonical dispatch and issue-breakdown design documentation to record the resolved boundary before product implementation is considered complete.
4. Deploy server and runner together. The additive optional field also permits either order during a rolling deployment: old runners ignore unknown JSON fields; new runners treat a missing field as no parent context.

Rollback removes the prompt composition and stops populating the optional field. No data migration or cleanup is required; in-flight dispatch/state deserialization treats the absent Orleans field as `null`.

## Open Questions

None.
