# Transport

Transport carries Server-owned work without taking ownership of it.
The [wire design](design.md) preserves the
[work-confirmation contract](../work-confirmation/spec.md).

## Connection and Recovery

Runner may lose its Server connection without losing Server-owned work:

- Server-owned work remains available for redelivery.
- Results retry after connectivity returns.
- Session mutation recovery follows the operation's contract.
- New work waits until Runner reports current presence and readiness.
- A live Workspace inspection fails as unavailable instead of returning a
  guessed filesystem result.
- A mutating retry keeps its original operation identity.

A transport timeout does not prove success, failure, or a missing Runtime
Session. Retry only through the operation-specific recovery path with the
original identity.
