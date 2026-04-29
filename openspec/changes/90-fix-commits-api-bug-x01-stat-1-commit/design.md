## Context

`GET /api/issues/:number/commits` runs `git log --format='%h%x00%s%x00%an%x00%aI\x01' --stat` and splits the output on `\x01`. With `--stat`, git emits the separator between the commit header and its stat lines (not after the stat lines), so splitting on `\x01` dissociates each header from its own stats. This causes N commits to collapse into ~1 usable entry with `+0 -0`.

## Goals / Non-Goals

**Goals:**
- Fix the git log parsing so every commit entry retains its header + stat as a unit
- Preserve the existing response shape (no API contract changes)

**Non-Goals:**
- Refactoring the `/commits/:hash/diff` endpoint (separate concern)
- Adding new response fields or pagination
- Handling merge commits specially

## Decisions

### D1: Use `----COMMIT----` text delimiter instead of `\x01`

Change the format string from `--format=%h%x00%s%x00%an%x00%aI\x01` to `--format=----COMMIT----%h%x00%s%x00%an%x00%aI`. Split the output on `----COMMIT----` so each chunk contains exactly one commit's header line followed by its stat lines. The leading empty string from the split (before the first marker) is discarded by the existing `.filter(e => e.trim())`.

**Rationale:** `--format` strings are emitted at the *start* of each commit's output block, before any diff/stat content. A text marker at the start guarantees the split happens at true commit boundaries, unlike a trailing separator which `--stat` pushes into the gap between header and stats.

**Alternatives considered:**
- **Two-pass approach (separate `git log` for headers + `git log --stat` for stats):** Correct but doubles the number of git invocations and requires correlating by hash.
- **`--format` with `\x00` separator and strip first line:** `\x00` could collide with `--stat` output on some git versions. Text delimiter is more robust.
- **`git log --numstat` instead of `--stat`:** Would require reimplementing the stat summary parsing. More work for no real benefit.

### D2: Skip the first split result (empty string before the first marker)

The output starts with `----COMMIT----`, so splitting on it produces an empty leading entry. The parsing loop SHALL skip entries where `trim()` yields an empty string (already handled by the existing `.filter(e => e.trim())` pattern, just changing the delimiter).

## Risks / Trade-offs

- [Delimiter collision] → `----COMMIT----` is extremely unlikely to appear in commit messages or stat output. If it ever did, it would only cause that single commit to be split incorrectly, not a systemic failure.

## Migration Plan

Single PR, direct replacement. No database changes, no config changes. Deploy and verify by calling the endpoint on an existing issue with known commits.

## Open Questions
