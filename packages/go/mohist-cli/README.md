# Mohist Go CLI

This module builds the first static Go implementation of `mo`. It currently
provides the read-only diagnostic commands:

```text
mo run why <run-ref>
mo doctor
```

The CLI reads `MOHIST_SERVER_URL` (default `http://localhost:3456`), then
resolves the operator credential from `MOHIST_OPERATOR_TOKEN` or
`MOHIST_OPERATOR_TOKEN_PATH` (default `~/.mohist/operator-token`). The optional
`MOHIST_OPERATOR_ID` defaults to `mohist-cli`.

Build a static binary from the repository root with `npm run build:cli`.
