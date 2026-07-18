# Review Report

## Result: FAIL

The candidate delivers the bulk of issue-409 (T-001 through T-004 plus T-006): the `OpenCodeRuntime` deep module, readiness gate, catalog via v2 list APIs, native `client.session.prompt()` turn execution, executor-owned deadline, provider-error failure policy, and retirement of the Workflow-path ACP bridge. Typecheck, build, and the full runner suite (1112/1112) pass on the post-repair snapshot.

However, the candidate defers T-005 (Workflow-source session commands over the native SDK) without relaxing the issue acceptance criteria. Three issue-level ACs are not met as a result. A second blocking issue is an incorrect carrier for `options.variant` (encoded as a system-prompt text marker instead of the SDK's native `body.variant` field), and the third is a flaky runner-host test that violates the project's no-flaky rule.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead-code-removal
  Evidence: `packages/runner/src/runtime/opencode/catalog.ts` exported `mergeCatalogDiagnostics`, which was never used anywhere in `packages/runner/src` or `packages/runner/tests`, and was not re-exported from `runtime/opencode/index.ts`. It was dead code left over from an earlier catalog-diagnostics design sketch.
  Verification: `grep -rn "mergeCatalogDiagnostics" packages/runner/src packages/runner/tests` now returns no matches; `npm run typecheck -w packages/runner`, `npm run typecheck:tests -w packages/runner`, `npm run check:test-boundaries -w packages/runner`, `npm run test:run -w packages/runner` (1112/1112), and `npm run build -w packages/runner` all pass.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: packages/runner/src/runtime/host.ts (handleSessionCommand L181-183, resolveFollowupTarget L195-216, restoreSessionTarget/resumeSessionTarget L218-275); packages/runner/src/runtime/acp-session-command.ts; openspec/changes/issue-409/tasks.json T-005
  Evidence: Issue AC #11 requires Workflow-source session commands to run natively: "Follow-up 使用 `client.session.promptAsync()`, Compact 使用 `client.session.summarize()`, 中断使用 `client.session.abort()`, Reset 使用 `client.session.create()` 建立新的空 Runtime Session". Issue AC #16 requires "Workflow 配置与 Workflow 来源的 Session 状态、用户可见诊断不再暴露 ACP Action 或 ACP Session 身份". T-005 in `tasks.json` (`id: "T-005"`, `dependsOn: ["T-004"]`) is the task that delivers this; it has `passes: false` and there is no commit message referencing T-005 in `git log master..HEAD` (only T-001, T-002, T-003, T-004, T-006). In the candidate snapshot:
    - `RunnerHost.handleSessionCommand` still calls `executeAcpSessionCommand(request, this.sharedAcpConnection?.connection ?? null)` for every source, including Workflow.
    - `RunnerHost.resolveFollowupTarget` still resolves through `sharedAcpConnection` and `RunnerHost.resumeSessionTarget` still calls `sharedConnection.connection.resumeSession(...)` (ACP) for every source, including Workflow (`binding.runtime.toLowerCase() !== "opencode"` is the only filter, and Workflow-source bindings already carry `runtime: "opencode"`).
    - Workflow-source session-command and follow-up requests therefore continue to surface ACP identity (`acpSessionId` in `actions/acp/session-strategies.ts:50,114,119,198,200,204,...`) and to call ACP `resumeSession` instead of `client.session.promptAsync/summarize/abort/create`.
    The deferral is acknowledged in `openspec/changes/issue-409/progress.txt` (no `## T-005 findings` section, only T-001..T-004 + T-006) and in the gap notes added to `design/runtimes/opencode.md`, `design/agent-execution.md`, and `docs/actions/opencode.md` ("Workflow 来源的 Session 命令...当前仍由历史 ACP 路径承担；issue-409 内的 T-005 落地后..."). But the issue's AC list was not relaxed — T-005 is still in `tasks.json` with `passes: false`, and the issue body explicitly scopes ACP cleanup to "Workflow 来源" including session commands. The candidate did not implement T-005 nor re-scope it to #410 in the issue.
  [disallowed:reason] Repair would require implementing T-005 (Routing Workflow-source Compact/Reset/Follow-up/Cancel through `OpenCodeRuntime`), which is a product-behavior change spanning the host, the runtime, and the session-command handler — explicitly disallowed by the repair policy.
  SuggestedAction: Either implement T-005 in this issue (route Workflow-source commands through `OpenCodeRuntime` and surface the spec's `notStarted`/`unavailable` taxonomy plus operation dedup journal without exposing `acpSessionId`), or split T-005 into a follow-up issue and explicitly relax issue-409 ACs #11 and #16 (and the AC #15 wording about Workflow-source Session state) to exclude session commands. The current state — ACs unchanged, implementation partial — is not approvable.
  Verification: `git log master..HEAD --oneline | grep -i 'T-005'` returns no matches; `grep -n "executeAcpSessionCommand\|resumeSession" packages/runner/src/runtime/host.ts` still shows ACP calls on the Workflow path; design/doc gap notes explicitly defer T-005.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/runner/src/runtime/opencode/turn.ts (SYSTEM_VARIANT_PREFIX L82, buildPromptBody L396-408)
  Evidence: Issue AC #3 requires "`options.variant` 保持独立" and issue AC #2 lists `options.variant` as a first-class option. The spec `opencode-model-catalog` requires "The runtime SHALL construct the SDK model DTO from this parsed provider and model ID inside the module boundary" and the turn-execution spec requires the runtime to "carry the specified model and variant on that prompt". The candidate carries variant by injecting `body.system = "[mohist variant:<name>]"` on every prompt (`turn.ts:82`, `turn.ts:401-407`). This is incorrect: the pinned `@opencode-ai/sdk@1.18.3` types declare a native top-level `body.variant?: string` on both the prompt and promptAsync operations (`node_modules/@opencode-ai/sdk/dist/v2/gen/types.gen.d.ts:8372` `SessionPromptData.body.variant`, `:8674` `SessionPromptAsyncData.body.variant`) and a `body.model.variant?: string` on session create (`:8090` `SessionCreateData.body.model.variant`). OpenCode will not actually select the configured model variant from a free-text system marker; the marker only pollutes the assistant's system context. `openspec/changes/issue-409/progress.txt` asserts "SDK's `session.prompt({body.model})` shape is `{providerID, modelID}` — no `variant` field" — this assertion is wrong, and the smoke verification (`sdk-smoke-verification.json`) did not exercise variant application so the drift was not caught.
  [disallowed:reason] Changing the variant carrier from `body.system` to `body.variant` is a product-behavior change affecting model selection — disallowed by the repair policy.
  SuggestedAction: Drop `SYSTEM_VARIANT_PREFIX` and pass `variant` as a top-level field on the prompt body (`body.variant = variant`) and on the create body (`body.model = { id, providerID, variant }`). Re-run the smoke verification with a variant-bearing model to confirm OpenCode actually applies it. Update the unit tests in `tests/opencode-runtime-turn.spec.ts` accordingly.
  Verification: `grep -n "SYSTEM_VARIANT_PREFIX\|mohist variant:" packages/runner/src/runtime/opencode/turn.ts` returns the wrong carrier; `grep -n "variant" node_modules/@opencode-ai/sdk/dist/v2/gen/types.gen.d.ts` confirms `body.variant` is supported on prompt/promptAsync and `model.variant` on create.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: packages/runner/tests/runner-host-opencode-runtime.spec.ts:465-529 (transitional AgentJob gating)
  Evidence: The "transitional AgentJob gating" test is flaky. In a loop of 30 runs of `npm run test:run -w packages/runner`, run #14 failed with `AssertionError: expected null not to be null` at line 504. Root cause: the test reaches into private state via `(host as unknown as { openCodeRuntime: ... }).openCodeRuntime` and polls it up to 50 times inside `await vi.advanceTimersByTimeAsync(POLL_INTERVAL_MS)`. `RunnerHost.run()` first awaits `this.workspaceRegistry.load()` and `this.sessionCommandJournal.load()` — both real disk-IO operations against `runnerRoot: "/tmp/mohist-runner-host-opencode-runtime"` (the `WorkspaceRegistry` and `SessionCommandJournal` modules are not mocked in this spec, only `WorkspaceManager` is). Fake-timer advancement does flush microtasks, but the libuv threadpool callbacks driving the actual fs reads are not bounded by the virtual clock, so on slow machine contention `this.openCodeRuntime` is not yet assigned when the 50-iteration polling loop ends. This violates the project testing principle in `AGENTS.md`: "不得 flaky：不得依赖顺序、时间戳种子、未恢复的 stub；不得用 `it.skip` 掩盖 flaky".
  [disallowed:reason] Repair requires either mocking `WorkspaceRegistry`/`SessionCommandJournal`, awaiting a deterministic signal from the host (e.g., a callback hook), or restructuring the test to not race real disk IO under fake timers — a non-trivial test refactor that may hide other behavior, disallowed by the repair policy.
  SuggestedAction: Either (a) `vi.mock` the `WorkspaceRegistry` and `SessionCommandJournal` modules the way `WorkspaceManager` is mocked, so the host's `run()` chain makes no real fs calls under fake timers, or (b) expose a deterministic `await host.waitForReadiness()` (or similar) test hook and have the test await it before reading private state. After the fix, run `npm run test:run -w packages/runner` 50+ times in CI-equivalent conditions to confirm zero flakes.
  Verification: `for i in $(seq 1 30); do npm run test:run -w packages/runner 2>&1 | grep -E "Tests "; done` — observed one `1 failed | 1111 passed` line in 30 runs.
  Status: open

## Non-blocking Items

- [ID: item-5]
  Severity: warning
  Scope: packages/runner/src/runtime/opencode/turn.ts:410-424 (extractFinalAssistantText)
  Evidence: The function collects `text` from any part whose `text` field is a string, without filtering by `part.type`. The pinned SDK declares both `TextPart` (`type: "text"`, `text: string`) and `ReasoningPart` (`type: "reasoning"`, `text: string`) — see `node_modules/@opencode-ai/sdk/dist/v2/gen/types.gen.d.ts:248-262` and `:278-291`. Models that emit reasoning parts (Anthropic extended thinking, OpenAI reasoning models) will mix chain-of-thought into the "final assistant text" used by the Workflow task executor to evaluate `path: _output` expect markers, in violation of the turn-execution spec requirement "supply the turn's final assistant text".
  SuggestedAction: Filter `parts.filter((p) => p?.type === "text")` before extracting text, or use the `info` role/`AssistantMessage` shape to guide extraction.
  Verification: Add a fake-runtime test where `promptResult.data.parts` contains both a `{type:"reasoning", text:"..."}` and a `{type:"text", text:"..."}` part, and assert `finalAssistantText` excludes the reasoning text.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: packages/runner/src/runtime/opencode/turn.ts:184-226 (resolvePhysicalSession)
  Evidence: The restart-and-reconnect spec requires "reconcile state by reading `client.session.status()` together with the relevant `client.session.get()` / `client.session.messages()` snapshot". The candidate only calls `client.session.get()` to verify the persisted binding exists (`turn.ts:196-199`); it never calls `client.session.status()` or `client.session.messages()` for snapshot reconciliation after reconnect. The "Restart reconciliation" test (`tests/opencode-runtime-turn.spec.ts:584-622`) asserts that `sessionCreate` is not called again but does not assert any `session.status`/`session.messages` snapshot read — the mocks for those calls are set up but never exercised on the reconnect path.
  SuggestedAction: After server-exit rebuild, before issuing the next prompt on a persisted binding, read `session.status` + `session.messages` to reconcile any in-flight assistant message state (especially an assistant message that completed during the disconnect window). Add a fake-runtime test asserting the snapshot calls fire and that their output is folded into the turn facts.
  Verification: `grep -n "sessionStatus\|sessionMessages" packages/runner/src/runtime/opencode/turn.ts` returns nothing on the resolution path; the test at `tests/opencode-runtime-turn.spec.ts:608` clears those mocks but never asserts them.
  Status: open

- [ID: item-7]
  Severity: minor
  Scope: packages/runner/src/actions/acp-agent.ts:19-50
  Evidence: The handler comment says "AgentJob-only ACP bridge. The Workflow source no longer routes through here". But the only source-gated line is `if (context.ownerKind !== "agent-job" || !context.agentSessionId) { await emitSessionEvent(...) }` — that gate controls only the `session.closed` emission, not execution. The prompt resolution, `runAcpGenericAgentSession(context, prompt)` call, and result framing all run unconditionally. A custom Workflow profile that still declares `uses: mohist/acp-agent` (explicitly permitted by self-review.md item-4 as a transitional state) will dispatch through ACP on the Workflow path, contradicting the spec scenario "The Workflow source has no ACP fallback" (`specs/opencode-session-operations/spec.md`) and the issue AC #15 implication that `mohist/acp-agent` is "solely" for the AgentJob path.
  SuggestedAction: Either add a dispatch-time guard at the top of `acpAgentAction` that returns an actionable failure when `context.ownerKind !== "agent-job"`, or relax the spec/comment wording to acknowledge that legacy Workflow profiles can still dispatch here until #410. The current state — comment claims Workflow exclusion, code does not enforce it — is misleading.
  Verification: `grep -n "ownerKind" packages/runner/src/actions/acp-agent.ts` shows the gate is only around `emitSessionEvent`.
  Status: open

- [ID: item-8]
  Severity: minor
  Scope: packages/runner/src/runtime/opencode/runtime.ts:276-287
  Evidence: When `eventSubscriptionFactory` is missing, the readiness diagnostic uses `code: "server-spawn-failed"` — the same code used when the server factory throws. The two failure stages are distinct (server lifecycle vs event-subscription wiring), and the runtime emits different `code` values for the other stages (`health-failed`, `catalog-load-failed`, `server-exit`). The misleading code makes operational triage harder.
  SuggestedAction: Use a distinct code such as `event-subscription-missing` (or `event-subscription-failed`) so the host's readiness log line identifies the actual stage.
  Verification: `grep -n "server-spawn-failed" packages/runner/src/runtime/opencode/runtime.ts` shows two distinct call sites using the same code.
  Status: open

- [ID: item-9]
  Severity: minor
  Scope: packages/runner/src/runtime/opencode/errors.ts:142
  Evidence: `errorKindFor` maps any error whose message matches `/not[ _-]?found/i` to `missing-session`. A provider returning "model not found" or "tool not found" would be classified as a missing AgentSession binding and surface a misleading Reset hint. The 404 branch above it is correctly scoped, but the regex branch over-generalizes.
  SuggestedAction: Drop the regex fallback (rely on `status === 404` for the missing-session classification), or tighten the pattern to session-specific phrasing (e.g., `/session.*not[ _-]?found/i`).
  Verification: Add a unit test in `tests/opencode-runtime.spec.ts` that asserts `errorKindFor({ message: "model not found" })` is `turn-failed`, not `missing-session`.
  Status: open

## Follow-up Items

- [ID: item-10]
  Severity: follow-up
  Scope: packages/runner/src/runtime/opencode/turn.ts:250-298 (executePrompt abort race)
  Evidence: The provider-error failure policy races the awaited prompt against an abort promise that is resolved from the event listener. If a non-recoverable `session.status` retry event arrives and is immediately followed by the awaited prompt resolving successfully (e.g., the retry event was for an earlier attempt and OpenCode actually completed), the current code still aborts and returns `turn-failed`. This is acceptable per the design (the abort is authoritative once a non-recoverable verdict is reached) but is worth noting as a known trade-off.
  SuggestedAction: Document the "first non-recoverable verdict wins, even if the prompt later succeeds" semantics in the design doc so future contributors understand the abort is intentional.
  Status: follow-up

- [ID: item-11]
  Severity: follow-up
  Scope: packages/runner/src/runtime/opencode/runtime.ts:369-373 (exitWatcher promise)
  Evidence: `watchExit` constructs `this.state.exitWatcher = new Promise<void>(() => { ... })` — a promise that never resolves and has no reject path. The comment says it is "intentionally long-lived so external code can await it on shutdown", but no external code currently awaits `exitWatcher` (no public getter exposes it, and `shutdown()` does not resolve it).
  SuggestedAction: Either expose and resolve `exitWatcher` from `shutdown()` so it serves its stated purpose, or drop the field and rely on the listener + rebuild path alone.
  Status: follow-up

- [ID: item-12]
  Severity: follow-up
  Scope: openspec/changes/issue-409/tasks.json (T-005 dependency)
  Evidence: T-006 ("Verify the Workflow source is ACP-free and align design/doc gap notes") has `"dependsOn": ["T-004", "T-005"]`, but T-006 was committed while T-005 remained `passes: false`. The workflow runner permitted T-006 to run with an unmet dependency in the candidate snapshot. This is a workflow-traceability risk: a future re-run that enforces the dependsOn DAG strictly would block T-006.
  SuggestedAction: Either implement T-005 (see item-2) or formally split T-005 out of this issue and update `tasks.json` so T-006's `dependsOn` no longer references it.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-13]
  Severity: info
  Scope: packages/runner/src/runtime/opencode/runtime.ts:376-390 (scheduleRebuild unref'd setTimeout)
  Evidence: The runtime's rebuild path uses `setTimeout(resolve, delay)` with `timer.unref?.()`. Under fake timers (as in the spec suite) this works because `vi.useFakeTimers` intercepts the call. Under real Node.js in production, `unref` means the rebuild will not keep the event loop alive on a graceful shutdown — if the runner process is tearing down, the rebuild may never fire. This is intentional (we do not want a rebuilding runtime to block process exit) but is worth noting.
  SuggestedAction: Optional — confirm the runner's shutdown path explicitly calls `runtime.shutdown()` (it does in `RunnerHost.shutdownSharedConnection`) so the rebuild race does not leak.
  Status: pre-existing

- [ID: item-14]
  Severity: info
  Scope: design/runtimes/opencode.md, design/agent-execution.md, docs/actions/opencode.md
  Evidence: The body of all three docs is unchanged (per the spec-first convention "body is spec, gap note is footnote"); only the gap/实装差距 footnotes were rewritten. The gap notes now correctly track that Workflow-source turns are native but Workflow-source session commands are deferred to T-005 and the AgentJob path is deferred to #410. No technical language was introduced into `docs/actions/opencode.md`. This is a consistent application of AGENTS.md.
  SuggestedAction: None.
  Status: pre-existing

- [ID: item-15]
  Severity: info
  Scope: packages/runner/src/runtime/opencode/server-process.ts (createSpawnedOpencodeServer)
  Evidence: The default server factory calls `createOpencodeServer({ signal })` without an explicit `directory`/`cwd`. The OpenCode server inherits the runner process's `process.cwd()`, while the client is constructed with `directory` from `OpenCodeRuntimeDeps.directory` (set to `process.cwd()` in `RunnerHost.initializeSharedConnection`). Per-call directories are passed on each SDK operation via `query.directory`/`body.directory`. This matches the smoke verification note but assumes OpenCode's server resolves relative paths against the runner cwd — worth flagging for future drift monitoring.
  SuggestedAction: Optional — add a comment in `server-process.ts` documenting that the server-side directory is the runner cwd and per-call directories are passed explicitly, so a future SDK change that drops `query.directory` does not silently break the model.
  Status: pre-existing

<promise>FAIL</promise>
