## Context

An Agent definition already stores Instructions, `agentConfig`, and ordered Skills, but the two execution paths resolve different subsets of that state. `AgentLauncher` accepts a caller or Issue Runtime override and persists an `AgentJobInput` without Skills. `WorkflowItemTranslator` resolves only Instructions and `agentConfig` for `mohist/agent` tasks. Neither `OpenCodeRuntime` nor `PiRuntime` currently receives a Skills input.

The product contract requires the Agent definition to be the sole execution-definition owner. Workflow still owns TaskRun state and direct Agent launch still owns AgentJob state. AgentSession must retain the definition needed by follow-up turns without becoming an authority for the live Agent record.

## Goals / Non-Goals

**Goals:**

- Resolve one immutable execution definition containing Instructions, Runtime, Model, Variant, and ordered Skills for every direct Agent launch and `mohist/agent` task attempt.
- Persist the resolved definition before a work item can be offered, and reuse it for redelivery, recovery, and an existing direct AgentSession's later turns.
- Remove request-, Issue-, and routing-derived Runtime overrides for named Agents.
- Deliver Skills through the existing OpenCode and Pi runtime boundary without adding a new Action, Runner process, or provider dependency.

**Non-Goals:**

- Change the Runtime selected by ordinary `mohist/opencode` or `mohist/pi` workflow actions.
- Create an AgentJob, direct AgentSession, or Agent-domain dependency for a `mohist/agent` task.
- Add a central Skill registry, install Skill assets, validate their availability before dispatch, or introduce a new Skill DSL.
- Rewrite persisted Issue variables or historical AgentJob/AgentSession records.

## Decisions

### One resolved execution-definition value

Introduce an Agent-owned resolved value with `instructions`, `runtime`, `model`, `variant`, and ordered `skills`. It normalizes the absent Runtime to `opencode` and reads model/variant only from the active Agent's validated config. `AgentLauncher` and `AgentExecutionSnapshotResolver` both create this value; `WorkflowItemTranslator` consumes it through its existing read-side resolver.

The direct-launch path snapshots the value into append-only fields on `AgentJobInput` and `RoutedAgentLaunchPlan`. Existing `AgentConfigJson` remains an audit copy but is not a runtime decision input. Both normal and routed launches use the same resolved value when opening the AgentSession and building the work dispatch. Idempotent routed redelivery continues from the persisted canonical plan rather than resolving the Agent again.

For generic Agent-launch sessions, add the immutable execution definition to the Session's persisted settings. This is a Session-local copy needed to execute later follow-up turns and to initialize a replacement physical Runtime Session; it is not a second writable Agent definition. `OpenAgentSessionCommand` sets it only when the session is created, and later opens must preserve it. The Runner's generic-session read contract exposes it only to authenticated Runner commands, not to the public Session read model.

Alternative considered: retain the definition only on AgentJob and look it up from the job for follow-ups. Rejected because AgentJob owns only the initial launch and follow-ups have no durable dependency on it. Alternative considered: reread the Agent on each turn. Rejected because edits would silently change an existing Session.

### Named Agent Runtime has no caller override

Remove `runtime` from `AgentSessionLaunchRequest`, the `IAgentLauncher` launch signatures, and routed-launch call sites. Reject a direct launch body that contains `runtime` before opening a Session or submitting a job. Delete `IAgentRuntimeOverrideResolver`, `IssueWorkflowProfileManager.GetAgentRuntimeOverrideAsync`, and routing reads of `vars.agent.runtime`.

Keep Runtime in an Agent definition's `agentConfig`, but split the currently shared validation/projection path so Agent definition writes continue to accept `runtime` while Issue model configuration no longer writes it. Existing persisted `vars.agent.runtime` values remain readable as ordinary historical data but are ignored by named-Agent launch and routing. Remove the corresponding Issue model-selector controls. This does not affect Runtime choice for explicit `mohist/opencode` and `mohist/pi` actions.

Alternative considered: accept the direct `runtime` field but ignore it. Rejected because a successful request would claim a configuration choice that the system did not honor. Alternative considered: retain Issue overrides with lower precedence. Rejected because it leaves two authorities for the same named Agent execution.

### Persist transformed Workflow attempts

Extend `AgentExecutionSnapshot` with the resolved Runtime, Model, Variant, and Skills, rather than making `WorkflowItemTranslator` parse raw config independently. For `mohist/agent`, the translator composes Instructions with the workflow prompt, selects `mohist/opencode` or `mohist/pi` from the resolved Runtime, and writes model, variant, and Skills into the transformed `with` payload before Workflow persists the WorkDispatch.

The existing persisted WorkDispatch remains the reoffer source. A retry creates a new attempt and therefore performs a new resolution. Missing or archived Agents keep the current `agent_not_found` dispatch rejection before Runner offer. Profile save and validation remain shape-only and do not resolve an Agent.

Alternative considered: defer transformation to Runner. Rejected because reoffers would need the live Agent or a second snapshot store, violating the Workflow persistence boundary.

### Runtime-neutral Skill delivery

Add an optional ordered `skills` field to the Mohist-owned turn-option types for both Runtime adapters. The AgentJob executor and transformed `mohist/agent` payload pass the captured list unchanged. A shared Runner helper creates a deterministic, clearly delimited preamble from that list and places it ahead of the existing Agent instructions and work prompt when a physical Runtime Session is created or replaced. The preamble instructs the installed agent runtime to load the named Skills in order; skill names are serialized as data rather than concatenated as executable syntax.

For an already bound Session follow-up, the runtime sends only the user's new input because the established Runtime context already contains the definition. When Reset or confirmed-missing recovery creates a replacement binding, the Runner uses the Session-local execution definition to add the preamble and Instructions to the first input for that binding. An empty Skills list produces no Skills preamble or Runtime option.

OpenCode and Pi Runtime modules receive only this Mohist-defined option and remain responsible for their SDK calls. Missing or unusable installed Skill assets surface through the existing Runtime turn failure path; the Server does not preflight local Runner assets.

Alternative considered: use a provider-specific Skill API. Rejected because neither current runtime boundary exposes one and it would split the Agent contract by backend. Alternative considered: add Skills directly to every raw Workflow Action. Rejected because only the named-Agent transformation needs Agent definition ownership; generic Actions retain their existing public input contract.

## Risks / Trade-offs

- [Two persisted copies of a launch-time definition can drift] -> AgentJob and AgentSession copies are written from one resolved value before dispatch; each is immutable and serves a different aggregate's recovery path.
- [A skill preamble relies on the installed runtime's Skill mechanism] -> retain one Runtime-neutral representation, preserve order, and report missing assets through the existing actionable Runtime failure path rather than silently dropping Skills.
- [Removing Issue Runtime writes can surprise existing Issue workflows] -> preserve stored values without rewriting them and limit removal to named-Agent launch/routing; explicit Runtime Actions retain their configured `uses` value.
- [New serialized fields encounter older persisted records] -> use append-only Orleans field identifiers and treat absent Skills as an empty list and absent Runtime as `opencode`.
- [Binding replacement can lose the original role context] -> keep the immutable definition on the generic AgentSession and reapply it only to the first input of a replacement physical Session.

## Migration Plan

1. Add the resolved definition and append-only AgentJob, routed-plan, and generic-AgentSession snapshot fields; update Runner request types and fakes first.
2. Route direct, mention, and event launches through the new snapshot builder; update Workflow `mohist/agent` transformation to include Skills.
3. Remove Runtime override inputs and the Issue override resolver, then remove Web and CLI surfaces that expose those named-Agent overrides.
4. Update OpenCode and Pi runtime execution/follow-up paths to consume the immutable definition, including first turns after binding replacement.
5. Add Server, Runner, CLI, and Web tests for snapshot reuse, retry re-resolution, override rejection, empty/ordered Skills, and missing/archived Agent dispatch failures.

No data migration is required. Rollback is a source rollback before deployment; deployed new code tolerates historical records that lack the appended snapshot fields. After deployment, rollback must retain the new persisted fields and ignore them rather than attempting a data downgrade.

## Open Questions

None. Skill asset discovery and installation remain the existing runtime environment's responsibility; this change only carries the Agent-selected list into execution.
