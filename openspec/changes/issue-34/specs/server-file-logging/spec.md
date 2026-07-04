### Requirement: Server daemon writes structured JSON file logs

The server daemon SHALL run a file-logging provider that writes structured JSON log records to `~/.mohist/logs/*.log` — the path `SystemPaths.Logs` already advertises. Each written record SHALL be a single JSON object containing at least `level`, `time`, and `message` fields, so the file is the real source of runtime logs instead of always being empty/absent.

#### Scenario: A log record is written to the advertised location

- **WHEN** the server daemon runs and emits a log entry
- **THEN** a JSON log record SHALL be appended to a file under `~/.mohist/logs/`
- **AND** that record SHALL contain `level`, `time`, and `message` fields

#### Scenario: Records are appended across the daemon lifetime

- **WHEN** the server daemon continues running and emits further log entries
- **THEN** subsequent records SHALL be appended to the log file
- **AND** the file SHALL remain readable by `/api/logs/tail` as it grows

### Requirement: Log directory is created by the provider

The file-logging provider SHALL create the `~/.mohist/logs` directory if it does not exist, so the advertised `SystemPaths.Logs` path becomes truthful and `/api/logs/tail` has a real source to read.

#### Scenario: Directory is created on startup when missing

- **WHEN** the server starts and `~/.mohist/logs` does not exist
- **THEN** the file-logging provider SHALL create the directory
- **AND** the directory SHALL exist before the first log record is written

### Requirement: Written file-log format matches the tail contract

The JSON record format the file logger writes SHALL be directly consumable by `/api/logs/tail` and renderable by the Web without re-parsing. In other words, the same per-line element type SHALL be used end to end: a record the file logger writes SHALL become a tail element whose fields are populated from that record.

#### Scenario: Written records are tail-able without transformation

- **WHEN** the server writes a JSON log record and a client subsequently calls `/api/logs/tail`
- **THEN** the tail SHALL return that record as the agreed structured element type
- **AND** the element's `level`/`time`/`message` SHALL be populated from the written record without a separate parsing step on the client
