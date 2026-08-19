# Design

## Boundary

The Runner already decides whether a provider retry is non-recoverable. OpenCode emits the `provider-quota-exhausted` diagnostic for quota and usage-limit exhaustion, and Pi owns the equivalent provider policy. The missing boundary is the conversion from that runtime fact to Workflow recovery eligibility.

The Runner maps that fact to the internal Action error code `provider-quota-exhausted`. `tryRecovery` rejects only that code before selecting a handler or creating a `retrySelf` task. The failed result remains failed and keeps the runtime's original provider message.

## Runtime Paths

OpenCode keeps its existing fail-fast policy and diagnostic. The executor capability maps that diagnostic code to the internal Action error code while retaining the diagnostic message as `ActionError.message`.

Pi records the provider retry message when its policy aborts a turn, emits the same diagnostic code, and maps the diagnostic to the same Action error code. This prevents the current generic `Pi provider retries exhausted` message from hiding the provider's useful quota text.

## Recovery Semantics

`tryRecovery` applies the veto before matching conditional or default handlers. It returns `null`, so the executor returns the original failed result and the Server sees ordinary task failure. No recovery budget is consumed, no Agent recovery task is added, and no new terminal state or public contract is introduced.

All other error codes and successful completion markers use the existing matching behavior. A transient rate-limit message that the runtime policy does not judge exhausted remains eligible for the existing short retry behavior and, after a normal failure, the ordinary Workflow recovery path.

## Verification

Focused Runner tests cover the recovery veto, Pi provider message and diagnostic mapping, existing OpenCode provider classification, and ordinary recovery regression. Full Runner typecheck and tests remain the delivery gate.
