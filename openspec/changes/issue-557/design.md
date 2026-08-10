## Context

Saved Agents currently store `runtime`, `model`, and `variant` inside the opaque
`AgentConfig` JSON. `AgentLauncher.ResolveModelAndVariant` treats `variant` as
the only strength-like setting, while the Runner adapters pass that value to
the runtime. This makes a provider-specific variant indistinguishable from a
reasoning choice and leaves the API, Web, CLI, readiness, and Job views with no
shared representation of the value that will run.

The server already has the correct durability boundary for this change.
`AgentExecutionDefinition` is resolved from the Agent and then carried through
the launch coordinator, `AgentJobInput`, `AgentSession`, `AgentJobRuntimeSnapshot`,
and `WorkDispatch`. `AgentJobGrain.BuildDispatchAsync` builds the Runner payload
from that stored input rather than rereading the Agent. The change should extend
this path instead of adding a second execution configuration path.

Capability data is currently model plus runtime-specific variants. OpenCode
discovery and the Pi SDK publish different forms of that data, and an absent or
partial catalog is already possible. Readiness must therefore consume cached
capability facts from Runner registration, not start a provider or infer a
reasoning capability from a variant with the same spelling.

## Goals / Non-Goals

**Goals:**

- Define one canonical, ordered `reasoningEffort` vocabulary for saved Agents.
- Keep `reasoningEffort` and runtime-specific `variant` independent in storage,
  validation, projections, snapshots, dispatch, and diagnostics.
- Evaluate the complete `(runtime, model, reasoningEffort, variant)` tuple from
  registered capability facts without active availability probing.
- Preserve a valid requested tuple while a compatible Runner or capability
  catalog is temporarily unavailable.
- Freeze the resolved tuple before AgentSession or AgentJob acceptance and
  reuse it for retries, recovery, redelivery, and idempotent replay.
- Give API, Web, CLI, readiness, launch, observation, and Job read surfaces one
  execution projection.

**Non-Goals:**

- Add `reasoningEffort` to inline Workflow `options`; Workflow `options.variant`
  remains a runtime-facing contract in this change.
- Automatically translate an existing Agent `variant` into
  `reasoningEffort`.
- Probe a provider, start a runtime process, or make a model request while
  evaluating Agent readiness.
- Select a different model, runtime, effort, or variant as a fallback.
- Introduce a shared SDK-shaped abstraction between OpenCode and Pi.
- Change follow-up, Session binding, workspace, or concurrency semantics.

## Decisions

### 1. Use a fixed canonical effort vocabulary

The persisted `reasoningEffort` field accepts these case-sensitive values, in
ascending order:

`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`

`off` is the explicit no-reasoning choice. An absent field, a JSON `null`, or a
clear operation is unset and is not equivalent to `off`. A saved Agent with an
unset effort is not launchable; readiness reports missing configuration until
the user chooses an effort, including `off` when that is intended.

The vocabulary belongs to the Agent configuration contract, not to a provider.
Runtime catalogs publish which canonical values each model supports. A runtime
adapter owns the translation from a canonical value to its native request
parameter.

`AgentConfigSchema` becomes the write-side authority for the new key and
exposes the ordered values to server-side callers. `reasoningEffort` is added
to the Agent-definition whitelist. It is not added to `IssueAllowedKeys`, so
inline Issue and Workflow configuration cannot accidentally acquire the saved
Agent contract.

The write boundary accepts a canonical string or an explicit null/clear value.
Blank strings, unknown strings, numbers, arrays, and objects return an
actionable field error. No input is trimmed into a valid value, defaulted, or
copied into `variant`. A null effort is normalized to the configured unset
representation when the Agent is stored; the surrounding `runtime`, `model`,
and `variant` fields remain unchanged.

**Alternative rejected:** Treat the current `variant` value as a backward
compatible alias. That would preserve the ambiguity this change is intended to
remove and would make the same persisted value mean two different things.

**Alternative rejected:** Use an open string for effort and let each runtime
define its own values. That makes list/edit/read surfaces incomparable and
turns a provider token into the Agent's public contract.

### 2. Centralize config reading and expose one execution projection

Add one server-side execution configuration reader next to the existing Agent
config and snapshot code. It reads the raw `AgentConfig` once and returns the
four execution fields plus validation state. `AgentConfigSchema.Validate`,
readiness, `AgentExecutionSnapshotResolver`, and `AgentLauncher` use this
reader instead of separately parsing `model`, `variant`, and `runtime`.

The reader applies only the existing default for an absent runtime:
`opencode`. An invalid persisted runtime is invalid; it is not silently changed
to `opencode`. An invalid persisted effort is retained as invalid read state,
reported by readiness, and never used to create a dispatch snapshot.

The public projection is a small record with these fields:

- `runtime`: the resolved runtime when valid, including the `opencode` default;
- `model`: the configured model or null;
- `reasoningEffort`: a canonical value, the invalid raw token for legacy read
  state, or null when unset;
- `variant`: the configured runtime-specific value or null;
- `reasoningEffortState`: `configured`, `unset`, or `invalid`.

The projection is attached to Agent list/detail records, readiness, accepted
launch responses, launch observations, AgentSession execution facts, Job views,
and terminal Job results. The raw `agentConfig` remains available where it is
already part of an API, but it is not the execution contract consumed by Web,
CLI, or Runner code. Accepted Job projections are built from the frozen
snapshot, never from the current Agent.

**Alternative rejected:** Add the field only to `AgentExecutionDefinition` and
let every public route map it independently. That would preserve multiple
projection rules and would allow list/detail to drift from launch/Job output.

### 3. Extend capability catalogs with independent effort facts

Extend the per-runtime `RuntimeCatalogEntry` carried by `RunnerInfo` with
append-only fields for:

- `ReasoningEfforts`: model to canonical effort values, including an empty list
  when a known model supports no effort value;
- `SupportsReasoningEffort`: whether the runtime has this capability at all;
- `Complete`: whether the catalog is complete enough to make a negative
  compatibility decision.

The existing `Models` and `Variants` fields remain separate. `Variants` is
never copied into `ReasoningEfforts`, even when both contain `high` or another
identically named token. A legacy catalog without the new fields is treated as
incomplete rather than complete.

Runner adapters publish facts as follows:

- Pi maps its SDK `thinkingLevels` to `ReasoningEfforts`. It publishes
  runtime-specific variants separately; the existing Pi behavior where
  `variant` directly selected a thinking level is removed.
- OpenCode keeps its discovered provider variants in `Variants`. It publishes
  `ReasoningEfforts` only from an explicit runtime-owned effort capability
  source. If the installed OpenCode surface cannot distinguish the two, the
  catalog remains incomplete and readiness reports temporary uncertainty.

The registry aggregates catalogs per runtime without fabricating facts. Runner
selection evaluates individual registered Runners so a Runner with a complete
compatible catalog can be selected while another Runner with an incomplete
catalog cannot make the tuple look incompatible. A tuple is permanently
incompatible only when every eligible catalog is complete and no eligible
catalog supports it.

**Alternative rejected:** Infer effort support from names in `Variants`. The
same spelling can be a provider-specific option, and this would reintroduce
the old alias through a less visible path.

**Alternative rejected:** Run the provider's model command or request from
`AgentReadinessService`. Catalog discovery is a Runner registration concern;
readiness must be deterministic, local, and safe to call from list/detail
requests.

### 4. Separate compatibility from runtime availability

Introduce a compatibility evaluation in the Agent readiness path that consumes
the resolved execution tuple and cached Runner catalogs. It returns stable gap
codes and an action for at least these outcomes:

- `model-missing` or `reasoning-effort-missing` for missing configuration;
- `reasoning-effort-invalid` for an unknown persisted value;
- `runtime-effort-unsupported` when a complete runtime catalog says the runtime
  has no effort capability;
- `model-effort-incompatible` when a complete catalog excludes the requested
  effort for the selected model;
- `capability-unconfirmed` when the needed catalog is absent or incomplete;
- `variant-incompatible` when a complete catalog excludes the selected
  runtime-specific variant.

Existing structural errors such as malformed model references and invalid
runtime remain separate gaps. The readiness result includes the common
execution projection alongside its gaps.

`AgentAvailabilityService` continues to report capacity and concurrency, but it
also consumes the compatibility result when it chooses a waiting reason. It
must not turn `capability-unconfirmed` into a permanent configuration error.
The AgentJob admission path uses the same evaluator before choosing a Runner:

1. A complete compatible catalog makes that Runner eligible for the exact
   tuple.
2. A complete catalog that rejects the tuple excludes that Runner.
3. An incomplete catalog does not qualify a Runner and does not prove the
   tuple incompatible.
4. If no compatible Runner is known yet, the Job remains pending with a stable
   temporary-unavailability reason and is retried by existing registration or
   recovery signals.
5. If all known catalogs are complete and reject the tuple, the launch gets an
   explicit preflight failure and no dispatch is issued.

Configuration failures (`Needs setup`) are rejected at the normal launch
boundary. Routed launches persist the same result as their existing durable
preflight-failed plan so the event remains observable. Temporarily unconfirmed
capability is accepted as a Job snapshot and waits; it is not repaired by
changing the tuple.

No new polling loop is introduced. Runner registration/heartbeat updates the
cached catalog, and the existing AgentJob reminder and admission signals
re-evaluate the pending Job.

### 5. Freeze the four-field tuple at the existing launch boundary

`AgentExecutionDefinition` gains nullable `ReasoningEffort` as an append-only
Orleans field. The resolved definition is the only source for a saved-Agent
launch. The new field is copied through every existing durable plan or input
that currently carries `Runtime`, `Model`, and `Variant`:

- `AgentLaunchCoordinatorCommandEnvelope` and
  `AgentLaunchCoordinatorPlan`;
- `PrepareManualLaunchCommand`, `AgentJobInput`, and
  `RoutedAgentLaunchPlan`;
- `AgentSession` initial-launch and execution-definition records;
- `AgentJobRuntimeSnapshot` and `WorkDispatch`'s `AgentDefinition`;
- the flattened AgentJob `with` payload delivered to the Runner.

Every new Orleans field uses the next unused field id. Existing fields are not
renumbered or removed. `EquivalentInput`, plan equivalence, replay handling,
and difference diagnostics compare `ReasoningEffort` independently from
`Variant`.

The accepted launch response includes the execution projection returned from
the first accepted plan. A replay returns the projection from that same plan.
`AgentJobGrain.BuildDispatchAsync` emits `reasoningEffort` as a sibling of
`model`, `variant`, and `runtime`; it omits only an actually unset effort. The
Runner never reconstructs the value from `variant`.

Launch callers cannot replace any of the four fields through prompt, context,
or inline runtime options. Any future public override field must be rejected at
the launch boundary. The current coordinator request's runtime metadata is
validated against the resolved definition and is not an execution source.

**Alternative rejected:** Re-read the Agent definition on retry or recovery.
That would make a pending Job change when an Agent is edited and would break
idempotent replay.

**Alternative rejected:** Persist only the raw `AgentConfig` JSON and parse it
again in the Runner. That duplicates resolution rules, allows later schema
changes to alter an accepted Job, and hides the requested tuple from read
surfaces.

### 6. Deliver effort and variant through separate runtime inputs

`AgentJobExecutor` parses `reasoningEffort` and `variant` independently and
passes both to the selected runtime boundary. Add the sibling field to the
OpenCode and Pi turn option types, follow-up option types where AgentJob facts
are reused, and the result projection helpers.

The Pi adapter applies `reasoningEffort` to the SDK thinking level. It no
longer applies `variant` as a thinking level. Pi v1 publishes no independent
runtime variant values; a non-null Pi variant therefore produces an explicit
variant incompatibility rather than changing thinking level. This is the
intentional breaking correction for Agents that used Pi `variant` as effort.

The OpenCode adapter applies its native effort input and native provider
variant input independently. If the runtime boundary cannot represent both
for a selected model, it returns an explicit incompatible-runtime result and
the catalog/readiness path must report the same known limitation. Workflow
Actions continue to send only their existing `model` and `variant` options.

AgentJob terminal facts and Runner diagnostics carry the requested
`reasoningEffort` and `variant` separately. The server treats the durable Job
snapshot as authoritative when building the public result; Runner output
cannot replace the requested tuple.

**Alternative rejected:** Rename `variant` to `reasoningEffort` inside the
shared runtime types. That would break the Workflow contract and erase the
runtime-specific dimension rather than separating it.

### 7. Update API, Web, and CLI through their existing public boundaries

The API keeps `agentConfig` as the write shape and adds the field to the
existing schema validation. A patch that changes only effort sends the full
typed Agent config with the current `runtime`, `model`, and `variant`; a clear
operation removes or nulls only `reasoningEffort`. The response adds the common
execution projection and readiness gaps use the same canonical tokens.

The Web Agent entity client exports the projection, effort values, catalog
capability data, and typed create/update fields through its slice public API.
`AgentProfileEditor` adds an effort selector separate from the runtime variant
selector, preserves the current variant when effort is cleared, and renders
catalog-unconfirmed and incompatibility states without guessing a value.

The CLI adds `--reasoning-effort` and `--clear-reasoning-effort` to create/edit,
keeps them mutually exclusive, and validates the canonical values before the
request. Agent list/show JSON and table output expose runtime, model,
reasoningEffort, and variant using the same projection. The retired
`--agent-config` path remains retired.

The model catalog endpoint becomes runtime-aware for effort capabilities while
remaining additive to its existing model and variant response. No Workflow
Action input or CLI Issue flag changes in this issue.

### 8. Test the boundaries and the no-fallback invariant

Add focused tests at each owner boundary:

- Server schema and projection tests for every canonical effort, explicit
  `off`, unset/clear, blank, unknown, and non-string input.
- Readiness tests for missing, invalid, unsupported, incompatible,
  unconfirmed, compatible, and variant-incompatible tuples; assert no provider
  or process probe is invoked.
- Registry and admission tests proving incomplete catalogs do not fabricate
  incompatibility, compatible Runner selection preserves the exact tuple, and
  a known incompatible tuple fails before dispatch.
- Launch/coordinator/AgentJob tests proving the first accepted effort survives
  Agent edits, replay, recovery, redelivery, and routed preparation.
- Runner tests proving the payload has independent effort and variant fields,
  Pi maps only effort to thinking level, unset effort remains unset, and
  result diagnostics retain both fields.
- API, Web, and CLI contract tests for set, clear, read-back, invalid values,
  readiness messages, and list/detail consistency.

## Risks / Trade-offs

- [The canonical vocabulary may not be supported by every provider] -> Runtime catalogs publish per-model support; unsupported values fail explicitly and incomplete metadata waits rather than coercing to a nearby effort.
- [Existing Agents may have used variant as reasoning effort] -> Do not auto-convert. Surface the Agent as needing setup, preserve the variant unchanged, and require an explicit effort selection.
- [Pi currently uses variant as its thinking-level input] -> Move that mapping to reasoningEffort, add a regression test for a non-null independent variant, and report Pi variant incompatibility instead of silently reusing it.
- [OpenCode capability metadata may not distinguish effort from provider variants] -> Keep the catalog incomplete until an explicit source exists; readiness remains temporarily unconfirmed and no active probe is added.
- [A Runner catalog can be stale or disagree with another Runner] -> Evaluate per Runner, require a complete compatible catalog for dispatch, and retry the exact frozen tuple after registration changes.
- [The execution tuple crosses many Orleans and JSON records] -> Add only nullable fields with append-only ids, centralize projection mapping, and test snapshot immutability through recovery and replay.
- [Old durable Jobs have no reasoningEffort field] -> Treat the missing field as unset and never infer it from variant. Do not rewrite old snapshots; drain or explicitly reconcile pre-change Jobs during rollout.
- [The public surface has separate Web and CLI type catalogs] -> Add contract tests against the Server projection and export Web types only through the entity public API; update the CLI ResourceOutputCatalog with the same field names.

## Migration Plan

1. **Configuration and projection:** Add the canonical effort vocabulary,
   Agent-only schema validation, the central config reader, and the shared
   execution projection. Update `AgentInfo`, readiness DTOs, Job read DTOs,
   launch responses, and the API contract tests.
2. **Capability and admission:** Extend `RuntimeCatalogEntry`, Runner
   registration/heartbeat payloads, OpenCode/Pi catalog projection, registry
   aggregation, readiness evaluation, availability waiting reasons, and
   AgentJob Runner eligibility. Add the no-probe and incomplete-catalog specs.
3. **Durable snapshots:** Add `ReasoningEffort` append-only fields to the
   coordinator, routed plan, manual input, AgentJob input, AgentSession
   definition, and dispatch structures. Update all launch construction,
   replay/equality, recovery, and read projection paths to use the first
   accepted tuple.
4. **Runner delivery:** Add the sibling dispatch payload and runtime option
   fields, remove Pi's variant-to-thinking mapping, implement each runtime's
   effort translation, and include both fields in Runner diagnostics and
   AgentJob result projection.
5. **User surfaces and documentation:** Update the Web Agent entity/editor,
   CLI typed flags/output, runtime design documents, and Agent execution design
   references. Keep Workflow `options.variant` behavior unchanged.
6. **Rollout audit:** Before enabling new launches, identify saved Agents with
   no canonical effort or with a variant that was previously used as effort.
   Mark them as setup-required through normal readiness. Do not mutate the
   stored variant or manufacture an effort. Restart all Runners with the new
   registration contract before accepting configured effort Jobs; active
   development does not require a rolling-version compatibility layer.

There is no data migration that maps `variant` to `reasoningEffort`. New
configuration writes reject invalid effort values. Previously accepted Jobs
retain their existing snapshot fields; a missing effort remains unset and is
never repaired from the mutable Agent or variant. Rollback before new effort
configuration is safe, but rolling back after new configurations exist requires
those Agents to be edited back to the old contract because the old Runner does
not understand the new effort field.

## Open Questions

- Which supported OpenCode client/runtime versions expose an independent native
  effort input alongside `variant`? The Server contract is fixed; until the
  runtime can publish both facts, OpenCode compatibility remains unconfirmed.
- Should Pi gain a separate provider-specific variant dimension in a follow-up,
  or should Pi continue to reject `variant` after the effort split? The v1
  design treats it as unsupported so it cannot regain the old alias behavior.
- Should inline Workflow Actions eventually receive the canonical effort field?
  That would require a separate Workflow contract and is intentionally outside
  this saved-Agent change.
