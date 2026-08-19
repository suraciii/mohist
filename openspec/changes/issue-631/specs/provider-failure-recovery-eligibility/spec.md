# Provider Failure Recovery Eligibility

## Exhausted Provider Quota Does Not Schedule Agent Recovery

- GIVEN a Workflow task has a recovery handler or `retrySelf`
- AND the Runner runtime has classified its terminal provider failure as `provider-quota-exhausted`
- WHEN the executor evaluates the failed result
- THEN it returns the failed result unchanged
- AND it does not add recovery-handler tasks
- AND it does not add a self-retry task
- AND it preserves the provider's original error message

## Ordinary Failures Keep Existing Recovery

- GIVEN a Workflow task has a recovery handler or `retrySelf`
- AND the terminal error is an ordinary failure such as `script-failed`
- WHEN the executor evaluates the failed result
- THEN the existing recovery handler matching and budget behavior is unchanged

## Runtime Policy Remains Bounded

- GIVEN a provider emits transient retryable rate-limit events
- WHEN the runtime handles the turn
- THEN its existing short in-turn retry policy remains in effect
- AND this change does not add cross-run admission, Retry-After parsing, or a new Workflow terminal state

## Pi Provider Message Is Preserved

- GIVEN Pi stops a turn because its provider policy classifies a quota or usage limit as exhausted
- WHEN the Runner returns the Action failure
- THEN the original provider error text remains in the failure message
- AND the internal error code is `provider-quota-exhausted`
