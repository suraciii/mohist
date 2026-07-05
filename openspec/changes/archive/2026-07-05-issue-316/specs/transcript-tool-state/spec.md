### Requirement: The transcript-tool-state public surface stays importable via the current paths

Every symbol exported today from `widgets/session-transcript/model/transcript-tool-state` SHALL remain importable — either from `transcript-tool-state.ts` directly or through a re-export — with identical signatures. The preserved surface comprises `updateToolInTurn`, `createToolPart`, `mapStatusToDisplay`, `isTerminalState`, `findToolByCorrelation`, `deriveToolTarget`, `buildLiveToolDetails`, `getDisplayFields`, `getNormalizedName`, and the `LiveToolCall` interface. The sole direct importer `transcript-state.ts` SHALL require zero import-specifier changes, and its downstream consumer `useSessionTranscript.ts` (which imports via `./transcript-state`) SHALL likewise require zero changes.

#### Scenario: The transcript-state importer keeps compiling unchanged

- **WHEN** `transcript-state.ts` is type-checked after the de-duplication and relocation
- **THEN** every currently-imported symbol (`buildLiveToolDetails`, `createToolPart`, `deriveToolTarget`, `findToolByCorrelation`, `getDisplayFields`, `getNormalizedName`, `isTerminalState`, `mapStatusToDisplay`, `updateToolInTurn`, `LiveToolCall`) SHALL resolve from `./transcript-tool-state`
- **AND** no import specifier in `transcript-state.ts` SHALL change

#### Scenario: The downstream hook keeps compiling unchanged

- **WHEN** `useSessionTranscript.ts` is type-checked after the change
- **THEN** its imports from `./transcript-state` SHALL continue to resolve `buildLiveToolDetails`, `updateToolInTurn`, `mapStatusToDisplay`, `isTerminalState`, `deriveToolTarget`, `getDisplayFields`, `getNormalizedName`, and `LiveToolCall`
- **AND** no import specifier in `useSessionTranscript.ts` SHALL change

#### Scenario: The existing transcript-state test suite passes unchanged

- **WHEN** `widgets/session-transcript/model/transcript-state.test.ts` is executed against the refactored modules
- **THEN** it SHALL compile without import edits
- **AND** every assertion SHALL pass

### Requirement: The tool-call merge precedence and output shape are unchanged

`updateToolInTurn` SHALL resolve a tool update through the same three-step precedence as before — first a match by `toolCallId`, then a match by `correlationKey` via `findToolByCorrelation`, then an append of a new part via `createToolPart` — and SHALL produce a `SessionTurn` structurally and field-for-field identical to the pre-change implementation for every input. The field-resolution order (`rawInput`/`rawOutput` fallback chain, `metadata`/`details` selection, `startedAt` selection, `normalizedName`/`displayTitle`/`displaySubtitle` resolution via `getDisplayFields`), the `stringifyPayload` application, the `parseEditInput`/`parsePatchOperations`-derived `changedFiles`, the `mapStatusToDisplay` status mapping, and the `completedAt` set to `now` iff the resulting status is terminal SHALL all be preserved.

#### Scenario: The toolCallId-match path is invariant

- **WHEN** `updateToolInTurn` is called with a `toolCallId` that matches an existing tool part
- **THEN** the merged tool part SHALL equal the pre-change output field by field
- **AND** the matched part's `toolCallId` SHALL be left unchanged
- **AND** `completedAt` SHALL be set to `now` iff the resulting status is terminal

#### Scenario: The correlationKey-match path is invariant

- **WHEN** no `toolCallId` matches but a `correlationKey` resolves to an existing non-terminal part via `findToolByCorrelation`
- **THEN** the merged tool part SHALL equal the pre-change output field by field
- **AND** the incoming `toolCallId` SHALL be written onto the matched part
- **AND** `completedAt` SHALL be set to `now` iff the resulting status is terminal

#### Scenario: The append-new path is invariant

- **WHEN** neither `toolCallId` nor `correlationKey` matches an existing part
- **THEN** a new `ToolPart` SHALL be appended via `createToolPart`
- **AND** the status SHALL default to `'started'` when `updates.status` is absent
- **AND** `completedAt` SHALL be set to `now` iff the defaulted status maps to a terminal display status

#### Scenario: The shared helpers are invariant

- **WHEN** `createToolPart`, `mapStatusToDisplay`, `isTerminalState`, `findToolByCorrelation`, `deriveToolTarget`, `getDisplayFields`, or `getNormalizedName` is invoked with the same arguments as before the change
- **THEN** each SHALL return a value equal to the pre-change output, including `createToolPart`'s `changedFiles` derivation and `mapStatusToDisplay`'s `timeout`→`failed` mapping

### Requirement: updateToolInTurn's duplicate merge branches collapse into a single merge helper

The two near-duplicate merge branches in `updateToolInTurn` (the by-`toolCallId` arm and the by-`correlationKey` arm) SHALL be collapsed into one shared merge helper that performs the tool-part field resolution. The precedence (try `toolCallId`, then `correlationKey`, then append) SHALL remain, and the single behavioral difference between the two arms — the correlation arm overwrites the matched part's `toolCallId` with the incoming one — SHALL be preserved by parameterizing the shared helper rather than by duplicating its body. The merge body SHALL NOT appear twice.

#### Scenario: A single merge helper performs field resolution

- **WHEN** `updateToolInTurn` is implemented
- **THEN** one shared merge helper SHALL perform the `rawInput`/`rawOutput`/`metadata`/`details`/display-field/status/`completedAt` resolution
- **AND** both the `toolCallId`-match and `correlationKey`-match arms SHALL route the matched part through it

#### Scenario: The toolCallId-overwrite difference between arms is preserved

- **WHEN** the `correlationKey` arm routes through the shared helper
- **THEN** the matched part's `toolCallId` SHALL be overwritten with the incoming `toolCallId`
- **AND** when the `toolCallId` arm routes through the shared helper, the `toolCallId` SHALL remain the matched part's existing value

### Requirement: buildLiveToolDetails relocates out of the state-transition module to the tool-views area

The `buildLiveToolDetails` tool-family dispatcher (the bash/shell/exec/command, task, skill, question/webfetch/websearch, and todowrite/todo branches) SHALL be moved out of `transcript-tool-state.ts` to co-locate with the tool views (under `widgets/session-transcript/ui/tool-views/` or its model). After the move, `transcript-tool-state.ts` SHALL be purely about turn/part state transitions and SHALL NOT contain the dispatcher body. The relocated module (or `transcript-tool-state.ts` via re-export) SHALL keep `buildLiveToolDetails` importable from `./transcript-tool-state` so `transcript-state.ts` is unchanged.

#### Scenario: transcript-tool-state becomes purely state-transition

- **WHEN** `transcript-tool-state.ts` is inspected
- **THEN** it SHALL NOT contain the `buildLiveToolDetails` dispatcher body
- **AND** it SHALL contain only turn/part state-transition logic (`updateToolInTurn`, `createToolPart`, `mapStatusToDisplay`, `isTerminalState`, `findToolByCorrelation`, `deriveToolTarget`, `getDisplayFields`, `getNormalizedName`)

#### Scenario: buildLiveToolDetails lives with the tool views and stays importable

- **WHEN** the tool-views area (`widgets/session-transcript/ui/tool-views/` or its model) is inspected
- **THEN** `buildLiveToolDetails` SHALL reside there
- **AND** it SHALL remain importable from `widgets/session-transcript/model/transcript-tool-state` via re-export so that `transcript-state.ts` requires no change

#### Scenario: The details records per tool family are unchanged

- **WHEN** `buildLiveToolDetails` is invoked for any tool family after relocation — execution (bash/shell/exec/command → `family: 'execution'` with `command`/`cwd`/`timeout`/`exitCode`/`outputPreview`/`completionStatus`), delegation (task → `family: 'delegation'` with `description`/`subagentType`/`subagentName`/`taskId`/`childSessionId`), skill (`family: 'skill'` with `skillName`), interaction (question/webfetch/websearch → `family: 'interaction'` with `url`/`query`/`answerCount`/`resultPreview`), or planning (todowrite/todo → `family: 'planning'` with `totalCount`/`statusCounts`)
- **THEN** the returned `details` record SHALL equal the pre-change output field by field, including `truncatePreview` truncation for execution previews, the 300-character cap on interaction `resultPreview`, and `undefined` returned for unknown families

### Requirement: transcript-tool-state leaves the complexity hotspot band

After the de-duplication of `updateToolInTurn` and the relocation of `buildLiveToolDetails`, `widgets/session-transcript/model/transcript-tool-state.ts` SHALL drop out of the `C=180` hotspot band it occupied before the change.

#### Scenario: Complexity leaves the hotspot band

- **WHEN** SCC complexity is measured on `widgets/session-transcript/model/transcript-tool-state.ts`
- **THEN** its complexity SHALL fall below the `C=180` hotspot threshold
