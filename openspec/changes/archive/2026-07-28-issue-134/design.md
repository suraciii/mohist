## Context

An Agent definition already stores Instructions, `agentConfig`, and ordered Skills, but the two execution paths resolve different subsets of that state. `AgentLauncher` accepts a caller or Issue Runtime override and persists an `AgentJobInput` without Skills. `WorkflowItemTranslator` resolves only Instructions and `agentConfig` for `mohist/agent` tasks. Neither `OpenCodeRuntime` nor `PiRuntime` currently receives a Skills input.

The product contract requires the Agent definition to be the sole execution-definition owner. Workflow still owns TaskRun state and direct Agent launch still owns AgentJob state. AgentSession must retain the definition needed by follow-up turns without becoming an authority for the live Agent record.

## Goals / Non-Goals

**Goals:**

- Resolve one immutable execution definition containing Instructions, Runtime, Model, Variant, and ordered Skills for every direct Agent launch and `mohist/agent` task attempt.
- Persist the resolved definition before a work item can be offered, and reuse it for redelivery, recovery, and an existing direct AgentSession's later turns.
- Remove request-, Issue-, and routing-derived Runtime overrides for named Agents.
- Resolve installed Skills on the Runner and deliver their instruction bodies through the existing OpenCode and Pi runtime boundary without adding a new Action, Runner process, or provider dependency.

**Non-Goals:**

- Change the Runtime selected by ordinary `mohist/opencode` or `mohist/pi` workflow actions.
- Create an AgentJob, direct AgentSession, or Agent-domain dependency for a `mohist/agent` task.
- Add a Server-side Skill registry, install Skill assets, or introduce a new Skill DSL.
- Rewrite persisted Issue variables or historical AgentJob/AgentSession records.

## Decisions

### One resolved execution-definition value

Introduce an Agent-owned resolved value with `instructions`, `runtime`, `model`, `variant`, and ordered `skills`. It normalizes the absent Runtime to `opencode` and reads model/variant only from the active Agent's validated config. `AgentLauncher` and `AgentExecutionSnapshotResolver` both create this value; `WorkflowItemTranslator` consumes it through its existing read-side resolver.

The direct-launch path snapshots the value into append-only fields on `AgentJobInput` and `RoutedAgentLaunchPlan`. Existing `AgentConfigJson` remains an audit copy but is not a runtime decision input. Both normal and routed launches use the same resolved value when opening the AgentSession and building the work dispatch. Idempotent routed redelivery continues from the persisted canonical plan rather than resolving the Agent again.

For generic Agent-launch sessions, add the immutable execution definition to the Session's persisted settings. This is a Session-local copy needed to execute later follow-up turns and to initialize a replacement physical Runtime Session; it is not a second writable Agent definition. `OpenAgentSessionCommand` sets it only when the session is created, and later opens must preserve it. The Server projects it only into authenticated generic `ReceiveFollowup` targets, never into the public Session read model.

Alternative considered: retain the definition only on AgentJob and look it up from the job for follow-ups. Rejected because AgentJob owns only the initial launch and follow-ups have no durable dependency on it. Alternative considered: reread the Agent on each turn. Rejected because edits would silently change an existing Session.

### Named Agent Runtime has no caller override

Replace default model binding for `AgentSessionLaunchRequest` with its existing raw-body/presence pattern: a dedicated `BindAsync` parses the top-level JSON object, records its field names, and accepts only `prompt` and `context`. It rejects `runtime` and every other undeclared top-level field before the route reads the Agent or opens a Session. Remove `runtime` from `AgentSessionLaunchRequest`, the `IAgentLauncher` launch signatures, and routed-launch call sites. Delete `IAgentRuntimeOverrideResolver`, `IssueWorkflowProfileManager.GetAgentRuntimeOverrideAsync`, and routing reads of `vars.agent.runtime`.

Keep Runtime in an Agent definition's `agentConfig`, but split the currently shared validation/projection path so Agent definition writes continue to accept `runtime` while Issue model configuration no longer writes it. Existing persisted `vars.agent.runtime` values remain readable as ordinary historical data but are ignored by named-Agent launch and routing. Remove the corresponding Issue model-selector controls. This does not affect Runtime choice for explicit `mohist/opencode` and `mohist/pi` actions.

Alternative considered: accept the direct `runtime` field but ignore it. Rejected because a successful request would claim a configuration choice that the system did not honor. Alternative considered: retain Issue overrides with lower precedence. Rejected because it leaves two authorities for the same named Agent execution.

### Persist transformed Workflow attempts

Extend `AgentExecutionSnapshot` with the resolved Runtime, Model, Variant, and Skills, rather than making `WorkflowItemTranslator` parse raw config independently. For `mohist/agent`, the translator composes Instructions with the workflow prompt, selects `mohist/opencode` or `mohist/pi` from the resolved Runtime, and writes model, variant, and Skills into the transformed `with` payload before Workflow persists the WorkDispatch.

The existing persisted WorkDispatch remains the reoffer source. A retry creates a new attempt and therefore performs a new resolution. Missing or archived Agents keep the current `agent_not_found` dispatch rejection before Runner offer. Profile save and validation remain shape-only and do not resolve an Agent.

Alternative considered: defer transformation to Runner. Rejected because reoffers would need the live Agent or a second snapshot store, violating the Workflow persistence boundary.

### Runner-owned Skill resolution and Runtime-neutral delivery

Add a Runner `SkillResolver` with configured, ordered roots. Its defaults are `<workDir>/.agents/skills` followed by `$HOME/.agents/skills`; an optional colon-delimited `MOHIST_SKILL_ROOTS` value is appended after those defaults. For each captured name it accepts only a single safe Skill name, finds `<root>/<name>/SKILL.md` in root precedence order, and reads the file as UTF-8 instruction content. It returns an ordered list of `{ name, instructions }` values. A missing, unreadable, or malformed requested Skill returns `skill_not_found` before any provider Runtime call; the resolver never follows a name outside a configured root.

Add this resolved list to the Mohist-owned turn-option types for both Runtime adapters. The AgentJob executor and transformed `mohist/agent` payload pass the captured names to the resolver, then pass its result unchanged into the selected Runtime. A shared Runner helper constructs a deterministic, clearly delimited execution envelope from the resolved name-and-instruction values, Agent Instructions, and the user/work prompt. It serializes Skill data rather than interpolating executable syntax. An empty Skills list does not trigger resolution or add a Skills envelope.

The Server carries the immutable generic-AgentSession definition in the existing authenticated Server-to-Runner `ReceiveFollowup` target for generic sessions only; it remains absent from public Session DTOs and Workflow targets. The Runner's binding-only resolver projects that optional definition alongside the binding, and every generic follow-up uses the same execution envelope. This deliberately reapplies the immutable definition on every generic follow-up, including after Reset or confirmed-missing recovery, so the command path has no binding-history or “first prompt” state to infer.

OpenCode and Pi Runtime modules receive only this Mohist-defined resolved input and remain responsible for their SDK calls. The Server does not preflight Runner-local Skill assets.

Alternative considered: use a provider-specific Skill API. Rejected because neither current runtime boundary exposes one and it would split the Agent contract by backend. Alternative considered: pass only Skill names as a prompt instruction. Rejected because it cannot prove loading, identify a missing asset, or produce the required actionable failure. Alternative considered: add Skills directly to every raw Workflow Action. Rejected because only the named-Agent transformation needs Agent definition ownership; generic Actions retain their existing public input contract.

## Risks / Trade-offs

- [Two persisted copies of a launch-time definition can drift] -> AgentJob and AgentSession copies are written from one resolved value before dispatch; each is immutable and serves a different aggregate's recovery path.
- [Runner-local Skill roots differ across installations] -> make roots explicit Runner configuration, constrain names to a single path segment, and fail `skill_not_found` before provider submission rather than silently omitting a Skill.
- [Removing Issue Runtime writes can surprise existing Issue workflows] -> preserve stored values without rewriting them and limit removal to named-Agent launch/routing; explicit Runtime Actions retain their configured `uses` value.
- [New serialized fields encounter older persisted records] -> use append-only Orleans field identifiers and treat absent Skills as an empty list and absent Runtime as `opencode`.
- [A generic follow-up can execute after binding replacement] -> carry the immutable definition in the authenticated generic follow-up target and reapply it on every generic follow-up, avoiding hidden binding-history state.

## Migration Plan

1. Add the resolved definition and append-only AgentJob, routed-plan, and generic-AgentSession snapshot fields; extend the authenticated generic follow-up target and Runner types.
2. Add strict direct-launch body binding, then route direct, mention, and event launches through the snapshot builder while removing Runtime override resolution.
3. Add Runner Skill-root configuration, safe resolution, and resolved execution envelopes for both Runtime adapters; update Workflow `mohist/agent` transformation to include captured Skill names.
4. Remove Issue-level Runtime override controls and reconcile Web/CLI/documentation surfaces.
5. Add Server, Runner, CLI, and Web tests for strict override rejection, snapshot reuse, generic follow-up, retry re-resolution, empty/ordered/missing Skills, and missing/archived Agent dispatch failures.

No data migration is required. Rollback is a source rollback before deployment; deployed new code tolerates historical records that lack the appended snapshot fields. After deployment, rollback must retain the new persisted fields and ignore them rather than attempting a data downgrade.

## Open Questions

None.
