### Requirement: Single generic HTTP request method

`MohistCliApi` SHALL expose a single generic request-execution method that accepts an HTTP method (or a request factory) and a path (plus optional body), and performs the HTTP call. The verb-specific public methods that previously each wrapped a single HTTP verb — `PrintGetAsync` / `PrintPostAsync` / `PrintPutAsync` / `PrintPatchAsync` / `PrintDeleteAsync`, together with the `*WithOutputAsync` variants (`PrintWithOutputAsync` / `PrintPostWithOutputAsync` / `PrintPutWithOutputAsync` / `PrintPatchWithOutputAsync` / `PrintDeleteWithOutputAsync`) — MUST be consolidated so that there is exactly one code path performing the request for a given verb rather than five parallel copies. The public surface used by command call sites MAY be preserved as thin forwarders, provided they all delegate to the single generic implementation.

#### Scenario: All verbs route through one request path

- **WHEN** a GET, POST, PUT, PATCH, or DELETE request is issued by any command
- **THEN** the call flows through the single generic request method
- **AND** no verb-specific send-and-print copy remains that duplicates the request construction and the `HttpRequestException` → server-unavailable handling

#### Scenario: Network failure prints the server-unavailable message

- **WHEN** a request fails with an `HttpRequestException` (server not reachable)
- **THEN** the generic method writes the server-unavailable message to error output and returns exit code 1

### Requirement: Single envelope parsing implementation

Extraction of the response envelope's `success`, `error`, and `code` fields — including the rule that a missing `success` field falls back to `response.IsSuccessStatusCode` — SHALL be consolidated to a single parsing implementation. The duplicated extraction blocks previously scattered across `MohistCliApi` (in `PrintResponseAsync`, `PrintRawResponseAsync`, `ReadPostResultAsync`, `ReadSuccessDataAsync`, `PrintProjectListAsync`, `PrintSystemInfoAsync`, `PrintRunnerShowAsync`, etc.) MUST be replaced by delegating to that single implementation. There MUST NOT remain a second copy of the `node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode` → `error`/`code` extraction pattern.

#### Scenario: Missing success field falls back to HTTP status

- **WHEN** a response body parses to JSON but contains no `success` field
- **THEN** the single envelope parser treats the request as successful if and only if the HTTP status indicates success

### Requirement: Unchanged success, failure, and not-found behavior

Consolidating the HTTP and envelope logic SHALL NOT change the observable output and exit-code behavior of the CLI for any response outcome. The resulting behavior after this change MUST match the prior behavior for success, error, and 404 cases.

#### Scenario: Successful envelope prints data and exits zero

- **WHEN** a response envelope has `success` true (or a missing `success` field on a 2xx status) carrying a `data` node
- **THEN** the CLI prints that `data` payload (or `"OK"` when `data` is absent) and returns exit code 0

#### Scenario: Error envelope prints error and code

- **WHEN** a response envelope has `success` false with `error` and optional `code` fields
- **THEN** the CLI writes `error` to error output, suffixed with ` (code)` when `code` is present
- **AND** returns exit code 1 for a generic failure

#### Scenario: Not-found envelope exits 4

- **WHEN** a response envelope reports a failure and the HTTP status is 404
- **THEN** the CLI returns exit code 4 while still printing the error/code to error output

#### Scenario: Empty body falls back to status code text

- **WHEN** a response body is empty
- **THEN** the CLI prints the HTTP status code and returns exit code 0 for success or 1 for failure
