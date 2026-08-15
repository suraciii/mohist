# Issue 557: Reasoning Effort as a First-Class Execution Configuration

## Why

Users pick a reasoning effort ("推理强度") as a product-level choice, but
Mohist has no field for it. The effort is smuggled through the
runtime-specific `variant` field: the Pi runtime publishes its native
thinking levels (`off`…`max`) as the catalog's variant map and applies
`options.variant` via `setThinkingLevel`, while OpenCode treats the same
field as a native model variant. One field therefore has two meanings, an
Agent's saved value changes meaning (or is silently dropped) when the
runtime changes, a model that has both a variant and an effort cannot
express both, and the Server has no canonical vocabulary to validate an
effort before execution — an invalid value surfaces only as a runtime
failure or disappears entirely.

The prerequisite plumbing is now in place: the runtime-readiness witness
gates claims on runtime readiness, and the generic capability contract and
claim fence are already specified in
`design/agent-runtime-reasoning-capability.md` and the
`issue-557-runtime-reasoning-capability` change. Blocked work (issue 556,
per-launch execution configuration and preview) needs this stable, explicit
vocabulary.

## What Changes

- Add a canonical `reasoningEffort` execution-configuration value
  (`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`, or unset),
  independent from `variant`. An effort is never encoded as a variant and a
  variant is never interpreted as an effort.
- Accept `reasoningEffort` in the Agent-definition `agentConfig` (server
  validation, `mo agent create/update`, Web Agent profile editor) and in the
  issue-level agent-config override.
- **BREAKING**: remove the Pi variant/thinking-level smuggling. Pi
  registration stops publishing thinking levels as variants, `variant` no
  longer reaches `setThinkingLevel`, and a saved Pi "variant" that is really
  a thinking level becomes an invalid configuration that must be re-entered
  as `reasoningEffort` — no compatibility layer or migration.
- Freeze the tuple `(runtime, model, reasoningEffort, variant)` plus the
  capability revision in every durable execution snapshot: `AgentJobInput`,
  `RoutedAgentLaunchPlan`, `WorkDispatch` `with` payload,
  `AgentExecutionDefinition`, and session-target definitions. Later catalog
  changes never rewrite a frozen snapshot.
- Extend the runtime catalog wire contract (append-only) with per-model
  `reasoningEfforts`, `supportsReasoningEffort`, `complete`, and
  `capabilityRevision`; the `variants` map keeps only true variants.
- Resolve the frozen tuple before admission with a pure Server-side resolver
  returning typed dispositions: `supported`, `needs-setup`, `unavailable`,
  `unsupported_execution_configuration`, `incompatible_execution_configuration`.
  Only `supported` is admitted; absent or incomplete catalogs leave work
  pending, and an explicit rejection is a deterministic preflight failure
  recorded with the frozen tuple.
- Enforce the claim-time capability fence (conditional AgentJob and Workflow
  claims with an immutable capability expectation), so the tuple cannot go
  stale between resolution and claim.
- Translate canonical effort only inside the selected runtime adapter: Pi
  maps it privately to its native thinking level; OpenCode reports
  `supportsReasoningEffort=false` today, so an explicit effort on OpenCode is
  an explicit configuration failure — never a silent drop.
- Treat effort as part of Agent Readiness (definition matching and gaps), and
  surface it as its own control in the Web model pickers (driven by
  `reasoningEfforts`, not the variant map) and in execution evidence
  (session model facts record the applied effort).

## Capabilities

- `agent-reasoning-effort`: The Agent-definition side of the feature —
  canonical `reasoningEffort` vocabulary, write-surface validation (API,
  CLI, Web), snapshot freezing across all launch paths, Readiness matching,
  and the removal of the Pi variant/thinking-level overload.
- `runtime-reasoning-capability`: The runtime evidence and admission side —
  versioned catalog entries with separated variant and reasoning-effort
  maps, the typed resolver dispositions, the claim-time capability fence,
  and runtime-private native translation (Pi thinking level; OpenCode
  explicit unsupported).

## Impact

- **Server** (`packages/server/`): `AgentConfigSchema`,
  `AgentLauncher` resolvers, `AgentExecutionSnapshotResolver`,
  `AgentReadinessService`, `AgentJobGrain` (input + dispatch snapshot),
  `AgentJobInput` / `RoutedAgentLaunchPlan` / `AgentExecutionDefinition`
  records, `IRunnerGrain` / `RunnerRegistryGrain` / `RunnerRoutes`
  (`RuntimeCatalogEntry`, conditional claim APIs), `DispatchService`,
  Workflow `mohist/agent` translation, `EventCatalog` failure categories.
- **Runner** (`packages/runner/`): `runtime/host.ts` registration state,
  `runtime/pi/*` (effort → thinking level, catalog separation),
  `runtime/opencode/*` (variant-only, unsupported-effort rejection),
  `runtime/agent-job-executor.ts`, `core/types.ts`, session-target and
  follow-up handler types.
- **Web** (`packages/web/`): Agent profile editor, model selectors and
  variant/effort chips (`ModelSelect`, `model-option-list`, settings
  queries), readiness display.
- **CLI** (`packages/cli/`): `mo agent` create/update/view options and table
  renderer.
- **Docs**: `design/runtimes/pi.md`, `design/runtimes/opencode.md`, user
  docs for Agent configuration.
- **No new dependencies.** Wire changes are append-only except the removed
  Pi variant semantics, which is a deliberate product break.
- **Out of scope**: per-launch effort override UX and preview (issue 556),
  and native OpenCode effort support if upstream adds one.
