## MODIFIED Requirements

### Requirement: Issue write endpoints accept case-insensitive priority values

`POST /api/issues` and `PATCH /api/issues/:number` SHALL accept priority values case-insensitively and normalize them to the stored lowercase priority contract.

#### Scenario: Create issue with uppercase priority
- **WHEN** a client sends `POST /api/issues` with `priority: "P2"`
- **THEN** the API accepts the request
- **AND** treats the priority the same as `"p2"`

#### Scenario: Update issue with uppercase priority
- **WHEN** a client sends `PATCH /api/issues/42` with `priority: "P0"`
- **THEN** the API accepts the request
- **AND** treats the priority the same as `"p0"`

#### Scenario: Reject invalid create priority
- **WHEN** a client sends `POST /api/issues` with `priority: "urgent"`
- **THEN** the API returns a 400-class validation error

#### Scenario: Reject invalid update priority
- **WHEN** a client sends `PATCH /api/issues/42` with `priority: "urgent"`
- **THEN** the API returns a 400-class validation error

### Requirement: Issue list endpoint accepts case-insensitive priority filters

`GET /api/issues` SHALL accept uppercase or lowercase priority filter values and apply the same normalized filter semantics for both.

#### Scenario: List issues with uppercase priority filter
- **WHEN** a client requests `GET /api/issues?priority=P1`
- **THEN** the API applies the same filter as `priority=p1`

#### Scenario: Reject invalid list priority filter
- **WHEN** a client requests `GET /api/issues?priority=urgent`
- **THEN** the API returns a 400-class validation error
