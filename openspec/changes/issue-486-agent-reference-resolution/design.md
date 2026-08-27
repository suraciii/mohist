# Design

## Context

The CLI already exposes `AgentCommands.ResolveAgentAsync`, which resolves a project-scoped Agent reference and returns the stable Agent id plus name. It uses the id route for an Agent id and the project Agent collection for a name, and reports `Agent '<input>' not found` when no match exists.

Routing-rule create and edit currently send `ctx.GetValue(agent)` as `agentId` without calling that resolver. This creates an input-boundary inconsistency: other commands accept name-or-id semantics, while routing-rule mutations implicitly require an id despite describing the option as either form.

## Goals

- Reuse the existing resolver rather than create a routing-specific lookup.
- Apply the same resolution behavior to create and edit.
- Ensure an Agent name and its stable id produce the same outbound `agentId`.
- Preserve the original unknown input in the existing diagnostic.
- Keep routing rule mutation and server contracts unchanged.

## Non-Goals

- Changing the routing DSL or event-match expression semantics.
- Changing routing rule ids, names, position references, ordering, archive, move, list, view, or dry-run behavior.
- Adding a backend resolver endpoint, API field, server fallback, or name persistence.
- Changing Agent identity, project resolution, or the shared resolver's matching rules.
- Normalizing arbitrary case or whitespace beyond what the existing resolver already does.

## Decisions

### 1. Reuse the existing project-scoped resolver

For `routing rule create`, resolve the supplied `--agent` after project resolution and before posting the rule. For `routing rule edit`, resolve a supplied `--agent` after project resolution and before patching the rule. The request body continues to contain `agentId`; only its value changes from raw input to `AgentRef.Id`.

The existing resolver remains authoritative for id-vs-name detection, project scoping, archived-agent inclusion, matching, transport failures, and error text. No new helper or duplicate list traversal is added.

### 2. Preserve update omission semantics

An edit without `--agent` continues to omit the semantic update by preserving the existing nullable option behavior. An edit with `--agent` always resolves the input and sends the resulting stable id. An unknown supplied input stops before the PATCH request and uses the resolver's diagnostic with the original input unchanged.

Create keeps `--agent` required. It always resolves before POST, including when the input already looks like an id, so create and edit share one resolver path.

### 3. Preserve all other routing fields

`name`, `match`, `responsePrompt`, `continue`, project query, position query, and output selection remain unchanged. The feature does not alter server-side validation or how the routing engine later uses the stored `agentId`.

## Verification

Future CLI tests MUST use a fake HTTP handler and assert:

1. create with Agent name lists/resolves the Agent and posts its stable id;
2. create with Agent id resolves the id and posts the same stable id;
3. edit with Agent name resolves and patches the stable id;
4. edit with Agent id patches the same stable id as the name form;
5. create and edit with an unknown input return non-zero, include the original input in the diagnostic, and make no mutation request; and
6. edit without `--agent` preserves existing omission behavior.

Tests must assert request order and paths, including the project-scoped Agent lookup before the routing mutation. Use no network, process, database, or wall-clock dependency. Run the focused CLI routing tests, CLI test/typecheck coverage, `npm run docs:check`, `npm run archtest`, `npm run test:fast`, and `npm run verify`.

## Risks and Trade-offs

- Resolving a name requires an additional project Agent lookup before mutation. This is required for stable identity and reuses the existing CLI contract.
- A concurrent rename or archive between lookup and mutation remains governed by the existing Server validation; this change does not add a new consistency protocol.
- Existing callers that passed a non-existent name will now fail before POST rather than letting the Server reject a raw `agentId`; the diagnostic is more direct and names the original input.

## Migration Plan

1. Call the existing resolver from routing-rule create/edit and pass `AgentRef.Id` to the unchanged mutation body.
2. Add focused CLI request-order and equivalence tests.
3. Run focused CLI tests, docs and architecture checks, fast tests, and the full `npm run verify` gate.
4. Confirm no Server, backend, routing DSL, or rule-reference files changed.

Rollback is a source revert of the CLI call-site and its tests. No data migration or API migration is required.

## Open Questions

None. The existing `AgentCommands.ResolveAgentAsync` is the approved resolver seam.
