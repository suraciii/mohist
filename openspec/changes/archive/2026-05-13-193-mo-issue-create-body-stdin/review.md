# Review: #193 — mo issue create --body supports file references and stdin

## Summary

The implementation adds three body input modes (`@file`, `--body-file`, `-` for stdin), case-insensitive priority normalization, non-zero exit codes on validation failures, and a post-create start hint. All changes are well-scoped, correctly implemented, and thoroughly tested.

## Correctness

No logic errors found. The `ingestBody` helper at `issue.ts:18-56` correctly handles all input modes: mutual exclusion check (line 19), stdin `-` detection (line 22), `@file` prefix (line 25), explicit `--body-file` (line 29), and literal string passthrough (line 32). The `normalizePriority` helper at `types/index.ts:46-53` correctly normalizes to lowercase and validates against `VALID_PRIORITIES`, returning `null` for `undefined`/`null`/empty/invalid values.

The `isStartable` function at `issue.ts:58-60` correctly checks `issue.stage === Stage.Backlog`. The Stage enum (`types/index.ts:1-8`) has no "Draft" stage — Backlog is the only initial stage — and `mo issue start` only allows starting from Backlog (`issues.ts:996`), so the hint is only shown when it's actionable.

## Complexity

All functions are well under 50 lines. `ingestBody` (15 lines), `ingestFile` (12 lines), `ingestStdin` (8 lines), `isStartable` (3 lines). Cyclomatic complexity is low throughout. No concerns.

## Test Coverage

25 tests pass in `issue-body-stdin-regression.test.ts`:

- `ingestBody`: `@file` read, `@file` not found, `--body-file` read, `--body-file` not found, stdin `-` returns promise, literal string passthrough, mutual exclusion error — **all covered**
- `normalizePriority`: uppercase P0-P4, lowercase p0-p4, invalid values, undefined — **all covered**
- API routes: POST with uppercase priority (P0/P2/P4), POST with invalid priority (400), PATCH with uppercase priority (P1/P3), PATCH with invalid priority (400), GET with uppercase filter (P1/P3), GET with invalid filter (400) — **all covered**
- API body passthrough: body content preserved through POST — **covered**

Not tested but acceptable: `process.exit(1)` calls in CLI handlers (hard to test without mocking process.exit), `isStartable` tip output (presentation logic, depends on API response shape).

## Security

No injection risks. File reads use `path.resolve()` for normalization. The CLI runs as the current user, so filesystem access is already implicitly authorized. No secrets exposed. Input validation rejects invalid priorities at both CLI and API boundaries.

## Spec Compliance

### cli-interface/spec.md

| Criterion | Verdict | Evidence |
|---|---|---|
| `--body @file.md` reads file as body | PASS | `issue.ts:25-27` — `startsWith('@')` → `ingestFile` |
| `--body-file body.md` reads file as body | PASS | `issue.ts:29-31` — `bodyFileOpt` → `ingestFile` |
| `--body -` reads stdin as body | PASS | `issue.ts:22-24` — `bodyOpt === '-'` → `ingestStdin` |
| `mo issue update --body @file.md` | PASS | `issue.ts:506` — `ingestBody(options.body, undefined)` |
| `mo issue update --body -` | PASS | Same code path via `ingestBody` |
| Literal body preserved | PASS | `issue.ts:32` — falls through to `{ body: bodyOpt }` |
| Create accepts uppercase priority | PASS | `issue.ts:197` — `normalizePriority(options.priority)` |
| Update accepts uppercase priority | PASS | `issue.ts:501` — same normalization |
| List accepts uppercase priority filter | PASS | `issue.ts:241` — same normalization |
| Invalid priority fails exit code 1 | PASS | `issue.ts:199-200` — `process.exit(1)` |
| Missing body file fails exit code 1 | PASS | `issue.ts:203-205` — `process.exit(1)` |
| Conflicting body sources fail exit code 1 | PASS | `issue.ts:19-20` → `issue.ts:204-205` |
| Start tip shown for startable issue | PASS | `issue.ts:218-220` — `isStartable(issue)` gates the tip |
| Start tip omitted for non-startable issue | PASS | `isStartable` returns false for non-Backlog stages |

### http-api/spec.md

| Criterion | Verdict | Evidence |
|---|---|---|
| POST accepts uppercase priority | PASS | `issues.ts:428-436` — `normalizePriority(priority)` |
| PATCH accepts uppercase priority | PASS | `issues.ts:814-823` — same normalization |
| Reject invalid create priority (400) | PASS | `issues.ts:429-435` — returns 400 |
| Reject invalid update priority (400) | PASS | `issues.ts:815-822` — returns 400 |
| GET accepts uppercase priority filter | PASS | `issues.ts:361-367` — `normalizePriority(priorityInput)` |
| Reject invalid list priority filter (400) | PASS | `issues.ts:362-368` — returns 400 |

### local-issue-store/spec.md

| Criterion | Verdict | Evidence |
|---|---|---|
| Body from file stored as plain text | PASS | `ingestBody` resolves to string before API call; store receives plain text |
| Body from stdin stored as plain text | PASS | Same — resolution at CLI boundary |
| No `@file` token persisted | PASS | Design: API contract unchanged, no CLI syntax leaks through |

### mohist-skill-guidance/spec.md

| Criterion | Verdict | Evidence |
|---|---|---|
| Recommends `--body @file.md` as default | PASS | `SKILL.md:50` — `mo issue create "Fix X" --body @issue-body.md` |
| Documents `--body -` stdin workflow | PASS | `SKILL.md:51-52` — stdin and pipe examples |
| Heredoc as fallback only | PASS | `SKILL.md:56-64` — "heredoc 作为兼容性备选" |

## Warnings

1. **List command API error does not exit non-zero** (`issue.ts:310-312`): The `catch` block in the list action handler prints the error but returns with exit code 0. Design D3 says operational failures in touched commands should also exit non-zero. This is a minor deviation since the spec's acceptance criteria only mandates non-zero for validation failures (which correctly exit with code 1), and a failed list is less harmful than a silently-failed create.

2. **No `--body-file` on update**: The design explicitly scopes `--body-file` to create only (D1), and the spec does not require it on update. Update supports `--body @file.md` and `--body -` via the shared `ingestBody` helper. This is correct but worth noting for future symmetry consideration.

## Build & Tests

- `npm run build` — PASS (clean build)
- `npx tsc --noEmit` — PASS (no type errors)
- 25/25 regression tests — PASS

<promise>PASS</promise>
