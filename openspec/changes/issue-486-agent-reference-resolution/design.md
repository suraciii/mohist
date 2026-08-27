# Design

## Context

The CLI already exposes `AgentCommands.ResolveAgentAsync`, which resolves a project-scoped Agent reference and returns the stable Agent id plus name. It uses the id route for an Agent id and the project Agent collection for a name, and reports `Agent '<input>' not found` when no match exists.

Routing-rule create and edit currently send `ctx.GetValue(agent)` as `agentId` without calling that resolver. Create and edit therefore do not share the name-or-id boundary used by other Agent commands.

There are two independent edit-path defects in current master. The edit command sends a nullable anonymous object, so omitted options can be serialized as JSON `null`. The Server `RoutingRuleUpdateRequest` binder discovers present JSON fields using `nameof(Name)`, `nameof(Match)`, `nameof(AgentId)`, and `nameof(ResponsePrompt)`, while `RoutingRuleStore.UpdateAsync` checks lower-case parameter names for those same fields. That vocabulary/casing mismatch can ignore or invalidate supplied name, match, AgentId, and response-prompt updates. The approved create/edit end-to-end contract therefore permits this minimal Server correction; it does not require a broader backend feature.

## Goals

- Reuse the existing project-scoped resolver for create and edit.
- Ensure an Agent name and its stable id produce the same outbound `agentId`.
- Ensure an explicit Agent value is resolved before the PATCH is sent.
- Preserve PATCH omission semantics: fields not supplied by the CLI are absent from JSON.
- Make Server PATCH presence tracking and application use one canonical JSON vocabulary.
- Keep this one standalone routing-rule Agent-reference value issue.

## Non-Goals

- Adding a backend Agent name lookup, fallback resolver, Agent routing service, or new endpoint.
- Changing the routing DSL, event-match expression semantics, routing-engine behavior, or rule-reference format.
- Changing routing rule ids, ordering, archive, move, list, view, dry-run, or output behavior.
- Changing Agent identity, project resolution, or the shared resolver's matching rules.
- Adding compatibility aliases for PascalCase or underscore JSON fields.

## Decisions

### 1. Reuse the existing project-scoped resolver

For `routing rule create`, resolve the supplied `--agent` after project resolution and before posting the rule. For `routing rule edit`, resolve a supplied `--agent` after project resolution and before patching the rule. The request body continues to contain `agentId`; only its value changes from raw input to `AgentRef.Id`.

The existing resolver remains authoritative for id-vs-name detection, project scoping, archived-agent inclusion, matching, transport failures, and error text. No routing-specific lookup or backend fallback is added.

### 2. Encode CLI presence, not nullable defaults

Create keeps `--agent` required and always resolves it before POST, including when the input already looks like an id. Its required `name`, `match`, `agentId`, and `responsePrompt` fields remain present, with `continue` retaining the existing create behavior.

Edit builds a `JsonObject` only for options whose parse result is present. A missing `--name`, `--match`, `--agent`, `--response-prompt`, or `--continue` is absent from JSON and leaves that field unchanged. A supplied `--agent` is resolved first, then the resolved stable id is added as `agentId`; the raw name is never sent as the id. Presence is distinct from value: if a caller sends a JSON `null` directly, the Server treats that property as present and applies its existing validation/normalization rules rather than treating it as omitted.

### 3. Use one canonical Server PATCH vocabulary

The routing-rule PATCH contract has exactly these editable JSON property names:

- `name`
- `match`
- `agentId`
- `responsePrompt`
- `continue`

`RoutingRuleUpdateRequest.Raw` reads these names, `Fields` contains these exact names, and `RoutingRuleStore.UpdateAsync` checks these same names when deciding which values to apply. C# member names are not presence tokens. A present property is applied, including a present `null` according to existing validation; an absent property preserves the stored value. The existing Server stable-Agent-id lookup and routing validation remain unchanged.

This is the minimum correction needed for the CLI PATCH contract. It does not add name lookup, routing DSL behavior, or a new API field.

### 4. Preserve all other routing fields

`name`, `match`, `responsePrompt`, `continue`, project query, position query, and output selection remain unchanged except for the explicit edit omission and canonical presence behavior above. Rule references, ordering, archive, move, and the routing engine continue to use the existing contracts.

## Verification

Future tests MUST use fake HTTP and a project-scoped Agent fixture, with no network, process, database, or wall-clock dependency in CLI tests. Server contract tests may use the existing in-memory test database and fake/injected time.

Focused CLI tests MUST assert:

1. create by Agent name performs resolution before POST and sends the stable `agentId`;
2. create by Agent id performs the same resolver boundary and sends the same stable `agentId`;
3. edit by Agent name and edit by Agent id both resolve before PATCH and send identical stable `agentId` values;
4. an edit with only `--name`, `--match`, `--response-prompt`, or `--continue` sends exactly the supplied canonical JSON properties and no omitted nullable properties;
5. unknown create/edit input returns non-zero with the original input and sends no mutation; and
6. project resolution precedes Agent resolution and mutation.

Focused Server contract tests MUST assert that PATCH bodies containing each of `name`, `match`, `agentId`, and `responsePrompt` (and `continue`) apply that field, while an absent field remains unchanged. They MUST assert canonical `Fields` tokens exactly match the JSON names and cover a present `null` as present rather than omitted.

The focused C# checks use the compiled xUnit v3 apphosts, not a solution-level selector. The authoritative existing CLI class is `Mohist.Cli.Tests.CliRoutingCommandSpecs`. Server store application belongs to the existing `Mohist.Server.SpecTests.Specs.Agent.Services.RoutingRuleStoreSpecs`. The current `Mohist.Server.SpecTests.Specs.Api.RoutingTestRoutesSpecs` owns the `/routing/test` surface and does not own `RoutingRuleUpdateRequest.BindAsync`; implementation MAY add the narrowly named `Mohist.Server.SpecTests.Specs.Api.RoutingRulePatchRoutesSpecs` for the PATCH route binder/presence contract.

Build each test project before invoking its executable apphost:

```bash
dotnet build packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore
dotnet build packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore -p:SkipWebBuild=true
```

Every focused invocation MUST be wrapped by the surrounding shell timeout and strict automated-output checks below. `TestCasesToRun` is the discovery total; the final assembly event must report a nonzero `TestsTotal` and zero `TestsFailed`, `TestsSkipped`, and `TestsNotRun`. Run each selected class separately so a missing intended class cannot be hidden by another class's passing tests.

```bash
set -euo pipefail
run_focused() {
  local apphost="$1"
  shift
  local output summary
  test -x "$apphost"
  output="$(timeout -k 10s 120s "$apphost" "$@" -automated sync -noColor -noLogo -noAutoReporters 2>&1)"
  printf '%s\n' "$output"
  grep -Eq '"\$type":"discovery-complete".*"TestCasesToRun":[1-9][0-9]*' <<<"$output"
  summary="$(grep '"\$type":"test-assembly-finished"' <<<"$output" | tail -n 1)"
  grep -Eq '"TestsFailed":0.*"TestsNotRun":0.*"TestsSkipped":0.*"TestsTotal":[1-9][0-9]*' <<<"$summary"
}

run_focused packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -class Mohist.Cli.Tests.CliRoutingCommandSpecs
run_focused packages/server/tests/Mohist.Server.SpecTests/bin/Debug/net11.0/Mohist.Server.SpecTests \
  -class Mohist.Server.SpecTests.Specs.Agent.Services.RoutingRuleStoreSpecs
run_focused packages/server/tests/Mohist.Server.SpecTests/bin/Debug/net11.0/Mohist.Server.SpecTests \
  -class Mohist.Server.SpecTests.Specs.Api.RoutingTestRoutesSpecs
run_focused packages/server/tests/Mohist.Server.SpecTests/bin/Debug/net11.0/Mohist.Server.SpecTests \
  -class Mohist.Server.SpecTests.Specs.Api.RoutingRulePatchRoutesSpecs
```

The implementation may use `-method` with an authoritative fully qualified method name for a narrower debugging run, but acceptance must retain the class coverage above. Keep the full repository gates:

```bash
npm run docs:check
npm run archtest
npm run test:fast
npm run verify
```

## Risks and Trade-offs

- Resolving a name requires an additional project Agent lookup before mutation. This is required for stable identity and reuses the existing CLI contract.
- A concurrent rename or archive between lookup and mutation remains governed by existing Server validation; this change does not add a consistency protocol.
- Existing callers that relied on omitted edit options becoming JSON `null` now receive correct omission semantics; explicit direct JSON `null` remains a present field governed by Server validation.

## Migration Plan

1. Call the existing resolver from routing-rule create/edit and pass `AgentRef.Id` to the existing mutation body.
2. Build edit JSON only from provided CLI options.
3. Align Server PATCH `Raw`, `Fields`, and store presence checks on the five canonical JSON names.
4. Add focused CLI and Server request/presence tests.
5. Run the exact focused commands above and the full verification gate.
6. Confirm no broader backend Agent lookup, routing DSL, rule-reference, or unrelated implementation files changed.

Rollback is a source revert of the CLI call-site, minimal Server presence correction, and focused tests. No data migration or API expansion is required.

## Open Questions

None. The existing resolver and the five canonical JSON field names are the approved seams.
