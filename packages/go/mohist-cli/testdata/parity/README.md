# CLI Parity Fixtures

Each migrated command registers one directory under `testdata/parity/<command>`.
The directory contains the language-independent contract and golden projections:

- `contract.json` names the command, records the invocations, declares the field
  catalog, and describes the expected HTTP request.
- `response.json` is the fake Server envelope returned by the transport.
- `help.stdout`, `human.stdout`, and `selected.stdout` are exact stdout goldens.
- `usage.stderr` is the exact stderr golden for a locally rejected invocation.

The Go harness embeds these files with `go:embed`; tests do not read the working
tree at runtime. The transport is an in-memory `http.RoundTripper`, and every
other `Dependencies` boundary is a test fake. A contract case must verify help,
field discovery, human output, selected JSON, request method/path/headers/body,
exit status, and that usage failures make no request.

The command name and invocation arrays in `contract.json` are the registration
record for the parity case. Add a new directory and registration entry when a
command group is migrated; do not copy parser or HTTP-client logic into the
harness.
