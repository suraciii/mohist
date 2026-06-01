# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: packages/web/src/entities/issue/api/client.ts
  Evidence: `getFileContent()` now calls `/api/issues/{number}/workflow/file-content?path=...`, matching the issue-scoped workflow artifact endpoint used by the new required-file viewer. Before this repair, the client pointed at `/issues/{number}/file-content`, which would have broken the acceptance criterion that file content loads on demand through the existing scoped workflow API.
  Verification: `npm run test:run -- WorkflowTaskArtifact.test.tsx` (in `packages/web`)
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: packages/web/src/widgets/issue-workflow/ui/WorkflowTaskArtifact.test.tsx
  Evidence: Added an explicit assertion that the viewer requests file content with `(issueNumber, filePath, projectId)`, which closes the regression gap around project/issue scoping for on-demand artifact fetches.
  Verification: `npm run test:run -- WorkflowTaskArtifact.test.tsx` (in `packages/web`)
  Status: resolved

## Blocking Items

- None.

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: verification environment
  Evidence: The root `npm test` script runs `dotnet test Mohist.sln`, so focused web verification must be run from `packages/web` via Vitest rather than through the root script. An attempted `npm test -- --runInBand ...` invocation fails with `MSBUILD : error MSB1001: Unknown switch.` This did not affect the candidate; the correct focused commands passed.
  SuggestedAction: Optional future cleanup would be adding a root convenience script for focused web tests.
  Status: out-of-scope

<promise>PASS</promise>
