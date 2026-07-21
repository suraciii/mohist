## Findings

No blocking findings. The final change keeps workflow and lifecycle commands on the shared decision surface, preserves mobile decision context and complete action access, provides visible product-language unavailable states, and offers concrete transcript navigation whenever a workflow session exists. The issue-detail workflow view is mounted read-only, so its retained evidence component does not expose a duplicate approval command path.

## Verification

- `npm run typecheck -w packages/web`
- `npm run test:run -w packages/web` (367 files, 4968 tests passed)
- `npm run test:browser -w packages/web -- tests/browser/issue-decision-surface.spec.ts` (10 tests passed)

<promise>PASS</promise>
