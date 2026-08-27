# CLI Agent Reference Resolution for Routing Rules

## Why

The `mo routing rule create` and `mo routing rule edit` commands accept `--agent` as an Agent name or id, but currently place the raw input into `agentId`. The command therefore behaves differently from other CLI commands that use the existing project-scoped Agent resolver: an Agent name is not converted to its stable id before the routing-rule request is sent.

Current master also exposes two independent PATCH-boundary seams. The CLI edit builds a nullable anonymous object, so options that were not provided can be serialized as JSON `null` instead of being absent. The Server PATCH binder records JSON presence with C# property vocabulary (`Name`, `Match`, `AgentId`, `ResponsePrompt`) while the store checks lower-case parameter vocabulary (`name`, `match`, `agentId`, `responsePrompt`); consequently supplied name, match, AgentId, or prompt updates can be ignored or treated inconsistently. The approved end-to-end requirement is that create and edit both work for Agent names and ids, so the smallest Server PATCH contract correction is in scope where this evidence requires it.

## What Changes

- Resolve `--agent` for routing-rule create and edit through the existing CLI Agent resolver.
- Send the resolved stable `agentId` for both a supplied Agent name and a supplied Agent id.
- Build edit PATCH JSON from options that were actually provided; fields not provided by the CLI MUST be absent from JSON rather than serialized as `null`.
- Resolve an explicit Agent value before PATCH and use that resolved value in the body.
- Correct the minimal Server PATCH presence contract so `Raw` and `Fields` use one canonical JSON vocabulary and the store applies every present field.
- Add focused CLI and Server contract tests for name/id equivalence, omission, field application, and request ordering.
- Preserve existing routing rule, backend validation, routing DSL, rule-reference, project-resolution, and output behavior. Do not add broader backend Agent lookup or routing DSL changes.

## Capability

- `routing-rule-agent-reference`: consistent project-scoped Agent name/id resolution at the CLI routing-rule create/edit boundary, with the minimum PATCH presence correction required for that boundary to work end to end.

This remains one standalone value issue: the Server seam is an enabling correction for the same CLI Agent-reference contract, not a new backend feature.

## Impact

- **CLI:** routing-rule create/edit resolve the option before issuing the mutation; edit omits fields that were not supplied.
- **Server:** the existing PATCH endpoint receives the same stable `agentId` contract and applies present JSON fields using canonical names. Existing stable-id validation remains authoritative; no backend name lookup is added.
- **Routing:** rule matching, rule ordering, and rule references are unchanged.
- **Errors:** an unknown reference identifies the exact original `--agent` input and prevents the mutation request.
