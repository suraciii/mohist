## Why

`GET /api/issues/:number/commits` returns incorrect data due to a git log parsing bug: `\x01` separator splits between a commit's header and its stat lines rather than after them, causing N commits to collapse into 1 entry with `filesChanged=0, +0, -0`. This blocks the UI from showing commit history for any issue.

## What Changes

- Replace `\x01` separator in `git log --format` with a unique text delimiter (`----COMMIT----`) that splits at commit boundaries, preserving stat lines within each entry
- Fix the parsing loop in `packages/cli/src/api/issues.ts:1346-1372` to correctly extract header fields and stat summary from each complete entry

## Capabilities

### New Capabilities

### Modified Capabilities

- `http-api` — fixes the `/issues/:number/commits` endpoint response to return all commits with correct file change statistics

## Impact

- `packages/cli/src/api/issues.ts` lines 1336-1372 (single route handler)
- No breaking API changes — response shape stays identical, only the data correctness changes
- No frontend changes needed
- No dependency changes
