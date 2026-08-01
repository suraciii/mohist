# Self-Review (round 2) — issue-525 (从 Web 创建和接管 Slack Connection)

Reviewer stance: reviewer, not fixer. This round re-reviews the plan after the
round-1 findings were fixed.

## Summary

All three round-1 findings are resolved and consistent across artifacts:

- **F1 (was must-fix) — avatar derivation:** `specs/web-connection-setup/spec.md`
  requirement "Creating a Connection presents a Bot identity preview derived from
  the bound Agent" now derives name + description only and explicitly states the
  avatar SHALL NOT be derived (applied manually in Slack App settings); its scenario
  matches. `proposal.md` line 8 and `design.md` Decision C / Risk are aligned. The
  unsatisfiable requirement is gone.
- **O1 — claim-owner traceability:** `tasks.json` T-004 notes now cross-reference
  web-connection-setup "The owner claim step generates a one-time code claimed through
  the Bot".
- **O2 — preview/navigate UX:** the connection page is now the authoritative,
  resumable surface for the identity preview + Create-in-Slack; `design.md` Decisions
  A/B/C/G updated, and `tasks.json` T-001 exposes the preview on `GET /slack-connections/{id}`,
  T-003 creates+navigates, T-004 renders the first setup step.

Structural checks pass:
- All six issue acceptance criteria (+ the claim-owner product-shape item) are covered
  by a spec requirement with at least one scenario.
- 22 scenarios total; every scenario uses exactly 4 hashtags; no 3-hashtag scenarios.
- Every spec requirement has ≥1 scenario.
- All four task `spec` anchors resolve to real requirement headings.
- `tasks.json` is valid JSON; the dependency graph is an acyclic DAG with strictly
  decreasing priorities (T-001 p1 → T-003 p3 → T-004 p4; T-002 p2 → T-004 p4) and
  every task carries test-verification acceptance criteria.
- All requirements are owned by a task; web-credential-input "persisted only by the
  Server's encrypted secret store" is met by the existing AES-GCM store + T-004's
  submission to `/configure` (no new server task needed).
- Cited code references verified: `App.tsx:74`, `AgentDetailPage.tsx:42-45/584`,
  `SlackConnectionRoutes.cs:33-64`, `SlackConnectionApiSpecs.cs`, `Agent.cs` (no avatar).

## Observations (non-blocking — cosmetic/tidiness only)

These do not block building; recorded for an optional tidy-up pass:

- **N1 — stale parenthetical in design Decision C:** it still says the avatar handling
  "reconciles the spec wording 'identity … derived from the bound Agent'". The spec is
  now corrected, so the reconciliation is done; the sentence reads as historical. Could
  be simplified to a plain statement (name+description derived, avatar in Slack).
- **N2 — T-003 title overstates scope:** "Web: Connections widget on Agent detail with
  Add Slack and identity preview" — but identity-preview rendering moved to T-004 in the
  O2 fix. The description and notes are unambiguous (preview rendering is T-004); only
  the title still implies T-003 renders it. Could drop "and identity preview" from the title.
- **N3 — migration plan omits the GET exposure:** `design.md` Migration Plan step 1 says
  "add preview to response" (create) but does not mention exposing the preview on
  `GET /slack-connections/{id}`, which Decision C and T-001 now include. A one-clause
  addition would keep the migration plan in sync.

None of these affect correctness or buildability — a builder following the task
descriptions, acceptance criteria, and design decisions will implement the right thing.

## Verdict

Round-1 must-fix resolved; no new must-fix problems; remaining items are cosmetic nits.
The plan is ready to build.

<promise>PASS</promise>
