## Design: Purpose And Permission Declaration

This slice adds two independent Agent definition fields:

- `purpose` is nullable free text. It is distinct from the existing
  `description`; old definitions remain without a purpose rather than being
  inferred or rewritten.
- `permissions` is a non-null list. Omission and an explicit empty list both
  project as an empty declaration.

`AgentPermissionVocabulary` is the single authority for the permitted terms:
`repo:read`, `repo:write`, `issue:read`, `issue:write`, `epic:read`,
`epic:write`, and `artifact:publish`. Agent create and patch validate the raw
request before invoking the Agent grain. An invalid term, an empty term, or a
non-array declaration returns `invalid_agent_permissions`; no partial update
can reach persistence.

The definition model, grain contracts, and `AgentInfo` projection carry both
fields. PATCH presence tracking preserves untouched fields: `purpose: null`
clears the purpose, while `permissions: []` clears the declaration.

The CLI adds purpose and permission options to create and edit, including
explicit clear options, and renders both in `agent view`. The Web client model,
profile editor, Agent list, and Agent detail consume the same API fields. The
Web editor sends a trimmed purpose and an empty permission list when clearing;
it does not duplicate the Server vocabulary, so validation feedback remains
authoritative.

The declaration is display and authoring state only. It is not passed as a
Runner capability, a launch override, or a Job snapshot. Existing launch and
runtime behavior therefore remain unchanged.
