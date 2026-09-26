# Readiness

Readiness evaluates the [Agent configuration](../configuration/spec.md), not
[Runner capacity](../../runner/presence-and-capacity/spec.md). Launch behavior
belongs to [Agent execution](../execution/spec.md).

## Readiness and Availability

Agent lifecycle and execution readiness are separate:

- `active` or `archived` says whether the Agent accepts new delegations.
- `ready` means Mohist confirmed that its execution configuration can run.
- `needs-setup` means Mohist found a configuration gap and provides its repair.
- `unknown` means Mohist cannot confirm execution readiness.

A temporarily offline or full Runner is Availability, not a Readiness failure.
Work may be accepted and queued. Entry points present one Mohist conclusion and
do not maintain separate Runtime rules.

## Implementation Gaps

- Agent Connection Readiness checks only Model and Runtime. Complete Runner and
  Runtime executability probing remains unavailable, so a launch can find more
  gaps.
