# Review: Issue 410

## Findings

### P1: ACP terminology remains in production source comments and diagnostics

The change does not meet the ACP-removal requirement that execution paths,
configuration, diagnostics, and developer-facing comments no longer name ACP.
There are still non-legacy production references throughout the changed surface,
including `packages/runner/src/actions/opencode.ts:44`,
`packages/runner/src/runtime/agent-job-executor.ts:22,37`,
`packages/runner/src/server/session-target.ts:19-21`,
`packages/runner/src/server/followup-failure-outbox.ts:10,14`,
`packages/runner/src/system/process.ts:243`,
`packages/server/src/Mohist.Server/Infrastructure/AgentConfigSchema.cs:10-15`,
and `packages/server/src/Mohist.Server/Workflow/Services/ProjectVariablesFilter.cs:9`.

This contradicts the issue acceptance criterion requiring all ACP terminology to
be removed, as well as `acp-removal/spec.md`'s requirement that developer-facing
comments and diagnostics not name ACP. Remove or rewrite these historical
implementation comments/diagnostics. Keep only the narrowly required
`mohist/acp-agent` recognition used to produce the actionable pre-cutover
WorkflowRun and custom-profile migration error; that compatibility error is
explicitly required by the same spec.

## Verification

- `npm run typecheck -w packages/runner` passed.

<promise>FAIL</promise>
