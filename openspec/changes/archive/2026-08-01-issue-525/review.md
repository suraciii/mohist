# Review — issue-525 (从 Web 创建和接管 Slack Connection)

Reviewer stance: reviewer, not fixer. Reviewed the changed files as they are
now (`git diff b6da77a32..HEAD`) against the issue's acceptance criteria and
the plan artifacts under `openspec/changes/issue-525/`.

## Summary

The change delivers the Web as a parity Setup entry to the CLI for Slack
Connection creation and takeover, and a server-derived Bot identity preview.
It is well-scoped to the proposal/design, additive on both sides, needs no
data migration, and all acceptance criteria are met with MSW/fake coverage.

## Acceptance-criteria verification

- **Create from Agent detail + identity preview + Create-in-Slack step** —
  `ConnectionsSection` (`widgets/agent-connections/ui/ConnectionsSection.tsx`)
  is injected into `AgentDetailPage`'s right rail (`AgentDetailPage.tsx:589`),
  Add Slack POSTs create and navigates; the first setup step
  (`ConnectionDiagnosticPage.tsx:205-232`) renders the server-derived
  `botName`/`appDescription` + the external Create-in-Slack link from
  `GET /slack-connections/{id}`. Covered by `ConnectionsSection.test.tsx`,
  `ConnectionsSection.msw.test.tsx`, `ConnectionDiagnosticPage.setup-msw.test.tsx`.
- **Protected credentials, no cleartext / no read-back** — `MaskedCredentialInput`
  (`shared/ui/components/masked-credential-input.tsx`) forces `type="password"`
  at the type boundary (the `type` prop is `Omit`-ed), has no reveal toggle, and
  the form (`credential-form-step.tsx`) clears state on submit and unmount and
  POSTs body-only. Asserted in `masked-credential-input.test.tsx`,
  `client.test.ts` (URL free of token prefixes), and
  `ConnectionDiagnosticPage.actions-msw.test.tsx` (body-only, masked, clear-on-success,
  storage untouched).
- **Resumable across close/refresh/device** — setup renders solely from
  `facts.setupProgress` (server-driven); `refetchInterval: 5000` +
  `refetchOnWindowFocus` on both the diagnostic and detail queries. Resume-on-refetch
  tested in `ConnectionDiagnosticPage.setup-msw.test.tsx`.
- **Transient blockers retain progress + single next step** — service-offline,
  invalid-credentials, and agent-not-Ready states surface one `primaryState` +
  one `nextAction` while keeping the four facts separately readable
  (`ConnectionDiagnosticPage.tsx:273-292`). Covered in both MSW suites.
- **Web/CLI same progress** — both drive the same server `SetupProgress`;
  CLI-completed step reflected on next Web refetch (tested); Web→CLI holds by
  construction (same persisted state).
- **Claim-owner one-time code** — shown once in local mutation state, discarded on
  unmount via `reset()` cleanup (`ConnectionDiagnosticPage.tsx:147-152`); regenerate
  re-POSTs and server supersedes. Covered in `actions-msw.test.tsx`.

## Server-side correctness (T-001)

- `SlackBotIdentityDeriver` (`Agent/Services/SlackBotIdentity.cs`) is a pure
  derivation: valid name used as-is; invalid/blank names sanitized + capped to 80
  chars with a deterministic 8-hex SHA-256 suffix from the Agent id; blank
  description falls back to a non-empty generic. Avatar is deliberately not
  derived (no avatar field on `Agent`). Verified by `SlackBotIdentityTests.cs`
  (unit) and `SlackConnectionIdentityPreviewSpecs.cs` (HTTP: create default,
  stable suffix, explicit-BotName compatibility, GET resumability, immutable
  binding fields unchanged).
- `POST /slack-connections` (`SlackConnectionRoutes.cs:34-82`) defaults omitted
  `BotName` to the derived value and adds `botName`/`appDescription`/
  `slackAppCreationReference` to the 201 response. `GET /{id}` (`:98-121`) exposes
  the same preview so the first step is resumable after refresh/device switch.
- No token is ever returned by create, GET, or diagnostic routes.

## Structural checks

- `npm run typecheck -w packages/web` — clean.
- `npm run check:fsd -w packages/web` — 505 modules, boundaries respected.
- `npm run test:run -w packages/web` — 394 files, 5147 tests pass.
- Server full suite (`dotnet test`) passes (SpecTests 3606, UnitTests 1723,
  ArchTests 51, Workflow.Definition.Tests 175, Cli.Tests 1487).
- FSD layering: widget/page imports flow only to `shared`/`entities`; the masked
  input lives in `shared/ui` and is consumed by `pages/connection-diagnostic`.

## Observations (non-blocking)

- **O1 — pre-existing flake, not introduced by this change:** one full `npm test`
  run failed in `widgets/agent-profile-editor/ui/AgentProfileEditor.test.tsx`
  (1 of 20). That file is not in the issue-525 diff and does not render
  `AgentDetailPage`; it passes in isolation and on the next full-suite run
  (394/394, 5147/5147). The flake predates this change and is unrelated to the
  Slack-Connection surface.
- **O2 — fix-ci commit is test-infrastructure, adjacent to scope:**
  `AgentSessionLaunchRoutesTestSupport.cs` `PollDispatchForSessionAsync` now uses
  `TestWait.ForAsync` (5s/25ms) mirroring the sibling `PollAgentJobDispatchAsync`,
  which was needed for the full `npm test` chain to pass reliably. The change is
  confined to test support, the core assertion (`Assert.Equal(agentJobId,
  dispatch.AgentJobId)`) is unchanged, and the rationale is documented in
  `progress.txt`. It strengthens rather than weakens the test; it does not touch
  any issue-525 product code.
- **O3 — GET fallback when Agent is missing:** `GET /{id}` derives a generic
  preview from `connection.AgentId`/`connection.BotName` when the bound Agent
  record is gone (`SlackConnectionRoutes.cs:110-113`). This is acceptable
  defensive behavior (the diagnostic already degrades gracefully); it does not
  violate any spec requirement.

## Verdict

All six issue acceptance criteria and both capability specs are met. Tests pass
(web + server + runner). No token leakage paths. No must-fix problems found.

<promise>PASS</promise>
