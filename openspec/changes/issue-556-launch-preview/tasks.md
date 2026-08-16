## Delivery Plan

- [ ] Add the closed `execution` request DTO and pure saved-default/override
  resolver with field-source reporting and canonical validation.
- [ ] Add preview response and route. Prove no Job, Session, Input, Turn,
  workspace, attachment, coordinator, or Runner side effect.
- [ ] Thread the resolved definition and canonical override through the
  coordinator request, durable plan, AgentJobInput, Session startup, and
  launch response.
- [ ] Include the canonical override in idempotency fingerprints and cover
  same replay, changed override conflict, explicit null, and field-source
  behavior.
- [ ] Add CLI `mo agent launch --preview` and render the exact resolved tuple,
  sources, executability state, gaps, and fingerprint.
- [ ] Connect exact-tuple executability to the #557 claim-time capability
  revision fence. Do not silently fall back while that dependency is absent.
- [ ] Run Server/CLI focused tests, current-head static checks, and the full
  local gate before opening the implementation PR.
