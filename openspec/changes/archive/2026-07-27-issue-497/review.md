# Review

No merge-blocking findings.

The grain now generates a fresh `Guid` (`N` format) for every omitted or
whitespace recovery idempotency key, while preserving caller-supplied keys.
The change removes the shared `"legacy"` default, retains completed replay and
in-progress join behavior for explicit keys, documents the intended default-key
semantics, and covers the completed-operation regression at grain and API
boundaries.

Verification: `npm test` passed, including 3,270 Server spec tests.

<promise>PASS</promise>
