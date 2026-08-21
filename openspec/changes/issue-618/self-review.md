# Self-Review: Issue 618 Plan Artifacts (Round 2)

This is a re-review. I re-read the canonical issue body and acceptance criteria first with:

```bash
mo issue view 618 --project proj_f6c141d63b6243bfbb481737b2243b87
```

I then verified the disposition of the prior review against `proposal.md`, `design.md`, `tasks.json`, and all four files under `specs/`.

## Verdict

**PASS** — no must-fix problems remain; the plan is ready to build.

## Previous finding disposition

### MF-1 — fixed properly

The prior review found a conflict between the exact nine-operation management allowlist and the required `mo slack message send` reply action. The updated artifacts resolve that conflict consistently:

- `specs/manager-command-capability/spec.md:2-18` defines `ManagerManagementCapability` as exactly the nine operations, gives the exact request envelope and argument mapping, and explicitly rejects `mo slack message send` on that bridge.
- `design.md:78-90` defines separate `ManagerManagementBridge` and `ManagerSlackReplyBridge` routes. It also defines the reply bridge's trusted anchor mapping, Manager outbox owner, route-scoped audiences, and per-input idempotency behavior.
- `specs/manager-execution-credential/spec.md:32-74` makes the management and reply audiences disjoint and requires validation before either application or outbox service is called.
- `tasks.json` T-003 and T-005 contain corresponding implementation and denial tests, including reply denial on the management route, anchor validation, Manager-owned routing, duplicate payload conflicts, and recovery redelivery.

This fixes the specific issue against acceptance criteria 1 and 7 without reintroducing a reply exception into the management allowlist. The proposal and lifecycle spec still remove the old model-output parser, synthetic follow-up, and Server-authored reply paths, so the fix did not regress acceptance criterion 2 or the no-compatibility-path non-goal.

## Re-review checks

- **Prior must-fix findings:** checked; MF-1 is fixed in the contract, design, task breakdown, and tests.
- **Regression from the fix:** checked, no must-fix regression. The reply route is separate, has its own audience and authorization, and does not broaden management access.
- **Coverage:** checked, no must-fix issue. The artifacts address all eight acceptance criteria: Agent-authored CLI-backed replies and received reaction; removal of envelope/synthetic/server-authored text; managed-Bot loop prevention; ephemeral credential boundaries; recovery reissuance; current authorization; strict exclusions; and one terminal reaction for every outcome.
- **Correctness:** checked, no must-fix issue. The selected boundaries make management effects originate only from the allowlisted capability, replies originate only from the separately authorized reply action, and liveness remains an independent reaction projection.
- **Current codebase consistency:** checked, no must-fix issue. The plan explicitly reuses the existing Session/Runner Slack context, application services, Slack outbox, Manager owner kind, managed-Bot admission, and liveness projection, while naming the current Manager parser, generic reply lookup, and Server terminal-delivery branch for removal or replacement.
- **Task breakdown, ordering, and verifiability:** checked, no must-fix issue. T-001 establishes the transient credential boundary; T-002 independently protects ingress; T-003 builds the management bridge; T-004 switches the Session lifecycle and removes the old protocol; T-005 completes reply delivery and liveness. Dependencies are acyclic, and each task has focused acceptance and regression tests.

## Observations

These do not affect the PASS verdict because the plan states the required invariants and assigns verification; they are implementation cautions rather than problems that make the plan wrong or incomplete relative to Issue 618.

1. The exact Runner-to-CLI transport, multi-Server placement/routing of the non-durable grant store, and grant TTL remain implementation choices in `design.md:145-147`. The plan does provide the non-negotiable security invariant and leakage tests. The chosen mechanism must preserve that invariant rather than turn the credential into a generic shell environment variable or durable token row.

2. Manager liveness is specified as reaction-only, but the existing common projection carries fallback text such as `Received...` and `Working...` in `SlackStatusProjection`. T-005 should explicitly verify the Manager path does not emit those fallback messages, including reaction-delivery failure and retry cases; this is already covered by the plan's no-Server-authored-text contract and is not a missing acceptance criterion.

3. Terminal delivery must resolve the triggering Manager input from durable Session/input provenance when a follow-up terminal envelope has no `messageTs`. The design calls for this resolution and T-005 requires absent-progress, restart, recovery, and redelivery coverage. The implementation should not fall back to the initial DM root or a synthetic terminal identity when finalizing reactions.

4. The command spec describes `list` as workspace Manager status and `view` as a single Agent/Connection inspection. The implementation should make the `list Agents` scenario explicit in the structured result and test it against the same facts exposed by the protected `mo` path, including project/workspace scope. This is a clarity and verification concern, not a demonstrated failure of the issue goals.

<promise>PASS</promise>
