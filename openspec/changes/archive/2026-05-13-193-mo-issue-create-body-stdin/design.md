## Context

`mo issue create` and `mo issue update` currently treat `--body` as a literal CLI string. That is acceptable for short text, but it breaks down for the exact issue bodies Mohist encourages users to write: Markdown with code fences, shell snippets, ASCII mockups, pipes, quotes, and dollar signs. Users are already working around this manually, and the `mohist-po` skill explicitly warns about shell quoting for long bodies.

The implementation surface is broader than one flag parser. The CLI currently validates priority in `packages/cli/src/cli/commands/issue.ts`, the API validates it again in `packages/cli/src/api/issues.ts`, and the create/update/list flows each handle user input independently. The design should keep the stored issue body model unchanged while making CLI ingestion more flexible and making priority/error behavior consistent across CLI and API.

No spec markdown files exist yet under this change's `specs/` directory, so this design uses the proposal and embedded acceptance criteria as the source of truth for scope.

## Goals / Non-Goals

**Goals:**

- Support three issue body input modes for CLI issue authoring: literal string, `@file` reference, and `-` for stdin.
- Add explicit `--body-file <path>` support for `mo issue create` while keeping `--body` backward compatible.
- Apply the same body input behavior to `mo issue update` for `--body`.
- Normalize priority input case-insensitively in both CLI and API for create, update, and list filtering.
- Make CLI argument validation fail with exit code `1` so scripting can reliably detect failures.
- Improve successful issue creation output with a next-step hint when the new issue is still startable.
- Update shared `mohist` skill guidance to recommend file-backed body input for long Markdown.

**Non-Goals:**

- Changing issue persistence, issue schema, or how bodies are stored after they reach the service layer.
- Adding rich body input support to unrelated commands in this change, even if the same helper could later be reused elsewhere.
- Introducing server-side file upload or stdin concepts into the HTTP API; file/stdin resolution remains a CLI concern.
- Redesigning all CLI error handling patterns across the repo beyond the issue flows touched by this change.

## Decisions

### D1: Resolve body input at the CLI boundary before calling the API

The CLI should translate user-facing body input forms into a plain string before sending the API request. This keeps the API contract unchanged: `POST /issues` and `PATCH /issues/:number` continue to receive `body?: string`, with no awareness of `@file`, `-`, or local filesystem paths.

The resolution rules are:

- `--body @path/to/file.md` reads the referenced file as UTF-8 text.
- `--body -` reads the full request body from stdin.
- `--body <anything-else>` preserves the current literal-string behavior.
- `--body-file <path>` reads the file as UTF-8 text and is only accepted on `create`.

`--body-file` and `--body` should be treated as mutually exclusive input sources for body content. That avoids ambiguous precedence rules and keeps the UX predictable. A small shared CLI utility should encapsulate this behavior so `create` and `update` do not drift.

This design keeps the change minimal: the only new behavior is in CLI parsing and file/stdin ingestion, while persistence and API payload shape stay the same.

**Alternatives considered:**

- Teach the API to accept `@file` and `-`. Rejected because those are shell-local concepts and would leak CLI semantics into HTTP clients.
- Add only `--body-file` and skip `@file` support. Rejected because curl-style `@file` is the most discoverable and lowest-friction form for users already trying `--body`.
- Support stdin implicitly whenever `process.stdin` is not a TTY. Rejected because it can block unexpectedly and makes simple invocations harder to reason about; explicit `--body -` is safer.

### D2: Centralize normalization helpers, but keep validation ownership at each boundary

Priority parsing is currently duplicated in CLI and API. This change should introduce a small shared normalization helper for issue priority values that:

- accepts `undefined`
- lowercases string input
- validates against `VALID_PRIORITIES`
- returns either a normalized `Priority` or a validation failure

Both CLI and API should use the same normalization helper, but each boundary should still decide how to report errors in its own transport:

- CLI prints a user-facing message and exits with code `1`
- API returns a `400` response with an invalid-priority error

The list flow should normalize before building the query string, and the API list handler should normalize again before filtering. That preserves consistency for direct API consumers and prevents the CLI from being the only forgiving layer.

This same pattern applies to body resolution errors: the CLI helper should surface a structured error, and command handlers should convert that into a clear message plus non-zero exit.

**Alternatives considered:**

- Keep separate lowercase logic in CLI and API. Rejected because the proposal already identifies drift as part of the problem.
- Move all validation exclusively to the API. Rejected because CLI users would lose immediate feedback for obvious local mistakes and scripts would still need to depend on remote failure paths.
- Add a generic repo-wide validation framework. Rejected as too large for a narrow CLI/API ergonomics change.

### D3: Use explicit non-zero exits for argument/ingestion failures in the touched issue commands

For `create`, `update`, and `list`, any user input validation failure introduced or touched by this change should terminate with `process.exit(1)` after printing a concise error. That includes:

- invalid priority
- nonexistent or unreadable body file
- invalid combination of `--body` and `--body-file`
- missing required positional input already enforced in command logic
- stdin read failures when `--body -` is used

This is intentionally narrower than a repo-wide CLI error-handling refactor. The goal is to make issue-authoring automation reliable now without redesigning every command.

Operational failures after a valid request is sent, such as API errors, should also continue to surface as failures; if the existing command already catches and prints them, the implementation should ensure those paths also produce a non-zero exit for the touched commands so shell scripts do not silently succeed.

**Alternatives considered:**

- Continue returning from handlers without exiting. Rejected because it preserves the current scripting bug.
- Throw errors and rely on Commander/global handlers. Rejected because this command file already uses local `try/catch` handling and a local explicit pattern is the smallest safe change.

### D4: Gate the post-create hint on the created issue's actual lifecycle state

The success message should remain concise but should add a follow-up hint only when the newly created issue is still in a startable state. The API already returns the created issue object, so the CLI can decide from actual issue data rather than from assumptions.

The rule is:

- Always print `Created issue #N: <title>` and priority.
- Print `Tip: Run 'mo issue start <number>' to begin processing` only when the created issue is in `draft` or `backlog`-equivalent idle state.
- Do not print the hint for issues already in another workflow stage.

This avoids misleading users in future cases where issue creation may evolve to produce issues outside the default initial stage.

**Alternatives considered:**

- Always print the hint. Rejected because the acceptance criteria explicitly exclude non-backlog cases.
- Push this formatting decision into the API response. Rejected because it is a CLI presentation concern.

### D5: Update the shared `mohist` skill documentation, not command help text alone

The proposal calls out that the `mohist-po` skill has already adapted to the current limitation. The permanent fix should therefore update the shared skill guidance so agents and humans converge on the same preferred pattern for long Markdown bodies.

The skill update should recommend:

- `--body @file.md` as the default long-body workflow
- `--body -` for piped content
- heredoc or command substitution only as compatibility fallbacks

This guidance belongs in the skill artifact because that is where issue-authoring behavior is currently taught. Command `--help` text can remain short and point users at the feature, but the richer workflow advice should live in the skill docs.

**Alternatives considered:**

- Update only the CLI help strings. Rejected because it would not fix the agent guidance gap the proposal highlights.
- Update every skill that might consume text files. Rejected because only `mohist` is in scope for this user-facing issue-authoring change.

## Risks / Trade-offs

- [Reading stdin can block if the user accidentally passes `--body -` interactively] → Require explicit `-` and document it as a pipe-oriented mode.
- [Treating leading `@` specially could surprise users who want a literal body beginning with `@`] → Preserve literal behavior for all non-file cases and document `--body-file` as the escape hatch for explicit file intent; if needed later, literal-leading-`@` escaping can be added as a follow-up.
- [CLI and API may still drift if normalization logic is copied instead of shared] → Put normalization in a small shared helper imported by both boundaries.
- [Switching touched commands to non-zero exit codes may break scripts that accidentally relied on silent success] → This is the intended contract correction; keep messages explicit so failures are easy to diagnose.
- [Successful hint logic may become stale if initial issue stage semantics change] → Base the hint on returned issue state rather than hardcoded create-path assumptions.

## Migration Plan

1. Add a shared CLI helper that resolves issue body input from literal text, `@file`, `--body-file`, or stdin.
2. Update `mo issue create` to use the helper, add `--body-file`, reject ambiguous body-source combinations, and print the conditional start hint.
3. Update `mo issue update` to use the same helper for `--body` and to exit non-zero on validation or ingestion failures.
4. Introduce a shared priority normalization helper and apply it in CLI `create`, `update`, `list`, plus API create/update/list handlers.
5. Adjust touched issue command failure paths so validation and request failures exit with code `1`.
6. Update `.agents/skills/mohist/SKILL.md` guidance to recommend `--body @file` and stdin for long issue bodies.
7. Add tests covering file body input, stdin body input, missing files, case-insensitive priority handling in CLI/API, non-zero exits, and conditional success hint rendering.

Rollback is low risk because no stored data format changes. Reverting the helper usage, priority normalization changes, and skill docs would restore the prior behavior.

## Open Questions

- Should `mo issue update` also gain an explicit `--body-file <path>` option for symmetry with `create`, or is supporting `@file` on `--body` sufficient for this change? The proposal explicitly requires `--body-file` on create, but only `@file` on update.
