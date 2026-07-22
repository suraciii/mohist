# Self-Review — Issue 451 (Pi Session Commands)

Reviewer verdict: the plan is complete, internally consistent, and maps fully to
the issue. It is ready to build.

## Artifacts reviewed

- `proposal.md` — Why / What Changes / Capabilities (`pi-session-command-routing`,
  `pi-session-channels`) / Impact.
- `specs/pi-session-command-routing/spec.md` (6 requirements / 12 scenarios).
- `specs/pi-session-channels/spec.md` (7 requirements / 14 scenarios).
- `design.md` — Context, Goals/Non-Goals, Decisions D1–D10, Risks, Migration, Open Questions.
- `tasks.json` — T-001…T-004.

## Issue acceptance-criterion coverage

- Follow-up joins the active turn / starts a new turn when idle and returns
  immediately → channels "Follow-up uses the Pi steer channel while busy and the
  prompt channel while idle" + "An idle Follow-up is accepted only after Pi
  reception is confirmed". Covered.
- Idle Compact keeps session identity, transcript stays visible → channels
  "Compact uses Pi native compaction and preserves the session identity". Covered.
- Idle Reset yields a fresh no-context session with prior session in lineage;
  busy Reset/Compact rejected → channels "Reset creates a new empty Pi Session
  and appends lineage without migrating context" + the #407 idle-concurrency
  boundary applies to `pi` once admitted (routing spec: "Server admits … under
  the same … idle-concurrency … rules"). Covered.
- Cancel interrupts the active turn; reports interrupt-unconfirmed (never shows
  as safely stopped) → channels "Cancel … reports stop confirmation honestly to
  the user" (`stopConfirmed`, `interruptUnconfirmed` reaching the API/user).
  Covered.
- Missing bound Pi session fails explicitly with a Reset hint, no silent new
  session → channels "A missing Pi session fails explicitly with a Reset hint".
  Covered.

## Consistency across artifacts

- Proposal capability names match the spec directories exactly
  (`pi-session-command-routing`, `pi-session-channels`).
- The cancel honesty signal (`interruptUnconfirmed`) is consistent end-to-end:
  proposal Impact, design D6 + Migration Plan, channels spec, and tasks T-002
  (`stopConfirmed`) / T-004 (reply field + server mirror) all agree.
- The per-session prompt mutex (design D10) is reflected in the channels
  serialization requirement and tasks T-002 (introduces it + retrofits `runTurn`)
  / T-003 (`compact` acquires it); T-003 depends on T-002.
- Structural validity: both specs have every requirement followed by ≥1 scenario,
  all scenarios at exactly 4 hashtags, no delta headers; `tasks.json` is valid
  JSON with a valid DAG (`T-001`→`T-002`→`T-003`→`T-004`), every `dependsOn`
  pointing to a strictly-lower-priority task, and a test-related criterion per task.

## Prior fix cycle confirmed resolved

The earlier review's blocking findings are closed and remain closed:
- F1 (cancel interrupt-unconfirmed could not reach the user) — resolved via the
  first-class `stopConfirmed`/`interruptUnconfirmed` field mirrored to the HTTP
  response.
- F2 (idle Follow-up double-prompt race) — resolved via D10's per-session prompt
  mutex.
- Non-blocking N1/N2/N3 (latency framing, "identically" wording, recovery
  reconciler) — resolved.

## Open questions (do not block the build)

- SDK `preflight` and `compact()` surface on the pinned version — gated behind
  T-001's smoke (design D9); escalate if absent rather than substitute.
- Idle user Follow-up turn budget — deferred (leaning on user Cancel).
- OpenCode compact/reset product ownership — explicitly a non-goal here; tracked
  as a separate concern (design Open Question Q4).

## Conclusion

No blocking problems. The plan is ready to build.

<promise>PASS</promise>
