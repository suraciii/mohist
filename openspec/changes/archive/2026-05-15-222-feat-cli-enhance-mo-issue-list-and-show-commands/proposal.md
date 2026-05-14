## Why

CLI users need fast terminal-first issue triage before deciding where to intervene, but the current issue commands require broad list scans, long detail output, or full patch output for common status checks. This change adds lightweight query and summary paths so users can quickly find active work, identify issues needing attention, and size review work without changing the workflow model.

## What Changes

- Add `mo issue list -s active` as a query alias for pipeline issues that have started and are not yet delivered, excluding backlog-active issues.
- Allow `mo issue list -s/--status` to accept comma-separated stages or aliases, with OR semantics inside the stage selection and clear errors for unknown names.
- Add `mo issue list --attention` to show issues that need user action or decision, including awaiting approval, blocked or interrupted issues, failed delivery/integration, and done/completed issues that are not merged.
- Keep stage/attention filtering composable with priority, label, archived, and all issue scopes.
- Add `mo issue show <id> --compact` for a single-line human-readable summary that omits body, comments, checks, approval output, and other long sections.
- Add `mo issue diff <id> --stat` to show file-level change statistics without printing the full patch, using the same base/head comparison semantics as the full issue diff.
- Preserve default `mo issue list`, `mo issue show <id>`, and `mo issue diff <id>` behavior unless the new options are used.
- Update CLI help text for the new flags, multi-stage status values, and `active` alias.

## Capabilities

### New Capabilities



### Modified Capabilities

- cli-interface

## Impact

- `packages/cli/src/cli/commands/issue.ts`: extend issue list option parsing/output, add `--attention`, add compact show output, add diff stat mode, and update help text.
- `packages/cli/src/api/issues.ts`: extend `GET /api/issues` query handling for multi-stage selection, the `active` alias, attention filtering, validation errors, and composition with existing filters.
- `packages/cli/src/services/issue-service.ts` and `packages/cli/src/db/issue-repo.ts`: may need query helpers for multi-stage and attention scope if filtering is kept server-side.
- `packages/cli/src/types/index.ts` and workflow delivery helpers: reuse existing stage, status, approval, and merge delivery states without adding a workflow stage or identity model.
- Diff behavior should align CLI output with existing issue diff comparison semantics exposed by `GET /api/issues/:number/diff`, including distinct unavailable/no-change feedback.
- Tests should cover CLI command behavior and server filtering semantics for aliases, invalid ranges, attention scope, compact output, and diff stat output.
