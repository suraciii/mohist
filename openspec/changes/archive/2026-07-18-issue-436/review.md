# Review

No merge-blocking findings.

The runner now keeps the server-expanded `with` as `ActionContext.rawWith`, while
continuing to provide the recursively rendered form as `with`. `mohist/openspec-tasks`
uses the preserved form for the propagated `task` subtree, so generated task inputs
retain whole-string workflow-variable placeholders for later dispatch and retry.

Coverage includes the executor split-context contract, the openspec task propagation
case, retry after a stage-variable change, and the legacy baked-input behavior.

Verified:

- `npm run typecheck -w packages/runner`
- `npm test -w packages/runner` (1186 passed)
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-build --filter "FullyQualifiedName~DispatchAndLoadingSpecs"` (20 passed)
- The full .NET solution test portion of `npm test` passed, including 2825 server specs. The enclosing command later failed only because its .NET filter was forwarded to unrelated Web and Runner Vitest suites, where no matching test files exist.

<promise>PASS</promise>
