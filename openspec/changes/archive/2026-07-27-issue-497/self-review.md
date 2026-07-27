# Self-Review — issue-497

Reviewer mode: review-only. No artifacts were modified by this review.

## Verdict

PASS — the plan is internally consistent, fully covers the issue's Behavior
Contract and Done When, the specs are testable and well-formed, and the
single-task split is correct for a tightly-coupled single-subsystem change.

## Artifact review

### proposal.md
- Why accurately states the root cause: all omitted-key recovery calls collapse
  to the shared `"legacy"` constant, so two distinct operations of the same kind
  can be misjudged as duplicates via `GetCompletedRecoveryAsync`.
- What Changes maps 1:1 to the issue's Change Scope and Behavior Contract:
  remove the constant, generate a unique default per call, preserve explicit-key
  semantics, document the decision, add regression coverage.
- Capabilities lists exactly one capability (`agent-session-recovery-idempotency`),
  which matches the single subsystem touched.
- Impact correctly names the precise code site (`RecoveryIdempotencyKey`,
  `BeginSessionCommandAsync`, `GetCompletedRecoveryAsync`) and notes the API
  entry points are pass-through (header → null → grain). Verified against the
  codebase: the grain-internal helper is called only at AgentSessionGrain.cs:280
  and :291; the API-layer `RecoveryIdempotencyKey(HttpContext)` is a different
  method that only forwards the header-or-null. No missed call-sites.

### specs/agent-session-recovery-idempotency/spec.md
- Self-contained, direct target behavior; no ADDED/MODIFIED/REMOVED headers and
  no cross-spec references. Compliant.
- Three requirements, each normative (SHALL/MUST), each with ≥1 scenario.
- All scenarios use exactly four hashtags (`####`) and WHEN/THEN. Compliant.
- Coverage maps to the issue's Behavior Contract:
  - unique default keys / no sentinel → "Default recovery idempotency keys are
    unique per call"
  - no false replay of completed ops → "Distinct operations are not falsely
    replayed"
  - explicit-key contract preserved (incl. explicit `"legacy"` is ordinary) →
    "Explicit-key idempotency contract is preserved"
- Scenarios are observable: operation-id inequality, recovery-effect/event
    recording, reservation key inspection, completed-result replay — all already
    exercisable through the existing grain spec fixture.

### design.md
- D1 precisely specifies the change (`Guid.NewGuid().ToString("N")` on blank
  input, delete the constant) and notes format parity with `OperationId`
  (AgentSessionGrain.cs:337).
- D2/D3 correctly argue that matching/join logic is untouched and that the
  completed-replay path self-corrects (default-key `GetCompletedRecoveryAsync`
  returns null because a fresh GUID cannot match a stored key) — no special-case
  needed.
- D4 identifies the design-doc landing spot (`design/agent-execution.md`,
  operationId idempotency paragraph) for the decision record.
- Alternatives considered (require-key / generate-at-API / `auto-` prefix) are
  listed with rejection rationale.
- Risks explicitly call out the two deliberate behavioral edges (completed-op
  retry now re-executes; in-progress same-command join preserved) rather than
  hiding them.
- Migration/rollback is correctly scoped: no schema/event/API-shape change, no
  data backfill.

### tasks.json
- Valid JSON; single task T-001; dependsOn [] (trivially a DAG).
- Single-task split is correct: the change is one coupled feature module (grain
  edit + one-sentence design-doc decision + regression tests). Splitting into
  "change function / update doc / add tests" would violate the no-over-granular
  rule.
- spec reference points at the capability spec; acceptance criteria are
  verifiable and include test verification (operation-id/event assertions,
  explicit-key regression, design-doc update, green `npm test`).
- mode AFK / type WRITE / passes false — appropriate.

## Cross-check against the issue

| Issue item | Covered by |
|---|---|
| Motivation: shared `"legacy"` → false duplicate judgement | proposal Why; design Context |
| Change Scope: grain generates unique default; record decision | T-001; design D1/D4 |
| Behavior Contract: explicit-key unchanged (same-key idempotent) | spec req 3; T-001 AC #4 |
| Behavior Contract: default each unique (retries non-idempotent) | spec req 1 & 2; T-001 AC #1–#3 |
| Done When: no two distinct ops share a key path | spec req 1 & 2; T-001 AC #1–#3 |
| Done When: decision in design doc | design D4; T-001 AC #5 |
| Done When: server tests green | T-001 AC #6 |
| Non-Goals: explicit-key semantics unchanged | spec req 3; design Non-Goals |
| Non-Goals: recovery command surface unchanged | design Non-Goals; D2 |

## Observations (non-blocking)

1. **In-progress same-command join is preserved for default keys (by design).**
   The issue's Behavior Contract says omitted-key retries are "每次视为新操作".
   The design (D2 + Risks) deliberately keeps the existing in-progress join: a
   default-key retry that arrives *while* an operation is still in progress
   (no outcome yet) appends its unique key to `AdditionalIdempotencyKeys` and
   resolves to the same reservation — same as an explicit different key. This is
   sound: a session can only run one binding replacement at a time, and the join
   satisfies the retry's intent. The literal "每次视为新操作" is honored on the
   path that actually mattered (the completed false-replay bug); the in-progress
   join is unchanged, non-regressing, and symmetric with explicit-key behavior.
   The design documents this transparently rather than hiding it. No change
   needed; flagged only so the implementer honors the documented intent and does
   not "fix" the in-progress join into a second concurrent operation.

2. **Optional spec hardening.** The spec has no scenario asserting the
   in-progress same-command join behavior (default or explicit). Since that
   behavior is unchanged by this issue, omitting it is acceptable. An
   implementer may add a guardrail scenario if desired, but it is not required
   to satisfy the issue.

3. **Design-doc anchor drift.** D4 cites "约 line 234" for the insertion point.
   Line numbers drift; the implementer should locate the paragraph by content
   (the `operationId` 命令幂等键段) rather than by line number. The design
   already names the paragraph, so this is covered.

<promise>PASS</promise>
