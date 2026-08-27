# CLI Agent Reference Resolution for Routing Rules

## Why

The `mo routing rule create` and `mo routing rule edit` commands accept `--agent` as an Agent name or id, but currently place the raw input into `agentId`. The command therefore behaves differently from other CLI commands that use the existing project-scoped Agent resolver: an Agent name is not converted to its stable id before the routing-rule request is sent.

Routing rules store a stable Agent reference. Name and id are two input forms for that same reference and must not create different routing behavior.

## What Changes

- Resolve `--agent` for routing-rule create and edit through the existing CLI Agent resolver.
- Send the resolved stable `agentId` for both a supplied Agent name and a supplied Agent id.
- Preserve the original user input in an unknown-Agent diagnostic.
- Keep all existing routing rule, backend, routing DSL, rule-reference, project-resolution, and output behavior unchanged.
- Add deterministic CLI coverage for create/edit by name and id, identical resulting request Agent IDs, and unknown-name diagnostics.

## Capability

- `routing-rule-agent-reference`: consistent project-scoped Agent name/id resolution at the CLI routing-rule create/edit boundary.

## Impact

- **CLI:** routing-rule create/edit resolve the option before issuing the mutation.
- **Server:** receives the same stable `agentId` contract as other CLI Agent consumers; no endpoint or schema change.
- **Routing:** rule matching and rule references are unchanged.
- **Errors:** an unknown reference identifies the exact original `--agent` input and prevents the mutation request.
