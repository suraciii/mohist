## Findings

1. **Medium: A declared `prompt` with the wrong JSON type crashes the strict launch binder.** `packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:218` calls `JsonElement.GetString()` for every non-null `prompt`, including numbers, arrays, and booleans. Those value kinds throw `InvalidOperationException`, but `BindAsync` at `:187` catches only `JsonException`; the endpoint therefore becomes a 500 instead of rejecting malformed client input before Agent lookup/session creation. Validate that `prompt` is a JSON string (or convert this type mismatch into the binder's normal invalid-body result) and add a route spec for `{"prompt": 1}`.

## Verification

`git diff --check master...HEAD` passed. The latest fix round's `npm test` and `npm run build` both passed; this review did not rerun them.

<promise>FAIL</promise>
