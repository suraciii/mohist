## ADDED Requirements

### Requirement: Tool call display title derived from rawInput
When rendering a `ToolCallEntry`, the frontend SHALL derive a meaningful display title from `rawInput` when `title` is absent or equals only the tool kind name (e.g., "read", "bash", "glob"). The derivation rules SHALL be:

- `read` / `read_file`: Extract file path from rawInput (first argument or `file_path` field)
- `bash`: Extract command string from rawInput (`command` field or first argument)
- `glob` / `search_files`: Extract pattern from rawInput (`pattern` field)
- `write` / `write_file`: Extract file path from rawInput (`file_path` field or first argument)
- `edit`: Extract file path from rawInput (`file_path` field or first argument)
- `grep` / `search`: Extract pattern from rawInput (`pattern` field)
- Other tools: Use `title` if available, otherwise `toolName`

#### Scenario: Read tool call with file path in rawInput
- **WHEN** a tool call has `toolName: 'read'` and `rawInput: '{"file_path": "packages/cli/src/openspec/ralph-executor.ts"}'`
- **THEN** the display title shows `ralph-executor.ts` (basename of the file path)

#### Scenario: Bash tool call with command in rawInput
- **WHEN** a tool call has `toolName: 'bash'` and `rawInput: '{"command": "npm run build"}'`
- **THEN** the display title shows `npm run build`

#### Scenario: Glob tool call with pattern in rawInput
- **WHEN** a tool call has `toolName: 'glob'` and `rawInput: '{"pattern": "src/**/*.ts"}'`
- **THEN** the display title shows `src/**/*.ts`

#### Scenario: Tool call with meaningful title already set
- **WHEN** a tool call has `title: 'packages/cli/src/openspec/ralph-executor.ts'` and `toolName: 'read'`
- **THEN** the display title uses the existing `title` without re-derivation

#### Scenario: Tool call with rawInput as non-JSON string
- **WHEN** a tool call has `toolName: 'bash'` and `rawInput: 'npm test'` (plain string, not JSON)
- **THEN** the display title shows `npm test` (use the string directly)

### Requirement: Completed tool_call_update propagates title and rawInput
When `useSessionTimeline` receives a `coder_tool_call` event or processes a `tool_call_update` log entry with `status: 'completed'` or `status: 'failed'`, it SHALL update the existing `ToolCallEntry`'s `title` and `rawInput` fields from the event payload, in addition to `state` and `rawOutput`.

#### Scenario: Coder tool call completed with updated title
- **WHEN** a `coder_tool_call` SSE event arrives with `state: 'completed'`, `title: 'packages/cli/src/main.ts'`, `rawInput: '{"file_path": "packages/cli/src/main.ts"}'`, and `rawOutput: '...'`
- **THEN** the existing ToolCallEntry is updated with `state: 'completed'`, `title: 'packages/cli/src/main.ts'`, `rawInput: '{"file_path": "packages/cli/src/main.ts"}'`, and `rawOutput`

#### Scenario: Historical tool_call_update with updated title
- **WHEN** `reconstructRoundsFromLogs` processes a `tool_call_update` log entry with `status: 'completed'`, `title: 'packages/cli/src/main.ts'`, and `rawInput: '...'`
- **THEN** the existing ToolCallEntry in the round is updated with the new `title` and `rawInput`, not just `state` and `rawOutput`

### Requirement: Historical reconstruction derives titles from rawInput
When `reconstructRoundsFromLogs` processes `tool_call` events where `title` is absent or equals the tool `kind`, the function SHALL derive a meaningful title from `rawInput` using the same derivation rules as live rendering.

#### Scenario: Historical tool_call with kind-only title
- **WHEN** a `tool_call` log entry has `kind: 'read'`, `title: 'read'`, and `rawInput: '{"file_path": "packages/cli/src/server.ts"}'`
- **THEN** the reconstructed ToolCallEntry has `title` set to `server.ts` (derived from rawInput)

#### Scenario: Historical tool_call_update overrides derived title
- **WHEN** a `tool_call` log entry derives title "server.ts" from rawInput, then a subsequent `tool_call_update` log entry for the same toolCallId has `title: 'packages/cli/src/server.ts'`
- **THEN** the final ToolCallEntry uses the `tool_call_update` title `packages/cli/src/server.ts`
