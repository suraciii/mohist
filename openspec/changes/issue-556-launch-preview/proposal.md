## Why

The current Agent launch endpoint intentionally rejects runtime fields in the
request body. That protects the saved Agent definition, but it leaves the
operator unable to inspect or select the exact execution configuration before
starting work. A preview that only renders the saved profile would be
misleading once per-execution overrides are introduced.

## What Changes

- Add one typed `execution` object to the manual launch contract. It contains
  only execution-owned override fields: `runtime`, `model`, `variant`, and
  `reasoningEffort`.
- Resolve saved defaults and explicit overrides through one pure Server
  resolver. The resolver returns the resolved definition, the source of each
  field (`saved` or `override`), and an executability conclusion.
- Add a side-effect-free preview operation that uses the same resolver and
  readiness evaluator as a real launch. Preview does not mint a Job, Session,
  Input, Turn, workspace, attachment binding, or Runner claim.
- Include the resolved execution definition in the durable launch plan and
  Job input. The idempotency fingerprint includes the canonical override
  object, so reusing a key with a different execution request is a conflict.
- Preserve exact configuration semantics: an unavailable or incompatible
  requested tuple is reported as `unknown`/`not-executable` according to the
  authoritative evaluator; it is never silently replaced by a saved default,
  another model, or another runtime.

## Non-Goals

- Changing Workflow `mohist/agent` ownership or adding a second scheduler.
- Runtime probing, provider-specific capability discovery, or fallback.
- Allowing execution overrides for instructions, skills, permissions, or
  subagent policy; those remain saved-Agent facts until a separate contract
  defines their ownership.
- Treating a Session transcript or activity record as the source of the
  resolved Job configuration.
