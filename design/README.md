# Cross-Domain Design

Read [Domain Analysis](domain-analysis.md) for business ownership,
[Architecture](architecture.md) for shared system boundaries, and
[Conventions](conventions.md) for identities and cross-domain contracts.

Feature-local mechanisms live beside their [product specifications](../specs/README.md).
Database evolution is an [engineering practice](../eng/database-migrations.md).
The records below preserve decisions and their rejected alternatives, not a
second copy of each feature contract.

## Decision records

- [decisions/issue-owns-epic-membership.md](decisions/issue-owns-epic-membership.md) — Issue holds the current Epic membership; Project-scoped number identity and cross-aggregate recovery flow.
- [decisions/epic-status-revival.md](decisions/epic-status-revival.md) — Epic `done` auto-revival and `closed` link rejection.
- [decisions/composite-issues.md](decisions/composite-issues.md) — Composite Issues: explicit owner-chosen decomposition, independent of the Epic axis.
- [decisions/slack-adapter-go.md](decisions/slack-adapter-go.md) — The Slack adapter is a static Go binary; the accepted deltas from the Node implementation.
- [decisions/one-ledger-no-reconciliation.md](decisions/one-ledger-no-reconciliation.md) — Why AgentJob dispatch has no staged copy or reconciliation loop.
- [decisions/squashed-baseline.md](decisions/squashed-baseline.md) — Point-in-time record of the accepted schema deltas at the current migration squash baseline.
- [decisions/workflow-run-profile-naming.md](decisions/workflow-run-profile-naming.md) — Why Run Variables retain the historical WorkflowRunProfile persistence name.
- [decisions/workflow-agent-binding.md](decisions/workflow-agent-binding.md) — Workflow tasks bind Agents through the ordinary `mohist/agent` Action.
- [decisions/routing-agent-reference.md](decisions/routing-agent-reference.md) — Routing commands accept one Agent reference resolved to a stable ID by the CLI; rule edits encode field presence explicitly.
- [decisions/volatile-runtime-event-evidence.md](decisions/volatile-runtime-event-evidence.md) — Receipt-wait evidence belongs to the active bounded waiter interval, not to the queued record.
- [decisions/openspec-removal.md](decisions/openspec-removal.md) — OpenSpec carries no role in the product; archived material lives outside the repository.
