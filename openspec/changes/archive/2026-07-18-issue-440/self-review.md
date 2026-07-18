# Self-Review — issue-440 plan

Reviewed: `proposal.md`, `specs/recovery-failure-context/spec.md`, `design.md`,
`tasks.json` against issue #440 and the current codebase.

## Verdict

The plan is internally consistent, aligned with the design sources the issue
calls out as authoritative (`design/workflow/recovery.md`, `task-dispatch.md`,
`docs/workflow-definition.md`), and the codebase facts the design depends on
check out (call sites, `variables.prompts` population, `formatUnresolvedError`
location, single Status gap note in `recovery.md`). The findings below are
advisory — none blocks building. The implementer should treat them as
clarifications, not required rework.

## Findings

### A. Issue AC #1 literally lists `${{ failure.errorCode }}`; plan narrows to `failure.output.*`

The issue's acceptance criterion #1 reads: "recovery 任务 prompt 中的
`${{ failure.errorCode }}`、`${{ failure.output.* }}` 引用被展开为触发失败的实际值".
The proposal/spec/design support only `failure.output` and its sub-paths
(`failure.output.errorCode`, `failure.output.prNumber`, …) and explicitly
exclude top-level `failure.errorCode` / `failure.message` shortcuts.

The plan's reading is defensible: `task-dispatch.md`'s dispatch-context table
lists only `failure.output`; `docs/workflow-definition.md`'s template table
lists only `failure.output`; no built-in prompt uses a top-level
`${{ failure.errorCode }}`; and the issue body itself frames `errorCode` and
`message` as fields *inside* the structured output. The plan's
`failure.output.errorCode` substitution satisfies the AC in substance.

Flagging only so the integrator confirms the reading; if the issue author
really did want `failure.errorCode` as a top-level alias, the spec/design need
a small addition.

### B. Task scope omits `executor-completion.spec.ts` (3 `tryRecovery` call sites)

The signature change `tryRecovery(work, result)` → `tryRecovery(work, result, variables)`
breaks direct callers beyond the ones the task description names. `grep` finds:

- `packages/runner/tests/executor-recovery.spec.ts` (mentioned in task)
- `packages/runner/tests/executor-completion.spec.ts:482`, `:522`, `:573` (**not mentioned**)

The task's `acceptanceCriteria` does require `npm run typecheck -w packages/runner`
to pass, which forces the implementer to update all call sites — so the work
gets done. But the task description and notes only mention
`executor-recovery.spec.ts`, which understates the test surface. Suggest the
implementer update both spec files (and any other direct caller surfaced by
typecheck) when changing the signature.

### C. Spec Requirement 3 says "fail the recovery task"; what actually fails is the triggering task

`specs/recovery-failure-context/spec.md` Requirement 3 reads: "the runner
SHALL produce a failure outcome … SHALL NOT deliver the recovery task to the
engine … SHALL NOT invoke the recovery action." The recovery task is never
delivered, so the failure outcome must be the *triggering* task's
`WorkItemResult` (status `failed`, diagnostic message). `design.md` Decision 4
states this correctly. A spec reader who skips the design could be confused
about which task's status flips to failed. Minor wording; recommend tightening
to "the triggering task's recovery result SHALL be a failure outcome carrying
a diagnostic that names the unresolvable reference and the recovery task it
appears in."

### D. Design risk mitigation references a Status section the plan removes

`design.md` Risk #1 mitigation says: "Documented as a known trade-off in
`design/workflow/recovery.md` Status section if it ever matters". The plan
(`proposal.md` + `tasks.json` AC #9) removes the `${{ failure.* }}` 展开未实装
bullet from that Status section, and `recovery.md`'s Status section has only
that one bullet — so the section disappears entirely. The mitigation's
"documented in the Status section" reference is therefore stale. Either drop
the mitigation sentence (the snapshot trade-off isn't documented anywhere
after this change) or note explicitly that the trade-off is acknowledged only
in this design document.

### E. Spec over-specification: whole-string `${{ failure.output }}` scenario

Requirement 1's "Whole-string failure.output preserves JSON type" scenario
covers a case no built-in prompt or workflow yaml uses today — every built-in
reference is a sub-path (`failure.output.prNumber`, etc.). Not wrong, but it
expands the implementation surface (type-preserving substitution of an entire
object) for a hypothetical case. Deferring this scenario would not affect the
bug fix. Leave as-is for forward compatibility, or note in the task that this
scenario is forward-looking.

### F. Spec is silent on whether `expect` is in scope

`design.md` Decision 5 walks both `with` and `expect` through the
failure-context pass. `tasks.json` AC implicitly covers this ("walk both
`with` and `expect`" in the description). But
`specs/recovery-failure-context/spec.md` never mentions `expect` — its
scenarios only show `with.prompt` and `with.targetPr`. The spec understates
the design's scope. `design.md` Open Question #2 correctly flags that
`definition.md`'s validation rule does not call out `expect` vs `with`
separately, so the implementer must verify whether `failure.*` is even legal
in `expect` before walking it. Recommend either (a) adding an `expect` scenario
to the spec, or (b) narrowing design Decision 5 / task AC to `with` only if
validation rejects `failure.*` in `expect`.

## Cross-artifact consistency checks (all pass)

- Proposal lists 1 capability (`recovery-failure-context`) → spec file exists
  at the prescribed path with that exact kebab-case name.
- Spec has 4 requirements, each with ≥1 scenario, all scenarios use exactly
  4 hashtags, all use WHEN/THEN, all normative language is SHALL/SHALL NOT.
- Design's 6 decisions map onto the spec's 4 requirements and the task's 9
  acceptance criteria.
- `tasks.json` is valid JSON; single task; no dependencies (trivially a DAG);
  `spec` reference resolves; `mode`/`type`/`priority` are appropriate.
- Codebase facts the design depends on:
  - `tryRecovery` is exported from `packages/runner/src/runtime/recovery.ts:15`
    and called from `executor.ts:166` (variables in scope at line 138). ✓
  - `formatUnresolvedError` lives at `executor.ts:460` (the cited diagnostic
    voice model). ✓
  - `variables.prompts` is populated (already read at `actions/openspec.ts:467`,
    so the executor's existing template substitution against `prompts.*` works
    the same way the design's body pre-render would). ✓
  - `design/workflow/recovery.md` is the only workflow design doc with a Status
    note about `${{ failure.* }}` 展开未实装; `task-dispatch.md` has no Status
    section at all, so the issue AC's "recovery.md / task-dispatch.md 的 Status
    差距标注随实装移除" is satisfied by editing `recovery.md` only. ✓
  - `ValidateTaskExpectations` in `WorkflowYamlSerializer.cs:225` does not
    today reject `failure.*` in `expect` — it only checks for legacy
    `with.expect` shape. So the design's Open Question #2 (whether to walk
    `expect`) is genuinely open. ✓

## Issue acceptance-criteria coverage

| Issue AC | Where covered |
|---|---|
| `${{ failure.* }}` expanded to triggering failure values | Spec Req 1+2; Design D1+D2+D3; Task AC #1, #2 |
| Unresolved `failure.*` → actionable dispatch error | Spec Req 3; Design D4; Task AC #4, #5 |
| Non-recovery rendering unchanged | Spec Req 4; Design Goals; Task AC #7 |
| `recovery.md` / `task-dispatch.md` Status gap removed | Proposal What Changes; Task AC #9 (correctly scoped to `recovery.md` only — `task-dispatch.md` has no Status section) |

All four issue ACs are covered. (Finding A above is about the literal
-vs-substance reading of AC #1, not a coverage gap.)

<promise>PASS</promise>
