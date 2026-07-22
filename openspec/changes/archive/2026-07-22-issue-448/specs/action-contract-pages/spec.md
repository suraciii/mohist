### Requirement: Every supported built-in Action has a product contract page

The product documentation SHALL publish one contract page for every supported built-in Action in the Runner manifest collection. Each contract page MUST mirror that Action's manifest and SHALL cover three contract facets: the complete input surface (each input's name, required status, accepted kinds, and declared default when one exists), the complete set of declared successful output fields, and the complete catalog of declared business error codes. A supported built-in Action MUST NOT lack a contract page, and a contract page MUST NOT omit an input, output, or business error code that its manifest declares.

#### Scenario: A supported Action is documented end-to-end
- **WHEN** a reader opens the contract page for any supported built-in Action
- **THEN** the page SHALL list every input declared by that Action's manifest with its name, required status, accepted kinds, and default
- **AND** the page SHALL list every output field declared by that Action's manifest
- **AND** the page SHALL list every business error code declared by that Action's manifest with its description

#### Scenario: An Action absent from docs is filled in
- **WHEN** the supported built-in Action set includes `mohist/github-pr-checks`, `core/process`, `core/script`, `core/artifact-exists`, `core/marker`, `mohist/openspec-tasks`, `mohist/openspec-artifacts`, or `mohist/archive-change`
- **THEN** each of those Actions SHALL have a contract page with its three contract facets
- **AND** the page SHALL be reachable from the contract overview page

#### Scenario: Existing Git and GitHub PR pages cover the full contract
- **WHEN** a reader opens the Git or GitHub PR group contract page
- **THEN** every Action on that page SHALL expose its declared outputs and business error codes in addition to its inputs
- **AND** no Action on those pages SHALL expose only its input list

#### Scenario: Removed Actions are not documented as supported
- **WHEN** a tombstoned Action such as `mohist/acp-agent` is considered for documentation
- **THEN** it MUST NOT receive a product contract page in the supported Action set
- **AND** the contract overview page MUST NOT link it as a usable Action

### Requirement: Contract pages mirror manifest declarations exactly

Each contract page's input, output, and error code content SHALL be consistent with the corresponding Action manifest in the Runner's built-in manifest collection. A documented input MUST NOT describe a name, required status, accepted kind, or default that the manifest does not declare, and a manifest-declared input MUST NOT be absent from the page. A documented output field or business error code MUST NOT appear unless the corresponding manifest declares it, and a manifest-declared output field or business error code MUST NOT be absent from the page.

#### Scenario: Documentation stays consistent with the manifest
- **WHEN** a built-in Action manifest declares an input, output, or business error code
- **THEN** the Action's contract page SHALL describe that declaration with the same name, kinds, required status, default, and description
- **AND** the page MUST NOT introduce an input, output, or error code that the manifest does not declare

#### Scenario: Platform-owned error codes are not documented as Action-owned
- **WHEN** a contract page describes an Action's business error codes
- **THEN** the page MUST NOT list the platform-reserved codes `invalid-input`, `unexpected-error`, or `timeout` as that Action's business errors
- **AND** the page SHALL attribute dispatch validation and timeout behavior to the platform where applicable rather than to the Action

### Requirement: Each contract page includes a directly usable example

Each contract page SHALL include at least one self-contained Workflow task or check snippet that uses the Action with manifest-valid inputs. A snippet MUST be copy-pasteable into a Workflow definition without edits that require reading implementation source, and every input referenced by a snippet SHALL be either a literal value or a `${{ }}` expression whose binding source is identified by the page. A snippet MUST NOT rely on an undeclared input, an implicit Variable fallback, or an Action that lacks a manifest.

#### Scenario: An example is copy-pasteable
- **WHEN** a reader copies a usage snippet from an Action's contract page into a Workflow definition
- **THEN** the snippet SHALL be a syntactically valid task or check using that Action
- **AND** every required input SHALL be bound to a literal or to an identified template expression
- **AND** the snippet MUST NOT depend on a Variable that the page does not identify

#### Scenario: An example uses only declared inputs
- **WHEN** a usage snippet binds a value to an input name
- **THEN** that input name SHALL be declared by the Action's manifest
- **AND** the snippet MUST NOT bind a name that the manifest rejects as unknown

### Requirement: The contract overview page enumerates every supported Action

The Action contract overview page SHALL enumerate every supported built-in Action and SHALL link each entry to its contract page. The overview page MUST NOT carry a gap footnote stating that remaining built-in Actions lack a contract page, and the link target for each enumerated Action SHALL resolve to a page that documents that Action.

#### Scenario: A reader can find every Action from the overview
- **WHEN** a reader opens the Action contract overview page
- **THEN** every supported built-in Action SHALL appear in the enumeration
- **AND** each enumerated entry SHALL link to that Action's contract page
- **AND** no broken or missing link SHALL remain for a supported Action

#### Scenario: The remaining-Actions gap footnote is removed
- **WHEN** the overview page describes the state of contract coverage
- **THEN** it MUST NOT state that `core/*` or OpenSpec Actions still lack independent contract pages
- **AND** it MUST NOT state that any remaining supported built-in Action lacks a contract page

### Requirement: Documentation change does not alter runtime behavior

This change SHALL modify only product documentation. The Runner manifest collection, Action execution functions, dispatch validation, catalog publication, profile behavior, and runtime gaps recorded by `mohist/pi` SHALL remain unchanged. Documenting an Action MUST NOT introduce a new input, output, error code, capability, runtime behavior, or migration step.

#### Scenario: Manifests remain the contract authority
- **WHEN** a contract page and a built-in Action manifest describe the same Action
- **THEN** the manifest SHALL remain the authoritative source
- **AND** the documentation change MUST NOT modify the manifest, its execution function, or its registry registration

#### Scenario: Pi runtime gaps remain documented as gaps
- **WHEN** a reader opens the `mohist/pi` contract page
- **THEN** that page SHALL retain its existing implementation-gap note describing Pi runtime capabilities that remain unimplemented
- **AND** the documentation change MUST NOT mark those gaps as delivered
