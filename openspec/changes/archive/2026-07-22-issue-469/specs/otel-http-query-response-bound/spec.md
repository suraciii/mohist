### Requirement: HTTP query responses are bounded to at most 1000 rows

`POST /otel/api/query` SHALL return at most 1000 rows per response. When the underlying result set contains more than 1000 rows, the response SHALL contain exactly the first 1000 rows and SHALL signal truncation; it SHALL NOT silently drop the excess or return an arbitrary unbounded array.

#### Scenario: A result exceeding the row limit is truncated to 1000 rows

- **WHEN** a SELECT would return more than 1000 rows
- **THEN** the response data array SHALL contain exactly 1000 rows
- **AND** the response SHALL carry a truncation indicator with a stable reason identifying the row limit as the cause

#### Scenario: A result within the row limit is returned complete

- **WHEN** a SELECT returns 1000 or fewer rows and the serialized size is within the byte budget
- **THEN** the response SHALL contain every returned row
- **AND** SHALL NOT carry a truncation indicator

### Requirement: HTTP query responses are bounded to at most 4 MiB serialized JSON

The serialized JSON response SHALL NOT exceed 4 MiB. The byte budget SHALL be evaluated against the serialized representation as rows are produced; when emitting the next row would push the response past 4 MiB, the endpoint SHALL stop adding rows and SHALL signal truncation. The endpoint SHALL NOT materialize and serialize the entire result set before applying the byte budget.

#### Scenario: A single very large cell value triggers byte-bound truncation

- **WHEN** a SELECT returns a row whose serialized representation would push the response past 4 MiB
- **THEN** the endpoint SHALL stop before exceeding 4 MiB
- **AND** SHALL carry a truncation indicator with a stable reason identifying the byte limit as the cause

#### Scenario: Many moderately-sized rows trigger byte-bound truncation before the row limit

- **WHEN** a SELECT returns fewer than 1000 rows but their cumulative serialized size exceeds 4 MiB
- **THEN** the endpoint SHALL stop before exceeding 4 MiB
- **AND** SHALL carry a truncation indicator with a stable reason identifying the byte limit rather than the row limit

#### Scenario: A recursive CTE that amplifies row production is bounded by whichever limit is hit first

- **WHEN** a recursive CTE produces a very large number of rows or very large cumulative bytes
- **THEN** the response SHALL be bounded by whichever of the row limit or byte limit is hit first
- **AND** SHALL carry a truncation indicator naming the bound that was hit

### Requirement: Truncation is signalled through a structured indicator with a stable reason

When the row limit or byte limit is reached, the response SHALL carry a structured truncation indicator that the caller can read programmatically. The indicator SHALL include a stable reason field that distinguishes the row limit from the byte limit, so a caller can tell why the result was truncated without inspecting row counts or response size.

#### Scenario: The truncation indicator is present and identifies the cause

- **WHEN** the row limit or byte limit is reached during a response
- **THEN** the response SHALL include a truncation indicator with a field marking the result as truncated
- **AND** the indicator SHALL include a stable reason field (for example `row_limit` or `byte_limit`) identifying which bound was hit

#### Scenario: A non-truncated response carries no truncation indicator

- **WHEN** neither the row limit nor the byte limit is reached
- **THEN** the response SHALL NOT carry a truncation indicator
- **AND** SHALL NOT present the result as truncated

### Requirement: The response is always well-formed and never silently empty

The endpoint SHALL return a complete, well-formed JSON response in every case. It SHALL NEVER return a JSON body cut mid-serialization, and SHALL NEVER represent a truncated result as an empty array. A caller SHALL always be able to distinguish a genuinely empty result (zero rows) from a truncated one.

#### Scenario: A truncated response is well-formed JSON

- **WHEN** the endpoint stops emitting rows because a bound was reached
- **THEN** the response SHALL be a complete, parseable JSON document
- **AND** SHALL NOT be a partial JSON fragment cut mid-array

#### Scenario: A truncated result is not presented as empty

- **WHEN** the underlying result had rows but was truncated
- **THEN** the response SHALL NOT present the data as an empty array
- **AND** SHALL distinguish truncation from a genuine zero-row result via the truncation indicator

### Requirement: The HTTP response bounds do not apply to the local CLI query path

`mo otel query` reads `otel.db` directly through its own read-only connection and is not an HTTP consumer. The 1000-row, 4 MiB, and truncation-indicator requirements SHALL apply only to `POST /otel/api/query`. The CLI's local read-only query path SHALL continue to return complete results subject only to its existing read-only connection and command timeout.

#### Scenario: The CLI local query path is not subject to HTTP response bounds

- **WHEN** `mo otel query` executes a SELECT that returns more than 1000 rows or more than 4 MiB
- **THEN** the CLI SHALL NOT apply the HTTP row or byte bounds
- **AND** SHALL NOT emit the HTTP truncation indicator
- **AND** SHALL continue to read `otel.db` directly through its own read-only connection
