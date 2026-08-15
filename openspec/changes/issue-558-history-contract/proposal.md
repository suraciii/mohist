# Issue 558: canonical history projection admission contract

## Why

AgentSession already retains stable session, input, and turn identities and
durable timestamps. It does not, however, retain usage and cost at Turn scope.
The only authoritative usage summary is cumulative for the whole Session, while
transcript usage parts are not assigned a durable usage revision and can be
merged by their correlation key. Publishing a history row that copies the
Session summary into every Turn would report the same spend repeatedly.

## Decision

This change defines the admission boundary for the next history slice. A
canonical history row may be exposed only when its timestamp and every usage
fact are attributable to the same durable Turn identity. Until that source
contract exists, the server must not map a history endpoint or synthesize
per-Turn cost from `AgentSession.Status.UsageSummary`.

The eventual read contract is shared by Server API, Web, and CLI. It carries
the stable Session/Input/Turn/Job identities, public context references, result
and lifecycle facts, `startedAt`, `endedAt`, `durationMs`, model, and an
optional Turn-scoped usage object. No filesystem path or runtime provider
payload is part of that contract.

## Non-goals

- Do not add a history route, CLI command, or Web query before the durable usage
  source is available.
- Do not infer usage from wall-clock ranges, event order, Runner logs, or the
  current Session cumulative summary.
- Do not reuse the unmerged history/timeline implementation from the older
  attempt; its `scope: session` cost field violates this admission boundary.
