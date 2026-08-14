### Requirement: Credential-owned direct API Project grants

A PAT MAY persist one valid direct API Project grant. An explicit grant SHALL
contain a non-empty, duplicate-free set of canonical Project IDs. An
`operator_all` grant SHALL have an empty Project-ID set and SHALL be stored as
an explicit grant kind, never inferred from the PAT scope. A PAT without a
grant remains valid for existing control-plane routes.

#### Scenario: Explicit grant survives credential lookup

- **WHEN** a PAT is created with Projects `proj_a` and `proj_b`
- **THEN** its persisted grant kind is `explicit`
- **AND** a later credential lookup returns exactly `proj_a` and `proj_b`

#### Scenario: Operator-wide grant is explicit

- **WHEN** an operator-scope PAT is created with `operator_all`
- **THEN** the persisted grant kind is `operator_all`
- **AND** no explicit Project child row is stored

### Requirement: Grant-aware PAT issuance is atomic

`POST /api/auth/tokens` SHALL accept optional explicit Project references or
an operator-wide grant. Explicit references and `operator_all` are mutually
exclusive; `operator_all` requires operator scope. Every explicit reference
MUST resolve before the Credential is written. A rejected grant request SHALL
write neither a Credential nor a Project grant.

#### Scenario: Unknown Project leaves no partial credential

- **WHEN** a token request names an unknown Project
- **THEN** the response is forbidden
- **AND** no Credential or Project-grant row is persisted

### Requirement: CLI forwards only valid grant intent

`mo auth token create` SHALL offer repeatable `--project` and `--all-projects`.
It SHALL reject their combination and `--all-projects` under readonly scope
before issuing an HTTP request. A valid request SHALL forward the chosen grant
intent to the token endpoint.

#### Scenario: Invalid option combination makes no request

- **WHEN** a caller passes both `--project proj_a` and `--all-projects`
- **THEN** the CLI exits nonzero
- **AND** it makes no token-create request
