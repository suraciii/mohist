# Self-Review: issue-617

## Review Mode

This is the first review: `self-review.md` did not exist before this review. I
re-read issue 617, including its body and three acceptance criteria, before
reviewing `proposal.md`, `design.md`, `tasks.json`, and the Slack specification.
I also checked the current embedded Skill, Server catalog/tests, Slack ingress
specs, and Runner context/envelope paths.

## Verdict

FAIL.

## Must-Fix Findings

### MF-001 — Coverage and verifiability of the docs-parity acceptance criterion

Issue 617 requires the injected Skill text to correspond one-to-one with the
`docs/slack.md` collaboration-rules section. That section has six rules at
`docs/slack.md:438-462`. The plan's asset criteria in `tasks.json:11-15` and
its contract-test list in `design.md:140-144` cover the direct-question,
silence, recovery, reply-anchor, self-contained-result, and delegated-mention
rules, but they do not require all documented content. In particular, they do
not require:

- the documented restriction that someone is mentioned only when they need to
  act or notice the result (`docs/slack.md:452-454`); or
- the documented proportion rule that fine-grained progress belongs in the Web
  session timeline (`docs/slack.md:455-456`).

There is also no task-level parity check or explicit complete checklist against
the source section. An implementation can therefore satisfy every current
Task 1 language assertion while still leaving the managed Skill out of sync
with the documented rules, violating the issue's third acceptance criterion.
The plan must require coverage of every rule in that section and make the
correspondence verifiable, either through an explicit rule-by-rule contract
test or a deterministic parity check.

## Dimension Verdicts

- **Issue goals and acceptance criteria:** Checked first; MF-001 violates the
  third acceptance criterion.
- **Coverage:** FAIL because the docs-parity criterion is not fully specified
  or verifiable. The direct-question and silent-recovery goals are otherwise
  represented in the proposal, design, specification, and Task 1.
- **Correctness:** Checked, no additional must-fix issue. The planned ordered
  Skill rules correctly give direct questions precedence over ordinary and
  recovery silence, while retaining Agent-owned replies and the Server anchor.
- **Current-code consistency:** Checked, no issue. The named embedded asset,
  catalog, root-launch context construction, follow-up context construction,
  Runner validation, and envelope injection points all exist and match the
  plan's boundaries.
- **Task breakdown and verifiability:** FAIL only for MF-001. The T-001 to T-002
  dependency order is coherent, and the existing Server/Runner test gates are
  appropriate for the described implementation.

## Observations

- The migration verification in `design.md:196-199` confirms that a recovered
  question produces an Agent-authored reply, but it does not explicitly say to
  inspect that the first recovered turn contains no restart/recovery narration.
  The normative Skill and spec requirements cover this; the manual check would
  be clearer if it recorded that absence directly.
- The plan intentionally leaves question classification, recovery markers,
  fallback replies, delivery, and Session recovery mechanics unchanged. That is
  consistent with the issue's stated scope and non-goals; this review does not
  treat the lack of a deterministic LLM behavior test as a must-fix for this
  Skill-text-only change.
- The Server test requirement says to assert the exact Skill identity and
  version (`tasks.json:15`); the existing test currently compares each value to
  the catalog constant itself. The implementation task should use literal
  expected values such as `mohist-slack-collaboration` and `1.1.0` so that those
  assertions are meaningful.

<promise>FAIL</promise>
