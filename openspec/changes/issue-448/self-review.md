# Self-Review — Issue 448

## Summary

The plan correctly identifies the problem (most built-in Actions lack complete contract pages), proposes a reasonable page structure (group pages by family), and tasks are well-split by file ownership with a clean DAG. However, the spec sets a universal target — every contract page MUST mirror its manifest — and two existing pages (`pi.md`, `opencode.md`) are inconsistent with their current manifests in ways that no task addresses. The acceptance criterion "每个可用内置 Action 都有契约页,输入、输出、错误码与实际声明一致" applies to ALL Actions, including these two.

## Findings

### 1. BLOCKER — `pi.md` error code table and input table diverge from the manifest; no task reconciles them

The `mohist/pi` manifest in `packages/runner/src/actions/built-ins.ts:155-162` declares **6** business error codes: `runtime-unavailable`, `session-workspace-mismatch`, `session-binding-failed`, `runtime-session-missing`, `unavailable-runtime`, `turn-failed`.

The `pi.md` error code table (`docs/actions/pi.md:166-177`) lists **10** codes, of which **5 are not in the manifest**:
- `invalid-input` — a platform-reserved code, not an Action business error
- `session-reporting-failed` — not declared
- `incompatible-runtime` — not declared for `mohist/pi` (it IS declared for `mohist/opencode`)
- `timeout` — a platform-reserved code
- `interrupted` — not declared for `mohist/pi` (it IS declared for `mohist/opencode`)

And **1 manifest-declared code is missing** from `pi.md`:
- `unavailable-runtime`

Additionally, the manifest declares `timeout` as an input (`default: 3600000`) at line 150, but the `pi.md` input table (lines 51-57) omits it, and the prose explicitly states "本 issue 不提供 Action Input 覆盖" (line 131) which directly contradicts the manifest.

This violates:
- Spec requirement "Contract pages mirror manifest declarations exactly" and its scenario "Documentation stays consistent with the manifest"
- Spec requirement "Platform-owned error codes are not documented as Action-owned"
- Issue acceptance criterion "每个可用内置 Action 都有契约页,输入、输出、错误码与实际声明一致"

The proposal says "Preserve `mohist/pi`'s own 实装差距 note" — that refers to the implementation-gap section, not the error code table. The design D1 labels `pi.md` as "already complete", but it is not complete relative to the current manifest.

**Fix**: Either add a task to reconcile `pi.md` with the manifest (add `timeout` input, fix error code table to match the 6 declared codes, remove the "本 issue 不提供 Action Input 覆盖" statement), or explicitly scope `pi.md` and `opencode.md` out of the spec's mirroring requirement with justification.

### 2. BLOCKER — `opencode.md` input table and error code catalog are incomplete; no task reconciles them

The `mohist/opencode` manifest (`built-ins.ts:118-137`) declares:
- Input `timeout` (`default: 3600000`) — **not in the `opencode.md` input table** (lines 56-62)
- **9 business error codes** — `opencode.md` has **no structured error code catalog at all** (errors are discussed only in prose in the "完成与失败" section)

This violates:
- Spec requirement "Each contract page MUST mirror that Action's manifest and SHALL cover three contract facets: the complete input surface... the complete catalog of declared business error codes"
- Spec scenario "A supported Action is documented end-to-end" — "the page SHALL list every business error code declared by that Action's manifest"

The design D1 labels `opencode.md` as "already complete", but it lacks a structured error code catalog and is missing the `timeout` input.

**Fix**: Same as finding 1 — either reconcile or explicitly scope out.

### 3. Minor — T-002 acceptance criteria says "nine Git and GitHub PR Actions" but covers 10

T-002's last acceptance criterion says "Manual manifest cross-check for all nine Git and GitHub PR Actions". The task actually covers 5 Git Actions + 5 GitHub PR Actions (including the net-new `mohist/github-pr-checks` section) = **10** Actions. The count should be corrected to ten.

### 4. Confirmed correct — manifest facts in tasks are accurate

Cross-checked all manifest-derived facts in T-001 and T-002 acceptance criteria against `built-ins.ts`:
- All 7 T-001 Actions' outputs and error codes match the manifest ✓
- All 9 existing T-002 Actions' outputs and error codes match the manifest ✓
- The `mohist/github-pr-checks` manifest facts (inputs `repositoryUrl`/`prNumber`, outputs `pollIntervalMs`/`message`, errors `config-error`/`pr-checks-unavailable`/`pr-checks-failed`/`aborted`) match ✓

### 5. Confirmed correct — gap footnote exists as described

`docs/actions/README.md:37-38` contains "OpenSpec 和 `core/*` 的独立产品契约页仍待补齐" — the removal target is real and accurately described.

### 6. Confirmed correct — DAG is valid

T-001 (priority 1, no deps) and T-002 (priority 1, no deps) are independent and edit different files. T-003 (priority 2, depends on T-001 + T-002) correctly waits for both. The dependency graph is a valid DAG with strictly lower priority numbers for all dependencies.

## Verdict

The plan cannot meet its own spec or the issue's acceptance criteria without reconciling `pi.md` and `opencode.md` with their manifests (or explicitly scoping them out). The spec says "Each contract page MUST mirror that Action's manifest" with no exemptions, and the issue says "每个可用内置 Action" — every Action, no exceptions.

<promise>FAIL</promise>
