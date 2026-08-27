# Self-Review

## Scope

This remains one standalone routing-rule Agent-reference value issue. It resolves CLI Agent names and ids consistently and includes only the minimum Server PATCH presence correction required for that value to work end to end. It does not add a backend Agent resolver, fallback routing, routing DSL, rule-reference, or routing-engine change.

## Findings

- Current evidence identifies both the CLI and Server seams: nullable CLI edit fields can serialize as `null`, and Server PATCH presence tokens use mismatched property vocabulary/casing.
- Create and edit have independent scenarios for Agent name and Agent id.
- Both forms resolve to the same stable Agent id before mutation.
- Unknown input preserves the original value, fails non-zero, and prevents POST/PATCH.
- Edit omission is tested as absence from JSON and remains distinct from supplied-reference resolution.
- Server `Raw`, `Fields`, and store checks share exactly `name`, `match`, `agentId`, `responsePrompt`, and `continue`.
- Project resolution stays ahead of Agent resolution and routing mutation.
- Focused verification names the existing CLI and Server store classes, and permits only the narrowly named `Mohist.Server.SpecTests.Specs.Api.RoutingRulePatchRoutesSpecs` addition for the currently unowned PATCH binder/presence contract.
- Every focused C# check uses a compiled apphost, an explicit shell timeout, automated/no-color output, and strict nonzero discovery plus zero failed/skipped/not-run assertions; no solution-level selector is prescribed.
- Future tests intentionally remain `passes: false`; no product, test, or documentation implementation files are changed in this spec-only repair.

## Residual Risks

- Name resolution adds a read before mutation and remains subject to the existing Server race behavior if the Agent changes between requests.
- A direct JSON `null` is present and follows existing Server validation; only an absent property means unchanged.
- The minimal Server correction must not expand into backend name lookup or routing DSL work during implementation.

## Verdict

No blocking finding. The artifacts describe one standalone value, its two evidenced boundary seams, exact canonical JSON names, focused CLI/Server contracts, and bounded implementation scope.
