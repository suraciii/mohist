# Self Review Report

## Result: PASS

## Repaired Items

None. The plan artifacts are internally consistent and grounded in the current codebase (verified `runCommand` at `system/process.ts:62`, `killProcess` at `:136`, `timeoutSignal` at `actions/registry.ts:190`, `looksLikeRetrySafe` at `github-pr-classify.ts:~58`). No safe, unambiguous repair was warranted.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: The `network-command-timeout` spec Requirement 1 and task T-002 include the `gh --version` / `gh auth status` precheck (`actions/github-pr-runtime.ts`) in the network timeout policy. `gh --version` is a local subcommand (prints version, no network), so by the issue's own "local commands don't hang on the network" principle it sits slightly outside the stated policy boundary. The proposal's `network-command-timeout` Capability is phrased as "`gh pr`/`gh` API calls", which is narrower than the spec/task enumeration.
  SuggestedAction: Either (a) narrow the spec/task to drop `gh --version` from the network set (keep `gh auth status`, which can validate against the network), or (b) explicitly widen the Capability phrasing to "gh invocations including the precheck pair" so proposal/spec/task agree. Harmless in practice — the timeout never fires on a fast local command — so this is cosmetic alignment, not a correctness risk. Defer to the implementer.
  Status: follow-up

## Review Notes

- **Alignment** — All six issue acceptance criteria trace cleanly to proposal "What Changes", both spec files, and task acceptance criteria. Both issue parts (per-command timeout + subprocess-tree cleanup) are addressed; the design correctly notes the detached-spawn/group-kill fix also closes today's leak on work-level abort (D3).
- **Completeness** — `command-timeout` spec covers the primitive (optional `timeoutMs`, tree termination, structured-but-distinguishable result, orthogonality with `with.timeout`, fake-timer testability). `network-command-timeout` spec covers the policy (single default, step/summary/duration, retry-safe classification, local exclusion, seam-based testing). Edge cases present: non-positive timeout, parent-abort precedence, byte-identical normal path, POSIX-only group kill with ESRCH/EINVAL fallback.
- **Consistency** — Capabilities ↔ specs ↔ tasks map 1:1 (`command-timeout` ↔ T-001, `network-command-timeout` ↔ T-002). Naming consistent (`NETWORK_COMMAND_TIMEOUT_MS`, `timeoutMs`, `status: "timeout"`). "Modified Capabilities: None" is correct — moving the internal `timeoutSignal` helper is an implementation refactor, not a capability change; `core/script` and `mohist/acp-agent` behavior is preserved.
- **Feasibility** — Two tasks, each a complete feature slice (primitive, then policy). No fine-grained sub-tasks ("定义接口"/"提取类"/"注册DI"-style titles absent); no standalone test tasks (tests folded into each task's acceptance criteria). The known circular-dependency risk (`registry.ts` → `process.ts`, and `process.ts` needing `timeoutSignal`) is explicitly called out in T-001 notes with an extraction plan to a shared low-level module.
- **Dependency completeness** — T-001 `dependsOn: []`, T-002 `dependsOn: ["T-001"]`; priorities (1 < 2) and IDs are consistent; no cycles.

<promise>PASS</promise>
