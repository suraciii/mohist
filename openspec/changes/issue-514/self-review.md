# Self-Review (round 4) — Issue 514 (Slack Agent Connection, Owner-only DM)

Reviewer re-checked the plan artifacts. No fix has been applied since round 3 (the last
commit is the round-2 self-review record; `design.md` D5 is unchanged).

## Status of prior findings

- **P1-P5 (round 1):** resolved.
- **P6 (round 2):** resolved (claim-code classification precedence).
- **P7 (round 3):** **still open — not yet fixed.**

`design.md` D5 (line 85) still specifies `/ingress` as "writes the provider inbox (D6) and
acks the adapter only after that durable write, **then** classifies server-side" — i.e. a
provider inbox row is written for every inbound event before classification, including
non-owner DMs and non-claim DMs that are about to be rejected. This still contradicts:

- `slack-connection-setup/spec.md` requirement 7 (line 103, 111): a non-owner DM "MUST NOT
  create ... provider inbox entry."
- `slack-dm-dispatch/spec.md` requirement 1, scenario "A non-claim DM before Setup is
  complete is rejected" (line 15): the rejected DM creates "no ... inbox entry."
- The authoritative `design/slack-agent-connection.md:60-64`: the Server decides
  ignore/reject/persist-to-inbox, then acks — only accepted events enter the inbox.

**Fix (unchanged from round 3):** reorder `/ingress` to classify first and persist a
provider inbox entry **only for accepted events** (rejected/ignored events ack Slack with no
inbox write; rejections are idempotently re-derived on redelivery); add a T-006 acceptance
criterion that rejected DMs create no provider inbox entry. No spec change is needed — the
specs are already correct.

## Other checks this round

- `tasks.json` still validates (10 tasks, DAG, priorities strictly increasing along every
  dependency, every task has criteria, all `passes: false`).
- All five specs retain correct `####` scenario structure (no 3-hashtag defects); every
  requirement has at least one scenario.
- No new spec/design/task contradiction found beyond P7.

## Verdict

P7 remains the single open must-fix: the ingress inbox-write ordering still violates two
normative rejection requirements and the authoritative design spec. It must be fixed before
the plan is built.

<promise>FAIL</promise>
