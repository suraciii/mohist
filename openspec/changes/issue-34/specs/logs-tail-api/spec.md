### Requirement: Single typed `/api/logs/tail` response shape

`GET /api/logs/tail` SHALL return one agreed typed contract that the server and the Web client both honor. The response SHALL always carry: the list of per-line elements, an incremental cursor value, the source identity (which file/source the lines came from), and truncation/reset metadata. The current ad-hoc server shape `{ lines: object[], nextCursor }` and the current client shape `{ file, cursor, lines: string[], truncated, reset }` SHALL both be replaced by this single shape so that no field the page depends on is `undefined` at runtime.

#### Scenario: Response always carries the agreed fields

- **WHEN** a client calls `GET /api/logs/tail` with or without a cursor
- **THEN** the response SHALL include the per-line element list, an incremental cursor, source identity, and truncation/reset metadata
- **AND** SHALL NOT omit any of these fields

### Requirement: Consistent per-line element type with no double-parsing

Every entry in the response `lines` list SHALL be a single agreed structured element type carrying `level`, `time`, `service`, `message`, and `raw` fields (`raw` holds the faithful original serialized line so search/export operate on it even when structured fields are absent). The server SHALL emit each element already in that structured type and SHALL NOT emit raw JSON strings for the client to parse. The Web client SHALL render elements directly without running `JSON.parse` on them, so `level`/`time`/`service`/`message` extraction no longer silently fails.

#### Scenario: Server emits a structured element for a valid JSON line

- **WHEN** the server reads a valid JSON log record from the log file
- **THEN** it SHALL emit that record as the agreed structured element type with `level`/`time`/`service`/`message` populated from the record
- **AND** SHALL NOT emit the record as a JSON string requiring client-side parsing

#### Scenario: Non-JSON line degrades to the same element type

- **WHEN** the server reads a line that is not valid JSON
- **THEN** it SHALL emit an element of the same agreed type whose `message` is the raw line and whose `level`/`time`/`service` are empty/null
- **AND** SHALL NOT mix a raw string into the element list

### Requirement: Incremental cursor for tailing

The response SHALL carry an incremental cursor such that passing it back to `/api/logs/tail` yields only lines after the previous read. A first request with no cursor SHALL return the recent tail.

#### Scenario: Cursor advances across successive reads

- **WHEN** a client calls `/api/logs/tail` and then calls again with the cursor from the first response
- **THEN** the second response SHALL contain only lines after the first read's position
- **AND** the second response SHALL return an updated cursor the client can pass back again

### Requirement: Source identity in the response

The response SHALL carry source identity identifying which file/source the returned lines came from, so the Logs page can render a real `File:` line against an agreed source instead of an `undefined` value.

#### Scenario: Source identity reflects the active log file

- **WHEN** the server reads lines from a populated log file
- **THEN** the response SHALL include the source identity (e.g. the file path or name)
- **AND** the Web page SHALL be able to display it as the `File:` source line

### Requirement: Truncation and reset metadata

The response SHALL carry truncation metadata indicating whether the returned chunk was bounded by the line-count or byte cap before reaching EOF, and reset metadata indicating whether the client MUST replace (not append) its current view — e.g. on a first read or when the source has rotated/truncated.

#### Scenario: Truncation reported when a cap is reached before EOF

- **WHEN** the server stops reading because the line-count or byte cap was reached before EOF
- **THEN** the response SHALL indicate truncation occurred

#### Scenario: Reset reported on first read or source rotation

- **WHEN** the client performs a first read, or the source has rotated/truncated since the previous read
- **THEN** the response SHALL indicate reset so the client replaces its entries rather than appending

### Requirement: Explicit source-unavailable state distinct from an empty tail

When the log directory or log file is absent, `/api/logs/tail` SHALL return an explicit source-unavailable state that includes the expected log location. This state MUST be distinct from a successful tail of an available source that simply returned zero new lines, so the page can distinguish "logs are not being captured" from "nothing new to show".

#### Scenario: Unavailable state when the log directory is missing

- **WHEN** the expected log directory (e.g. `~/.mohist/logs`) does not exist
- **THEN** the response SHALL report that the source is unavailable
- **AND** SHALL include the expected log location path

#### Scenario: Available-but-empty is not reported as unavailable

- **WHEN** the log source exists but has no new lines since the cursor
- **THEN** the response SHALL NOT report the source as unavailable
- **AND** SHALL report an available source with zero new lines
