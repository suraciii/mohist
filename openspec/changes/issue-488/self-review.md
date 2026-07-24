# Self-Review — issue-488 (`mo agent install supervisor`)

Reviewer role: critical review of `proposal.md`, `specs/agent-preset-install/spec.md`,
`design.md`, `tasks.json` against issue #488 and the existing codebase. No files
other than this one were modified.

## Verification of load-bearing claims (against code)

The plan hinges on a few assumptions about existing server/CLI behavior. I checked
each rather than trusting the design's citations:

1. **Tail-append on null before/after — TRUE.** `RoutingRuleStore.InsertPosition`
   returns `rules.Count + 1` and `Renumber` inserts at `rules.Count` when both
   anchors are null (`RoutingRuleStore.cs:171-196`). D4 and the "rules appended at
   the routing-table tail" requirement hold.
2. **Match DSL accepts `event.type == "..."` — TRUE.** `MatchParser` requires the
   root identifier to be `event` and supports `==` with string literals
   (`MatchParser.cs:151-157`). The two shipped expressions are valid.
3. **Event type strings are real — TRUE.** They match the notification mapping in
   `InboxProjectionHandler.cs` (`com.mohist.workflow.run.failed`,
   `com.mohist.workflow.stage.approval-requested`).
4. **Agent create requires non-empty name+instructions, 409 on dup — TRUE**
   (`AgentDefinitionRoutes.cs:19-47`). Rule create 409 `routing_rule_name_conflict`
   and 400 `invalid_match_expression` — TRUE (`RoutingRulesRoutes.cs:92-99`).
5. **JSONC parsing for the notification check is feasible — TRUE, and already
   solved.** A `StripJsoncComments` helper exists in the CLI
   (`MohistCliCommands.Notify.cs:214-217`), mirroring the server's. The preflight
   check can reuse it; strict `JsonNode.Parse` on `.jsonc` would otherwise throw on
   comments, but the helper removes that risk.
6. **Fixed create order is not cosmetic — it is a hard dependency.** Rule create
   validates that `agentId` references a real, non-archaged agent
   (`RoutingRuleStore.cs:150-161`: `agent_required` / `agent_not_found` /
   `agent_archived`). The supervisor Agent therefore *must* exist before either
   rule can be created. D1/D3's agent-first ordering satisfies this, though the
   design does not call out *why* the order is mandatory.

The plan is internally consistent, correctly scoped (explicitly excludes
`mo issue watch` #489, agent-failed notification, approval `--author`), and the
3-task DAG (T-001 → T-002 → T-003) is valid with dependencies pointing only to
strictly lower priorities.

## Findings

### F1 — Spec is silent on the rule → supervisor Agent binding [Medium, should fix]

The "Supervisor preset authoritative content" requirement fixes the rules' names,
match expressions, response prompts, and the agent's instructions — but never
states normatively that **both routing rules' `agentId` SHALL resolve to the
`supervisor` Agent**. That binding is the actual mechanism that makes the
supervisor supervise (events route to it). It is strongly implied and
overdetermined by the API (rule create rejects a missing/unknown agentId), so an
implementer will almost certainly get it right — but the spec offers no scenario
that would catch a regression where a rule is bound to the wrong agent or has a
stale id.

Recommendation: extend the content requirement (or T-002 acceptance) with a
scenario asserting both created rules' `agentId` resolves to the `supervisor`
Agent in the same project. This is the single most worthwhile strengthening.

### F2 — Archived-supervisor edge case unaddressed [Low, open question]

`GET /agents?all=true` (the existence check D3 prescribes) includes archived
agents. If a user previously *archived* an agent named `supervisor`, install
would detect "exists" and skip creation, yet rule create would then fail
`agent_archived` when binding to that archived agent (and creating a fresh
`supervisor` may 409 if the name unique index spans archived rows). The design
does not define behavior here. Narrow, but worth a decision: treat
archived-same-name as "does not exist for install purposes" (filter the list to
non-archived), or document the limitation.

### F3 — Preset asset-root resolution under-specified vs skill-data [Low, nit]

D2 says presets reuse "the `SkillAssetRootResolver` idea" with
`AppContext.BaseDirectory/presets` fallback, but does not pin the override env
var or managed-cache path for presets (skill-data uses `MOHIST_SKILLS_DIR` and
`~/.mohist/cli/skill-data`). Either state that presets have no override/cache
(sibling-only resolution) or name the paths. T-001's acceptance doesn't constrain
this, leaving it to the implementer.

### F4 — Traceability: T-002 carries three requirements but one spec anchor [Low, nit]

T-002 implements Preset-name-resolution, Idempotent-installation, and
Rules-appended-at-tail, but its `spec` field points only at
`#idempotent-installation-by-name`; the other two are acknowledged only in
`notes`. Usable, but a reader auditing "which task delivers Preset name
resolution" must read notes. Consider multi-anchor or splitting the field.

### F5 — Design D5 should name the JSONC helper [Trivial, nit]

D5 says "parse `Mohist:Notifications:Hermes:EnabledTypes`" without noting the
config is JSONC. Cite the existing `StripJsoncComments` helper so the
implementer doesn't hand-roll comment stripping.

## Assessment

No finding blocks building: every load-bearing technical assumption verified true
against the code, scope is correct, and the task graph is sound. F1 is the only
substantive one and is self-correcting in practice (the API enforces agentId
validity). F2–F5 are refinements. The plan is ready to build; the findings above
are improvements a separate fix task can fold in.

<promise>PASS</promise>
