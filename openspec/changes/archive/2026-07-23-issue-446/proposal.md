## Why

A Workflow Profile that misspells `uses` or passes an unknown `with` field saves without complaint today and only fails when the run reaches that task, leaving the author with no early, actionable signal. #432 has delivered the authoritative Workflow Definition semantic model — one validator that owns every Definition-language rule and hands off `uses` and `with` to the Action contract — and #444 has delivered the declarative Action manifest, its serializable catalog (with removed-Action tombstones), and the Runner's dispatch-time input validation. The catalog is already reported on registration and retained by the Server, but nothing consumes it. This change closes that gap by validating each task and check against the catalog at Profile save, so authors learn about unknown Actions, unknown inputs, and wrong types the moment they save, while the Runner's dispatch entry remains the authoritative fail-closed judge.

## What Changes

- During Profile save, consume the Runner's latest reported Action catalog to validate every task and check: reject an unknown `uses` by naming the task or check, the Action, the YAML path, and an Action-contract source.
- Distinguish a tombstoned (removed) Action from an unknown one, surfacing the tombstone's guidance so the author knows the Action existed and was retired.
- Reject unknown `with` fields, missing required inputs, and constant-value type mismatches at save, identifying the offending field and the reason.
- Validate a `with` input that contains a template expression by field name only; leave value-type checking to the Runner after the expression is rendered.
- Merge Action-contract errors with Definition errors through the shared YAML-path rule, distinguished by source; do not duplicate Definition-field or template-namespace rules, which stay owned by the Definition validator.
- When no catalog is available, do not reject the save; state in the result that Action-contract validation was not performed.
- Replace the transitional inline-agent `with` guard with the catalog-backed check, so legacy fields are caught by the same rule that judges all Actions.
- Preserve the Runner's dispatch-time validation as the authoritative fail-closed boundary; save-time checks are advisory early feedback and never weaken it.

## Capabilities

- `profile-action-validation`: Profile save consumes the latest Runner-reported Action catalog to judge each task and check — unknown and tombstoned `uses`, unknown `with` fields, missing required inputs, and constant-value type mismatches — producing Action-contract errors that share the YAML-path rule with Definition errors but are labeled as a distinct source. Template-expression inputs are validated by field name only, with value types deferred to the Runner. When no catalog is available the save proceeds and the result states that Action-contract validation was skipped. Definition-language and template-namespace rules remain the sole property of the Definition validator; the catalog check adds no second parser, and dispatch-time validation stays the authoritative fail-closed boundary.

## Impact

- **Server** (`packages/server`): the Profile save entry (`WorkflowProfileYamlParser`) replaces the transitional `WorkflowActionGuards` with a catalog-backed check that consumes the already-retained catalog via `IRunnerRegistryGrain`; Action-contract errors reuse the existing `ValidationSource.Action`; the save result reports when Action-contract validation was skipped for want of a catalog. Operates on the single-runner model; multi-runner catalog merging is out of scope.
- **Runner** (`packages/runner`): no change — manifest/catalog reporting (#444) and dispatch-time input validation (#444) remain as-is and authoritative.
- **CLI** (`packages/cli`): `mo run validate` stays Definition-language only and connects to no Server; it deliberately does not replicate the catalog (non-goal).
- **Docs** (`design`, `docs`): the Profile save-time validation gap in `design/workflow/actions.md` closes, and the product reference is updated to reflect save-time Action-contract checking.
- **Dependencies**: none new; builds entirely on the #432 semantic model and the #444 catalog infrastructure.
- **Risk**: medium — it enters the Profile save main path, but degrades gracefully when no catalog is present and never weakens the dispatch-time boundary.
