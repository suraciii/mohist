# Routing Agent Reference

Status: accepted

## Problem

Routing rules persist a stable `AgentId`, but the CLI is used with both human-readable Agent names and stable IDs. Sending raw CLI input as `agentId` makes the command shape depend on whether the caller knows storage identity. The edit path also loses the distinction between an omitted option and a JSON `null`, while the Server binder and store use different field-presence spellings. A name lookup alone would therefore not make edit reliable end to end.

## Decision

`mo routing rule create` and `mo routing rule edit --agent` accept one Agent reference: a project-scoped Agent name or stable ID. The CLI resolves that reference through the same Agent resolver used by other Agent commands after Project resolution and before mutation. The request always carries the resolved stable `agentId`. The Server does not add Agent-name lookup or a fallback resolver.

Routing-rule edit encodes presence explicitly. It sends only options supplied by the caller. An omitted `name`, `match`, `agentId`, `responsePrompt`, or `continue` property is absent and preserves the stored value. A direct API caller's JSON `null` remains present and follows the field's existing validation; `null` is not an omission marker.

The PATCH presence vocabulary is exactly `name`, `match`, `agentId`, `responsePrompt`, and `continue`. The request binder and store application use these JSON names. C# member names and alternate casing are not presence tokens, and the API accepts no compatibility aliases.

This decision does not change the routing expression language, rule identity, ordering, archive, move, dry-run, rendering, or launch semantics.

## Alternatives considered

### Require stable Agent IDs in routing commands

Rejected because the CLI already resolves Agent names for other Agent operations. Requiring IDs only for routing adds an unnecessary storage detail to the human command surface.

### Resolve Agent names on the Server

Rejected because the persisted and API contract remains a stable `AgentId`. A backend resolver would duplicate the existing project-scoped CLI boundary and expand routing semantics for every caller.

### Serialize all edit fields with nullable values

Rejected because it makes omission indistinguishable from an explicit value. PATCH must preserve fields that the caller did not address.

### Accept multiple field-name spellings

Rejected because aliases preserve the binder/store mismatch as compatibility surface. One canonical JSON vocabulary is smaller and fail-closed.

## Consequences

Create and edit perform an Agent read before mutation, including when the input is already an ID. Concurrent Agent rename or archive remains governed by the existing Server validation at mutation time.

Focused CLI tests must prove project resolution, Agent resolution, and mutation order with fake HTTP. Server tests must prove exact field presence and application. Compiled xUnit v3 apphost acceptance must reject zero discovery and any failed, skipped, or not-run test.
