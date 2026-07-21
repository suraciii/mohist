## Review

No findings. The change preserves active-run-only command resolution while adding a read-only, label-scoped historical fallback for metadata and transcripts. The fallback enforces project, issue, workflow source-kind, and session-name boundaries and selects the latest persisted record by creation time.

Validated with:

- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore --filter FullyQualifiedName~IssueWorkflowSessionHistorySpecs` (8 passed)
- `npm test` completed all .NET tests successfully; the supplied name filter was then forwarded to unrelated web and runner Vitest suites, where no matching tests exist.

<promise>PASS</promise>
