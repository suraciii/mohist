### Requirement: Root help is a complete capability index
`mo --help` SHALL list every visible command registered directly under the root exactly once under Work, Automation, Operations, or Tools, with a one-sentence presentation of that command. The index SHALL classify `workspace` under Work and SHALL classify `audit`, `github`, and `slack` under Operations. Root help MUST remain an area index and MUST NOT expand descendants, arguments, or leaf options.

#### Scenario: Discovering all registered root areas
- **WHEN** a user runs `mo --help`
- **THEN** every visible command registered directly under `mo` SHALL appear exactly once in the capability index
- **AND** `workspace` SHALL appear under Work
- **AND** `audit`, `github`, and `slack` SHALL appear under Operations

#### Scenario: Root help remains progressively scoped
- **WHEN** a user runs `mo --help`
- **THEN** the output SHALL provide a one-sentence presentation for each visible root command
- **AND** the output MUST NOT list descendant invocations, command arguments, or leaf options

### Requirement: Group help covers every direct child
Help for every visible command group or subarea SHALL identify that group's purpose and SHALL list every visible direct child with a one-sentence presentation of the child's behavior. Group help MUST list only direct children; deeper descendants SHALL remain discoverable through help for the listed child group.

#### Scenario: Discovering a newly covered area
- **WHEN** a user runs `mo workspace --help`
- **THEN** the output SHALL identify the Workspace command area
- **AND** the output SHALL list `list`, `view`, `create`, `close`, and `repo` with one-sentence presentations
- **AND** actions below `workspace repo` MUST NOT be expanded in the Workspace action list

#### Scenario: Discovering actions in a nested subarea
- **WHEN** a user runs `mo session schedule --help`
- **THEN** the output SHALL identify the scheduled-input subarea
- **AND** the output SHALL list its visible direct actions `create`, `list`, and `cancel` with one-sentence presentations
- **AND** the usage and further-help guidance SHALL contain the complete `mo session schedule` invocation path

#### Scenario: Discovering recently registered direct children
- **WHEN** a user runs help for a group containing a visible registered child such as `agent spawn`, `agent subscription`, `session tree`, `session detach`, or `otel traces`
- **THEN** the group help SHALL list that child with a one-sentence presentation of its current product behavior

### Requirement: Leaf help provides the exact scoped invocation
Help for every visible leaf command SHALL provide a one-sentence presentation of its behavior and SHALL show the complete invocation path, arguments, and visible options derived from that command. Where a leaf supports JSON field selection, its help SHALL list the fields accepted by that leaf. Leaf help MUST NOT expand unrelated commands or the complete command tree.

#### Scenario: Reading help for a deeply nested leaf
- **WHEN** a user runs `mo workspace repo add --help`
- **THEN** the output SHALL describe adding a Repository to a Workspace
- **AND** the usage SHALL start with the complete invocation `mo workspace repo add`
- **AND** the output SHALL show that leaf's arguments and visible options without listing sibling or root command trees

#### Scenario: Reading help for a recent resource-result leaf
- **WHEN** a user runs `mo otel traces --help`
- **THEN** the output SHALL describe listing recent traces through the Server
- **AND** the usage SHALL start with `mo otel traces`
- **AND** the output SHALL list the command's visible inputs and supported JSON fields

### Requirement: Help presentations cover the visible registered tree
Every visible command node in the registered command tree SHALL have explicit help presentation coverage appropriate to its level before the CLI is accepted by automated verification. Verification MUST traverse the registered tree, MUST reject a missing root classification or missing command presentation, and MUST identify each uncovered command by its complete invocation path. A command description used to register syntax MUST NOT allow missing presentation coverage to pass verification silently.

#### Scenario: A visible root command lacks classification
- **WHEN** a visible command is registered directly under `mo` without a capability classification
- **THEN** automated verification SHALL fail
- **AND** the failure SHALL identify the command's complete invocation path

#### Scenario: A visible descendant lacks presentation coverage
- **WHEN** a visible group, subarea, or leaf is registered without its required one-sentence presentation
- **THEN** automated verification SHALL fail
- **AND** the failure SHALL identify the uncovered command's complete invocation path

#### Scenario: All visible commands are covered
- **WHEN** automated verification traverses the current registered command tree
- **THEN** every visible node SHALL have the classification or presentation required for its level
- **AND** no visible command SHALL depend on fallback registration text to satisfy coverage

### Requirement: Help and usage discovery are local and side-effect free
Every `--help` invocation SHALL exit successfully using only the local command model. Help MUST NOT resolve a Project, contact the Server, prompt for input, invoke an external process, or perform a command's operational side effects. A local usage error SHALL exit with code 2 and SHALL render usage for the nearest recognized command using its complete invocation path, without executing that command.

#### Scenario: Requesting help for any covered command without dependencies
- **WHEN** a user requests `--help` for a visible root command, nested group, or leaf while no Project or Server is available
- **THEN** help SHALL be written successfully from the local command model
- **AND** no Project resolution, Server request, prompt, external process, or command action SHALL occur

#### Scenario: Entering an unknown nested action
- **WHEN** a user invokes an unknown action below a recognized nested group
- **THEN** the CLI SHALL exit with code 2
- **AND** stderr SHALL show usage for that nearest recognized group with its complete invocation path
- **AND** no Project resolution, Server request, prompt, external process, or command action SHALL occur

### Requirement: Help coverage does not change command behavior
This change SHALL NOT add, remove, or rename registered commands, arguments, or options, and SHALL NOT alter command execution, request, output, or exit semantics outside help and local usage presentation.

#### Scenario: Executing an existing command after help coverage is completed
- **WHEN** a caller invokes an existing command without a help option
- **THEN** the command SHALL parse and execute with the same registered path, inputs, operational behavior, and result semantics as before this change

### Requirement: CLI reference matches the executable help surface
The CLI reference command map SHALL list the complete visible registered command surface and the same root capability classifications exposed by `mo --help`. Its implementation-gap status MUST NOT claim that a command area remains absent from root help after that area is covered.

#### Scenario: Comparing the reference with root help
- **WHEN** the delivered `mo --help` capability index is compared with the CLI reference command map
- **THEN** both SHALL include the same visible root commands under the same capability classifications
- **AND** the reference SHALL include `workspace` under Work and `audit`, `github`, and `slack` under Operations

#### Scenario: Reviewing implementation gaps after delivery
- **WHEN** the CLI reference's implementation-gap section is read after this change
- **THEN** it MUST NOT report `workspace`, `audit`, `github`, `slack`, or any other covered visible area as missing from executable root help
