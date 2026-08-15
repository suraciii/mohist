# Tasks: Turn-attributed history projection admission

- [x] Record the durable Session/Input/Turn timestamp and identity facts that
      can already support a history row.
- [x] Record why Session cumulative usage and merged transcript usage parts do
      not satisfy Turn attribution.
- [x] Define the minimum durable TurnUsageFact identity, revision, and
      delta/absolute semantics.
- [x] Add a focused negative contract for the current ineligible model.
- [ ] Add the source-side turn-scoped usage persistence and replay/idempotency
      proof.
- [ ] Implement the shared Server/Web/CLI history projection after the source
      contract is present.
