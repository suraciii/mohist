# Mohist Go CLI

This module builds the static Go implementation of `mo`. The shared foundation
provides local help, info, credential/session handling, field selection, HTTP
envelopes, stable errors, and the first diagnostic and authentication commands:

```text
mo run why <run-ref>
mo doctor
mo info
mo auth login
mo auth status
mo auth logout
mo auth token <create|list|revoke>
```

The CLI reads `MOHIST_SERVER_URL` (default `http://localhost:3456`) and resolves
credentials in this order: `MOHIST_TOKEN`, a matching session in
`~/.mohist/credentials.json`, and the loopback-only administrator credential
from `MOHIST_ADMIN_TOKEN` or `~/.mohist/admin-token`. The optional
`MOHIST_OPERATOR_ID` defaults to `mohist-cli`.

Build a static binary from the repository root with `npm run build:cli`.
