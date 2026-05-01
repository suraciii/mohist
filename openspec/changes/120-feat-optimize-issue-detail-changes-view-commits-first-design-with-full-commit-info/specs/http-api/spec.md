## MODIFIED Requirements

### Requirement: Commits API returns per-commit file list

`GET /api/issues/:number/commits` SHALL parse per-file lines from `git log --stat` output and return a `files: string[]` field in each commit entry, containing the file paths changed by that commit. The existing fields (`hash`, `message`, `author`, `date`, `filesChanged`, `additions`, `deletions`) SHALL remain unchanged.

#### Scenario: Commit with file list

- **WHEN** `GET /api/issues/5/commits` is called
- **AND** a commit changed `src/foo.ts` and `src/bar.ts`
- **THEN** that commit entry includes `files: ["src/foo.ts", "src/bar.ts"]`
- **AND** all other fields (`hash`, `message`, `author`, `date`, `filesChanged`, `additions`, `deletions`) remain present

#### Scenario: Commit with no file changes (merge commit)

- **WHEN** a merge commit has no file changes
- **THEN** that commit entry includes `files: []`

#### Scenario: Commits API backward compatible

- **WHEN** `GET /api/issues/:number/commits` is called
- **THEN** the response shape is `{ success: true, data: { commits: [...] } }`
- **AND** each commit entry has the same shape as before with the additional `files` field

### Requirement: Diff API returns precise stats and per-file diff content

`GET /api/issues/:number/diff` SHALL use `git diff --numstat` for precise per-file addition/deletion counts and `git diff` (unified format) for full diff content. Each file entry SHALL include a `diff: string` field containing the unified diff for that file. The response SHALL replace the previous `--stat` symbol-counting approach.

#### Scenario: Diff with precise stats

- **WHEN** `GET /api/issues/5/diff` is called
- **AND** `src/foo.ts` has 42 additions and 7 deletions
- **THEN** the file entry for `src/foo.ts` has `additions: 42` and `deletions: 7` (exact values from `--numstat`)
- **AND** the file entry includes `diff: "diff --git a/src/foo.ts b/src/foo.ts\n..."` with the full unified diff

#### Scenario: Diff with binary file

- **WHEN** the diff includes a binary file change
- **THEN** the file entry has `additions: 0` and `deletions: 0` (as `--numstat` reports `-` `-` for binary files)
- **AND** the file entry includes `diff: ""` or omits the diff field
- **AND** `isBinary: true` is set on the file entry

#### Scenario: No changes

- **WHEN** the issue branch has no changes relative to the base branch
- **THEN** the response is `{ success: true, data: { files: [] } }`

#### Scenario: Diff API response shape

- **WHEN** `GET /api/issues/:number/diff` is called with changes
- **THEN** each file entry in the response contains: `file` (string), `additions` (number), `deletions` (number), `diff` (string), `isBinary` (boolean)
