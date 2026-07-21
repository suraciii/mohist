# Self-Review — Issue 451 (Pi Session Commands), re-review after fixes

Reviewer verdict: the prior blocking findings (F1, F2) and non-blocking
observations (N1–N3) are resolved consistently across `design.md`, both specs,
and `tasks.json`. No new blocking defects were introduced. The plan is ready to
build.

## Verification of prior findings

### F1 — Cancel honesty now reaches the user (resolved)

- `design.md` D6: cancel result facts carry a first-class `stopConfirmed`;
  `CancelAgentSessionReply` gains optional `interruptUnconfirmed`, set by the
  handler when `stopConfirmed === false` and mirrored by the Server into the
  HTTP response. OpenCode cancel is unchanged.
- `specs/pi-session-channels/spec.md` "Cancel … reports stop confirmation
  honestly to the user": requires `stopConfirmed` and that an interrupt-
  unconfirmed indication *reaches the API/user*; scenarios assert
  `stopConfirmed: true`/`false` and the reply carrying `interruptUnconfirmed`.
- `tasks.json`: T-002 returns `stopConfirmed`; T-004 criterion "Cancel honesty
  reaches the user … interruptUnconfirmed=true … OpenCode unchanged".

The original defect — diagnostics dropped at `cancel-handler.ts:85-88` because
`CancelAgentSessionReply` was `{ state }` only — is closed by the first-class
field end-to-end.

### F2 — Per-session prompt serialization (resolved)

- `design.md` D10: `PiRuntime` owns a per-physical-session async mutex covering
  `runTurn`, idle Follow-up, and `compact`; `steer`/`reset` excluded; `runTurn`
  retrofit is safe (redundant with the external coordinator). D5's idle path
  acquires it and no longer claims `isStreaming` alone suffices.
- `specs/pi-session-channels/spec.md` "Prompt-initiating operations are
  serialized per physical Pi session" (+2 scenarios: concurrent follow-up vs
  workflow turn; two concurrent follow-ups).
- `tasks.json`: T-002 introduces the mutex + retrofits `runTurn`; T-003's
  `compact` acquires it; T-003 now depends on T-002 (graph
  T-001→T-002→T-003→T-004).

The double-prompt race (queued workflow turn / concurrent follow-up) is closed.

### N1 / N2 / N3 (resolved)

- N1: D4 and Risks corrected — an unhandled `SessionCommand` resolves to null
  promptly; the 15 s timeout is a backstop. (Context line 9 still names the
  dispatcher's 15 s timeout, which is an accurate description of the config,
  not the corrected latency claim.)
- N2: routing spec softened to "unchanged in outcome" with an explicit
  semantic-equivalence sentence for OpenCode compact/reset.
- N3: new routing-spec requirement "Pi compact/reset recovery preserves one
  operation across interrupted delivery" + T-004 journal-dedup /
  no-blind-reexecute criterion.

## Structural validation

- Specs well-formed: channels 7 requirements / 14 scenarios; routing 6 / 12;
  every requirement is followed by ≥1 scenario; no 3-hash scenarios.
- Task graph is a valid DAG; every `dependsOn` points to a strictly-lower-
  priority task (`T-001`→`T-002`→`T-003`→`T-004`).
- Issue acceptance criteria all map to spec scenarios (follow-up busy/idle +
  immediate return; compact identity-preserving; reset new-session + lineage +
  busy-rejection; cancel interrupt-unconfirmed; missing-session Reset hint).
- Non-goals respected: no new command types; OpenCode behavior preserved
  (regression-guarded); the OpenCode compact/reset pre-existing gap is left
  untouched and flagged as a separate concern (Open Question Q4).

## Non-blocking observations (not required for build readiness)

- **O1 (wording nuance, D6).** D6 says "OpenCode cancel always returns
  `stopConfirmed: true`," but the existing `RuntimeCancelResult` has no such
  field — the handler treats its absence as confirmed. Behavior is correct;
  the sentence could say "OpenCode results carry no `stopConfirmed`, treated as
  confirmed." Not a defect.
- **O2 (implementation latitude, D7/D10).** The ordering of mutex-acquire vs
  the `isStreaming`→conflict guard in compact is unspecified. Either order is
  safe because the Server's idle gate already rejects compact while a workflow
  turn is active; the mutex then serializes compact against any in-flight
  user Follow-up turn. No corruption either way.
- **O3 (task output list, T-004).** The `interruptUnconfirmed` mirror touches
  `session-target.ts` (`CancelAgentSessionReply`) and the server cancel route,
  which T-004's description covers but its `output` file list does not name
  explicitly. The list is illustrative; intent is clear.

These are within implementation latitude and do not affect build readiness.

## Conclusion

All reported problems are fixed; the artifacts are internally consistent and
map completely to the issue. The plan is ready to build.

<promise>PASS</promise>
