## Decision

The launch contract has one syntax owner: a nested `execution` object. The
existing strict top-level binder remains strict, while the nested object is
validated as a closed request DTO. This prevents accidental acceptance of
future top-level fields and makes the override visible in request fingerprints.

The semantic owner is `AgentLaunchExecutionResolver`. It takes the immutable
saved Agent snapshot and an optional override, then produces one
`ResolvedAgentLaunchExecution` value:

```text
saved Agent definition + explicit execution override
                 |
                 v
resolved definition + per-field source + executability result
```

The resolver is pure after the saved snapshot is loaded. Preview and launch
both call it before any participant or workspace side effect. Launch then
persists the returned definition and canonical request in the coordinator
plan; it never calls the mutable Agent definition again while advancing the
plan.

## Override Rules

Each property is optional. Omitted properties inherit the saved value; a
present property replaces it. `null` is not an alias for omission: it is
accepted only where the field's saved value may be cleared, and its canonical
representation is included in the fingerprint. Runtime and model must be
non-empty strings when present. `reasoningEffort` uses the canonical values
`off`, `minimal`, `low`, `medium`, `high`, `xhigh`, and `max`. Variant is an
opaque non-empty value owned by the selected runtime adapter.

The resolver returns a deterministic validation error for malformed values.
It does not probe a provider and it does not map a Pi value onto an OpenCode
value. If the exact tuple is not confirmed by the eventual claim-time
capability contract, the launch remains pending or fails with the
authoritative incompatibility code; the saved tuple is not substituted.

## Preview Boundary

`POST /api/projects/{project}/agents/{agent}/sessions/preview` accepts the same
prompt/context/execution shape as launch but ignores attachments for the
purpose of side effects. It may validate referenced issue, epic, and workspace
records, but it must not call attachment binding, workspace provisioning,
AgentSession, AgentJob, coordinator, or Runner APIs. Its response contains the
canonical resolved definition, field sources, executability state, gaps, and a
stable `requestFingerprint` that the launch response can echo.

The CLI `mo agent launch --preview` is a read operation against this endpoint.
It renders the response and exits non-zero for malformed input or a blocked
configuration. It never creates an idempotency record. A subsequent real
launch still requires its own Idempotency-Key.

## Launch and Replay

The launch route validates the override and resolves the definition before
attachment binding or workspace provisioning. The coordinator request and
plan carry the canonical override and resolved definition. The fingerprint
includes canonical JSON for the override (object keys sorted, arrays ordered,
omitted and explicit null preserved). Same key plus the same canonical
request resumes the original plan. Same key plus a different override,
including a changed field source, returns `launch_idempotency_conflict` before
creating or mutating a participant.

The Job input and AgentSession startup use the resolved definition from the
plan. A later Agent edit cannot change queued work. A preview response is not
an execution receipt and cannot authorize a claim; the future claim path must
compare the frozen tuple against the Runner catalog revision in one lifecycle
gate (the #557 dependency).

## Alternatives Rejected

- **CLI-only preview:** rejected because it would duplicate Server resolution
  and could show a configuration the launch route ignores.
- **Top-level `runtime`/`model` fields:** rejected because they weaken the
  existing strict request grammar and make future fields ambiguous.
- **Preview by creating then immediately stopping a Job:** rejected because
  it creates durable side effects and can claim a Runner before the operator
  confirms the result.
- **Fallback to the saved tuple when an override is unavailable:** rejected
  because the operator asked for a specific execution and the resulting Job
  would not match the preview.

## Dependency Gate

The first code slice must land the resolver, preview route, canonical request
fingerprint, and persisted plan/input fields together. The Runner capability
revision and conditional claim fence from #557 must be available before the
launch path reports an exact tuple as executable; until then, preview may
report `unknown` and launch must preserve the existing accepted/pending
semantics without fallback.
