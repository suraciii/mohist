## Why

A Runner restart can currently redeliver work claimed by the previous process, leaving a Runner-local durable started fence as the only protection against duplicate execution and making report rejection ambiguous. The Server must own this crash boundary now so a dead process becomes an ordinary, decidable `runner-lost` failure and later Runner journal removal is safe.

## What Changes

- **BREAKING** Require each Runner process to present a unique opaque `processGeneration` at registration and on every poll, and bind every new claim to that generation.
- Before serving a newly registered generation's first poll, close every Running Workflow and AgentJob work item claimed by an older generation of the same Runner as `FAILED("runner-lost")`.
- Never redeliver a work identity claimed by one process generation for execution by another generation; normal dispatch, capacity, readiness, and reconciliation behavior remains unchanged within one live generation.
- Keep presence-expiry closeout as the backstop when a Runner process disappears without registering a replacement, using the same `runner-lost` failure reason.
- **BREAKING** Replace ambiguous report acknowledgement and transport-error interpretation with one explicit verdict: `accepted` when the owner durably settles the report, `refused` when the report is terminally inapplicable, or `outstanding` when the Runner must retry.
- Make duplicate and late reports return the same decidable verdict without creating a second terminal outcome.

## Capabilities

- `runner-process-generation`: Runner process identity, generation-bound claims, pre-poll closeout of older-generation Running work, and prevention of cross-generation execution redelivery for Workflow and AgentJob owners.
- `runner-report-verdicts`: Explicit accepted/refused/outstanding report arbitration, including deterministic duplicate and late-report handling and retry responsibility.

## Impact

- **Runner protocol:** Registration and poll request contracts under `packages/server/src/Mohist.Server/Api` and `packages/runner/src/server` gain the required process generation; report responses and Runner report handling adopt the explicit verdict contract.
- **Server lifecycle and dispatch:** `RunnerGrain`, `DispatchService`, claim APIs, registration ordering, presence closeout, and dispatch reconciliation under `packages/server/src/Mohist.Server/Runner` must enforce the current generation before admitting or rendering work.
- **Owner ledgers:** Workflow assignment/active-work state and AgentJob ledger records must persist the claiming process generation and support idempotent `runner-lost` closeout without changing their existing failure states or recovery policy.
- **Tests and operations:** Server SpecTests and Runner tests must cover restart during execution, closeout before the first new-generation poll, no cross-generation redelivery, stable late/duplicate report verdicts, and unchanged same-generation dispatch behavior. No new external dependency is required, but Runner and Server protocol changes must ship together.
