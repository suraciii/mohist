## Why

Creating or updating an issue with a substantial Markdown body currently forces users to fight shell quoting instead of describing the work, which makes a core CLI workflow fragile exactly when users need it for high-quality issue capture. This should be fixed now because users are already working around it manually, the `mohist-po` skill has had to warn about it explicitly, and related CLI friction around priority parsing, exit codes, and post-create guidance is making everyday issue authoring less scriptable and less forgiving.

## What Changes

- Extend `mo issue create` and `mo issue update` so `--body` can accept literal text, `@file` references, and `-` for stdin, while preserving existing plain-string behavior
- Add an explicit `--body-file <path>` input path for issue creation so long-form content can be passed without shell escaping
- Normalize CLI and API issue priority input case-insensitively so `P0`-`P4` and `p0`-`p4` are treated equivalently for create, update, and list filtering
- Tighten CLI argument validation so invalid inputs fail with a non-zero exit code instead of printing an error and silently succeeding from a script's perspective
- Improve successful issue creation output with a next-step hint to start processing when the created issue is still in backlog/draft
- Update Mohist skill guidance to recommend file-backed issue body input for long Markdown content

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- **cli-interface**: Issue CLI commands gain file/stdin body input behavior, case-insensitive priority handling, stricter non-zero validation failures, and post-create guidance output
- **http-api**: Issue endpoints accept case-insensitive priority values for create, update, and list filtering so CLI and direct API clients observe the same contract
- **local-issue-store**: Local issue create/update behavior now includes body ingestion from CLI file/stdin sources without changing stored body semantics

## Impact

- **CLI commands**: `packages/cli/src/cli/commands/issue.ts` for `create`, `update`, and `list` option parsing, validation, exit behavior, and success messaging
- **HTTP API**: `packages/cli/src/api/issues.ts` for `GET /api/issues`, `POST /api/issues`, and `PATCH /api/issues/:number` priority normalization and validation behavior
- **Shared types/contracts**: `packages/cli/src/types/index.ts` and related validation helpers/constants used by CLI and API priority parsing
- **Issue persistence path**: existing issue body storage remains in the current local issue flow (`IssueService`/`IssueRepo`), but the ingest path into that storage changes at the CLI boundary
- **Agent skill docs**: `.agents/skills/mohist/SKILL.md` and related guidance that currently works around shell quoting limitations
- **Scripted integrations**: shell scripts and automation invoking `mo issue create` or `mo issue update` can reliably detect validation failures via exit status and pass rich Markdown bodies via files or pipes
