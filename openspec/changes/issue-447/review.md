# Review Findings

## Findings

### 1. [blocker] The build-prompt workaround changes the Action contract and breaks existing callers

The current follow-up adds `buildPrompt` to the `mohist/openspec-tasks` manifest in `packages/runner/src/actions/built-ins.ts:145-148` and reads it in `packages/runner/src/actions/openspec.ts:96-106`. This is a new public `with` input, contrary to the issue's non-goal of preserving every Action input contract. More importantly, existing callers that supplied `prompts.build` through workflow variables but did not supply this new input now generate fallback prompt loaders without `base`; only newly migrated callers that explicitly pass `buildPrompt` retain the old behavior. The shipped profiles avoid the regression only because they already provide an explicit prompt loader, which does not preserve arbitrary existing OpenSpec callers. Restore the old conditional behavior without adding a new Action input or requiring callers to migrate their `with` payload; the variable-to-deferred-task handoff must remain engine-owned and preserve both the present and absent `prompts.build` cases.

<promise>FAIL</promise>
