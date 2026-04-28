## Context

Both `runPipelineReviewStage` and `runPlanStage` end with a self-check/self-review round that writes a report file (`review.md` / `self-review.md`) containing a `## Verdict: PASS/FAIL` line. Currently neither stage parses this verdict — they always proceed to awaiting-user. There is no auto-fix logic in the codebase yet.

The `AcpConnection` interface supports multi-round prompts on a single connection (`prompt()` → `prompt()` → `close()`). Creating a new connection spawns a fresh opencode subprocess, which provides an unbiased context for re-evaluation.

## Goals / Non-Goals

**Goals:**
- Parse verdict from report files after self-check/self-review
- Auto-fix on FAIL with a single attempt, using same ACP connection
- Full re-check on a new ACP connection after auto-fix
- Apply same pattern to both review and plan stages
- Emit round events for UI visibility

**Non-Goals:**
- Multi-round auto-fix loops (explicitly single attempt)
- Escalation to other stages on failure (always await user)
- Classifying fix severity or type
- New prompt template for targeted re-verify (replaced by reusing existing prompts)

## Decisions

### D1: Verdict parsing via regex on report file

Parse `## Verdict: PASS` or `## Verdict: FAIL` from the report file content using a simple regex (`/^## Verdict:\s*(PASS|FAIL)/m`). If the line is missing or doesn't match, treat as FAIL to trigger auto-fix attempt.

**Rationale:** The existing prompt templates (`review-self-check.md`, `self-review.md`) both define this exact output format. Regex is reliable given the structured markdown format, and avoids adding parsing complexity.

**Alternatives considered:**
- Parse from `AcpSessionResult.text` — less reliable because the agent output may include thinking/content before the report
- Structured JSON verdict — would require changing all prompt templates

### D2: Shared `parseVerdict` helper, shared auto-fix flow as private methods

Extract the auto-fix flow into reusable private methods on `WorkflowController`:
- `parseVerdict(reportContent: string): 'PASS' | 'FAIL'` — regex extraction
- `runAutoFixAndRecheck(conn, issue, changeDir, reportContent, stageType, acpOptions)` — handles the fix → close → new conn → re-check sequence

**Rationale:** Both stages need identical logic. Avoids ~80 lines of duplication.

**Alternatives considered:**
- Inline in each stage method — would duplicate the entire auto-fix + re-check block
- Separate service class — over-engineering for a single concern

### D3: Auto-fix prompt inlines the report content

Build the auto-fix prompt by inlining the failing report content rather than creating a new prompt template file. The prompt says "read the report, apply all fix suggestions."

**Rationale:** The auto-fix prompt is generic and short (~10 lines). The actual fix items come from the report content, which varies each time. No need for a template file — a `buildAutoFixPrompt(issue, changeDir, reportContent, reportFileName)` function in `artifact-prompt.ts` is sufficient.

**Alternatives considered:**
- New `auto-fix.md` prompt template file — adds maintenance burden for a static, short prompt
- Reusing existing prompt templates — they serve different purposes (review, self-check)

### D4: Re-check reuses existing prompt builders

After auto-fix, the re-check uses the same prompt builders as the original rounds:
- Review stage: `buildReviewerPrompt` + `buildReviewSelfCheckPrompt` (full R0+R1 sequence)
- Plan stage: `buildSelfReviewPrompt` (full self-review only — artifacts are already generated)

**Rationale:** Full re-check means the same quality bar as the initial review. Using existing prompts avoids drift between initial and re-check behavior.

**Alternatives considered:**
- Targeted re-verify prompt (Issue #65 approach) — misses regressions from auto-fix
- Slightly modified prompts for re-check — unnecessary complexity, same purpose

### D5: Plan stage re-check only re-runs self-review, not artifact generation

For the plan stage, auto-fix applies to generated artifacts (proposal, specs, design, tasks). After auto-fix, re-check only re-runs `buildSelfReviewPrompt` — it does NOT re-run artifact generation rounds.

**Rationale:** The self-review already validates all artifacts. Re-generating artifacts would discard the auto-fix work and waste time. The plan stage artifact rounds are already checkpointed and won't re-run.

### D6: Event emission uses existing `plan_round_start` event pattern

Emit `plan_round_start` events with new `roundType` values: `'auto-fix'`, `'re-review'`, `'re-review-self-check'`, `'re-self-review'`. Same payload structure as existing rounds.

**Rationale:** The UI already renders rounds based on `roundType`. Adding new types requires no UI changes — they display as additional rounds in the timeline.

## Risks / Trade-offs

- **[Auto-fix introduces regressions]** → Full re-check on new ACP connection detects regressions. If re-check FAIL, user decides.
- **[Auto-fix prompt fails]** → Graceful fallback: close connection, return success with `requiresApproval: true`, message notes auto-fix failure. User sees the unmodified FAIL report.
- **[New ACP connection adds latency]** → Single attempt keeps overhead bounded. The new connection is only created when FAIL is detected (not the common PASS path).
- **[Verdict parsing false positive]** → Missing verdict defaults to FAIL, triggering auto-fix. This is safe — worst case, an unnecessary auto-fix round that still PASSes on re-check.

## Migration Plan

No migration needed — this is a new flow added to existing stages. No database schema changes, no config changes.

## Open Questions

None.
