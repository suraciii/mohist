### Requirement: The endpoint admits only single SELECT or WITH statements on a read-only connection

`POST /otel/api/query` SHALL admit only SQL whose every top-level statement begins with `SELECT` or `WITH`, and SHALL reject any top-level statement that begins with another keyword. Multi-statement input where any statement is not `SELECT`/`WITH` SHALL be rejected at the keyword layer before execution. Write attempts that survive the keyword layer SHALL still be rejected by the SQLite engine because the connection is physically read-only. This is the existing three-layer safety net (keyword allow-list, read-only connection, command timeout) and SHALL continue to hold after this change.

#### Scenario: Non-SELECT top-level statements are rejected with the query_not_select code

- **WHEN** a request body contains a SQL statement whose first keyword is not `SELECT` or `WITH` (for example `DELETE FROM traces`, `INSERT INTO traces ...`, `DROP TABLE traces`, `ALTER TABLE traces ...`, or `PRAGMA writable_schema = 1`)
- **THEN** the endpoint SHALL return HTTP 400
- **AND** the response body SHALL carry the stable error code `query_not_select`

#### Scenario: Multi-statement input with a non-SELECT tail is rejected

- **WHEN** a request body contains more than one top-level statement separated by `;` and any statement does not begin with `SELECT` or `WITH` (for example `SELECT 1; DROP TABLE traces`)
- **THEN** the endpoint SHALL return HTTP 400 with code `query_not_select`
- **AND** SHALL NOT execute any of the statements

#### Scenario: Write attempts that bypass the keyword layer are rejected by the read-only engine

- **WHEN** a request body contains a statement that the keyword layer does not reject on its head but that the SQLite engine treats as a write or schema change (for example `ATTACH DATABASE ':memory:' AS attached`)
- **THEN** the endpoint SHALL return HTTP 400 with code `query_sqlite_error`
- **AND** the physically read-only connection SHALL refuse the operation at the SQLite engine level

#### Scenario: Single SELECT and WITH statements are admitted

- **WHEN** a request body contains a single statement beginning with `SELECT` or `WITH` (including compound `SELECT ... UNION SELECT ...` and CTE forms)
- **THEN** the endpoint SHALL accept the statement for execution on the read-only connection

### Requirement: Oversized request bodies are rejected before full buffering

The endpoint SHALL enforce a single explicit maximum request body size. The limit SHALL be small (on the order of tens of kilobytes) and SHALL be applied to the raw request body before the body is fully buffered into memory or parsed as JSON. A request whose body exceeds the limit SHALL be rejected with HTTP 413 and a stable error code; the endpoint SHALL NOT allocate a string holding the entire oversized body.

#### Scenario: An oversized body is rejected with 413 before full buffering

- **WHEN** a client posts a request body larger than the configured maximum request body size
- **THEN** the endpoint SHALL return HTTP 413
- **AND** SHALL apply the limit before the entire body has been read into memory
- **AND** the response SHALL carry the stable error code `query_request_too_large`

#### Scenario: The body-size limit is enforced before JSON parsing and keyword validation

- **WHEN** a request body exceeds the maximum size, regardless of whether its contents are valid JSON
- **THEN** the endpoint SHALL apply the body-size limit first
- **AND** SHALL NOT attempt to parse the body as JSON or invoke `ValidateSelectOnly`

#### Scenario: Bodies within the limit proceed to admission

- **WHEN** a request body is at or below the maximum size and is otherwise well-formed
- **THEN** the endpoint SHALL proceed to parse and validate the SQL
- **AND** SHALL NOT return a 413 response

### Requirement: Admission errors use the standard API envelope with stable error codes

Every admission rejection (non-SELECT, oversized body, missing SQL, malformed JSON) SHALL be returned in the standard `ApiResponse` envelope used by the rest of `/api/*`, with a stable `code` field so callers can distinguish rejection reasons programmatically without parsing human-readable text.

#### Scenario: Each admission failure carries a distinct stable code

- **WHEN** the endpoint rejects a request for any admission reason
- **THEN** the response body SHALL be an `ApiResponse` envelope with `success = false`
- **AND** the `code` field SHALL identify the specific reason (`query_request_too_large` for body-size, `query_not_select` for non-SELECT, `query_malformed` for invalid JSON, `query_missing_sql` for missing SQL)
