## Context

Issue #431 closes the Workflow template language to ten roots. Prerequisites #408, #445, and #465 are complete: the Runner is the single execution-boundary renderer, `with` is the sole Action input channel, and raw declarations survive retry/recovery. What remains is the namespace surface itself.

Today the dispatch context conflates two concerns. `IssueVariableBuilder.BuildBuiltInContext` bakes runtime facts (`mohist`, `issue`, `project`, `repository`, `openspecChangeName`, `openspecChangeDir`, `workspace.changeDir`) into a `VariableBundle`. `WorkflowItemTranslator.BuildPayloadAsync` then hoists every key from that bundle to a top-level bare name, so each runtime fact and each user variable key is reachable both as `${{ vars.foo }}` and as a bare `${{ foo }}`. The runner adds its own `runner` root and overrides `workspace` with an absolute `changeDir`. ApprovalFeedback lives at a bare `approvalFeedback` root with a pre-rendered `command` field. Two rendering engines coexist with divergent rules: the runner `renderTemplate` leaves embedded unresolved references as literal text and returns `""` for missing `tasks.*`, while the server `PromptTemplateEngine` (preview) stringifies objects and never fails. Builtin profiles and prompts reference the off-table roots pervasively.

## Goals / Non-Goals

**Goals:**
- Template context exposes exactly ten roots; no off-table bare root is injected or resolvable.
- Effective Variables resolve only via `vars.*`; runtime context is never aliased into `vars` or hoisted to bare names.
- One fail-fast rendering behavior across task input, live-read Prompt, and preview entry points.
- ApprovalFeedback exposed as `work.approvalFeedback.{id,stage,createdAt,summary}` in feedback tasks only; pre-rendered `command` removed.
- `mohist/opencode` and `mohist/pi` are the same inline-agent set for all dispatch validation and rendering.
- Builtin profiles and prompts migrated to the closed namespace with no end-to-end behavior change.

**Non-Goals:**
- Redesigning raw-declaration / attempt-snapshot / retry-expansion boundaries (#465 owns these; this change preserves them).
- Profile metadata or embedded-variables asset boundary (#474).
- Static validation of unknown Definition fields or field types (#432).
- Save-time Action manifest validation (#446).
- New template roots (no `openspec.*`).
- Changing Prompt Project-ownership, live-read, or fallback semantics.
- Migrating the entire `mo` command tree.

## Decisions

### D1: Separate runtime context from variables in the dispatch payload

**Decision:** Stop putting runtime facts into the `VariableBundle`. `BuildBuiltInContext` ceases to inject `mohist`, `project`, `openspecChangeName`, `openspecChangeDir`, and `workspace.changeDir`. `BuildPayloadAsync` builds the runtime roots (`issue`, `repository`, `workspace`) directly as payload entries alongside the `workflow`, `stage`, `work` roots it already emits. The vars-hoisting loop (`BuildPayloadAsync` lines that iterate `effectiveVarsJson.EnumerateObject()` into top-level payload keys) is removed; `vars` is the sole surface for Effective Variables.

`BuildRootVariables` (used to persist the WorkflowRun's initial variable document) also stops hoisting and stops injecting runtime facts. It produces `{ vars, prompts }` only; runtime roots are dispatch-time facts built by `BuildPayloadAsync`, not persisted variables.

**Rationale:** The root cause of every off-table bare root is the conflation of runtime context with variables in one bundle. Separating them at the source eliminates all off-table roots and the hoisting in one move, rather than filtering them downstream where the filtering can be bypassed.

**Alternatives considered:**
- *Filter off-table keys at payload assembly time.* Rejected: the runtime facts would still live inside `vars`, violating "vars contains only merged Variables," and future bundle additions would silently re-leak.
- *Add an allowlist of root names at payload time.* Rejected: same problem — `vars` still carries the contamination; the allowlist is a downstream band-aid.

### D2: Runner variable assembly drops off-table roots and workspace.changeDir

**Decision:** `executor.variables()` stops spreading user variables verbatim into a flat root and stops adding the `runner` root. It builds the template context from the dispatch payload's ten roots, then overrides only `workspace` with the resolved on-disk `{ path, branch }` (no `changeDir`). The `ResolvedWorkspace` type and `resolvedWorkspaceToVariables` drop the `changeDir` field.

`workspace.ts` stops reading `openspecChangeDir` from the variable context. The change-directory path (`openspec/changes/issue-N`) is no longer a workspace fact; profiles and prompts express it literally. If any internal runner path still needs the relative change dir, it computes it from `issue.number` — but no current consumer remains after removing the template variable.

The workspace identity cross-check (`workspaceIdentity`) stops reading `mohist.runId`; the authoritative run identity is `workflow.runId` / the dispatch `workflowRunId`, which already agree.

**Alternatives considered:**
- *Pass changeDir as a non-template dispatch field.* Rejected: no consumer remains; adding a parallel channel reintroduces the conflation this change removes.
- *Keep `runner` root for Actions that read `runner.os`.* Rejected: `runner.*` is not in the documented namespace. If an Action needs host facts, they enter through declared `with` inputs, not an implicit root.

### D3: Template engine fails on all unresolvable references

**Decision:** `renderTemplate` / `renderString` SHALL throw for any `${{ path }}` that does not resolve, whether the expression occupies an entire field value or is embedded in a larger string. The "leave embedded unresolved as literal text" behavior is removed. The executor's pre-dispatch check switches from `wholeStringUnresolvedReferences` to `unresolvedReferences` so embedded unresolvable references are caught before rendering. Literal `${{ }}` text is produced only through the `\${{` escape.

The `resolvePath` special case that returns `""` for missing `tasks.*` paths is removed; missing `tasks.<id>.outputs.*` resolves to `undefined` and fails like any other missing path.

**Rationale:** "Cannot leave unexpanded literal text" is an explicit acceptance criterion. The escape mechanism is the documented way to get literal `${{`. Tolerating embedded unresolved references created a second, undocumented rendering mode that let bad expressions propagate as text.

**Alternatives considered:**
- *Keep embedded-unresolved-as-literal, add a lint warning.* Rejected: the issue requires failure, not a warning; a warning would not prevent the late-failure problem the user voice describes.
- *Make tasks.* missing return null instead of failing.* Rejected: the issue explicitly states missing `tasks.<id>.outputs.*` must fail.

### D4: Converge the preview engine onto the shared behavior vectors

**Decision:** `PromptTemplateEngine.Render` is rewritten to apply the same rules as the runner engine: fail (or report) on unresolvable references instead of leaving literal text; honor `\${{` escape; enforce a deterministic nesting stop; reject embedded object/array values. The `MissingVariables` return is repurposed from "left-as-literal references" to "references the author must supply" — the preview surfaces them as errors, not silent passthrough.

Since prompt bodies are always strings, the type-preservation rule for complete expressions applies to `with`/`expect` rendering (runner) rather than prompt preview. For prompt preview, a complete `${{ path }}` that resolves to a non-string scalar is stringified; a complete reference resolving to object/array is reported as an error (consistent with embedded interpolation rules).

The extract-variables route (`ExtractVariables`) is unchanged in mechanics but its results feed the corrected render path.

**Alternatives considered:**
- *Share one rendering implementation across C# and TypeScript.* Rejected: the languages differ; a shared spec (the behavior vectors) with parallel implementations is the pragmatic boundary, and the vectors are locked by tests.
- *Leave preview lenient; only execution is strict.* Rejected: the issue requires "no entry point retains a separate set of rules."

### D5: Relocate ApprovalFeedback to work.approvalFeedback, drop command

**Decision:** `BuildPayloadAsync` nests feedback facts under `work.approvalFeedback` instead of a bare `approvalFeedback` root. The `work` object is built as a dictionary so `approvalFeedback` is added conditionally (feedback tasks only). The exposed fields are `id`, `stage`, `createdAt`, `summary`. The `command` field and `BuildFeedbackShowCommand` are removed. `WorkflowRunExtensions.BuildFeedbackSummary` stays (it produces `summary`).

**Rationale:** ApprovalFeedback belongs to the current `work`, not a global root. The pre-rendered `command` is an implementation artifact that couples the template layer to CLI command formatting; the consuming prompt can build the invocation from primitives.

**Alternatives considered:**
- *Keep `command`, just move it under `work.approvalFeedback`.* Rejected: the issue explicitly removes the pre-rendered command; keeping it under a different path preserves the coupling.

### D6: Inline agent parity via shared inline-agent set

**Decision:** Both `IsInlineAgentUses` sites (`WorkflowItemTranslator.cs` and `WorkflowYamlSerializer.cs`) match `mohist/opencode` and `mohist/pi`. Both agents already share the same executor rendering path (`renderWithSkippedFields`); the asymmetry is only in server-side validation gating. Extending the set ensures `mohist/pi` receives the same legacy-input rejection (`with.agent`, `with.expect` legacy shape) as `mohist/opencode`.

**Alternatives considered:**
- *Remove inline-agent-specific validation entirely, rely on #446 manifest validation.* Rejected: #446 is not yet delivered; removing the guard now would allow legacy shapes to pass on both agents with no replacement.

### D7: Builtin content migration

**Decision:** Both profile YAMLs and all builtin prompts are rewritten in one coordinated pass:
- `${{ openspecChangeDir }}` → `openspec/changes/issue-${{ issue.number }}` (literal template).
- `${{ project.id }}` → `${{ issue.projectId }}` (with `--project` / `--project-id` flag).
- `${{ approvalFeedback.id }}` → `${{ work.approvalFeedback.id }}`.
- `${{ approvalFeedback.command }}` usage removed; the `apply-feedback` prompt builds the `mo issue feedback show` invocation from `work.approvalFeedback.id`, `issue.number`, and `issue.projectId`.
- Any literal `${{ }}` text in prompts that is documentation/example (not a live reference) is escaped with `\${{`.

Web preview fixtures, editor sample context, and test defaults that carry `openspecChangeDir` or `project.id` are updated to the closed namespace.

**Rationale:** A single coordinated pass avoids a window where profiles reference removed roots. The path pattern `openspec/changes/issue-${{ issue.number }}` resolves to the same on-disk locations as the old `openspecChangeDir`, preserving end-to-end behavior.

### D8: Shared behavior-vector test matrix

**Decision:** A single behavior-vector matrix is encoded as test cases covering: complete value (scalar/object/array/number/boolean/null), embedded value (scalar concatenation, object/array rejection), missing reference (whole and embedded), `tasks.*` (present and missing), escape (`\${{`), nesting and cycle, `failure` context (recovery vs non-recovery), and `work.approvalFeedback` (feedback task vs non-feedback). The same matrix runs against:
- Runner `renderTemplate` unit tests (TypeScript).
- Runner executor spec tests (task input + live-read Prompt).
- Server `PromptTemplateEngine` tests (preview).
- Inline-agent parity test: same task definition with `uses` switched between `mohist/opencode` and `mohist/pi`, asserting identical rendering, prompt, completion, and error.

**Rationale:** The issue identifies implementation drift between entry points as the core risk. A locked matrix prevents regression and makes drift immediately visible.

## Risks / Trade-offs

- **[BREAKING: user profiles reference off-table roots]** -> User-defined profiles using `${{ openspecChangeDir }}`, `${{ project.id }}`, or bare variable names will fail at render time after this change. This is intentional and documented. Mitigation: the Definition validator (separate concern, #432) can later surface these at save time; for now the fail-fast render error identifies the offending expression.
- **[Embedded-unresolved-as-literal removal]** -> Prompts or profiles that relied on unresolved embedded references passing through as text will now fail. Mitigation: builtin content is audited and escaped in the same change; user content gets a clear error message identifying the expression.
- **[Preview strictness may surprise authors]** -> Authors who preview prompts with missing context variables will see errors instead of literal text. Mitigation: the preview reports which references are missing, which is actionable feedback.
- **[Cross-cutting change touching dispatch, runner, content, and two agents]** -> High blast radius. Mitigation: the behavior-vector matrix locks the contract; #465 invariants are explicitly asserted in regression tests; end-to-end Workflow scenarios (Plan artifacts, recovery, approval feedback, retry, GitHub PR) are covered.
- **[Server `failure` root incomplete]** -> The dispatch `failure` root currently carries only `{ output }`; the runner's recovery expansion builds its own `{ output, error }` context. Mitigation: recovery semantics are unchanged because recovery.ts builds its own context; the dispatch `failure` root is completed to `{ output, error: { code, message } }` for consistency but is not the primary expansion site.

## Migration Plan

This is an atomic, coordinated change — there is no gradual rollout because off-table roots and their consumers are removed in the same commit:

1. **Server:** Separate runtime context from variables (`IssueVariableBuilder`, `WorkflowItemTranslator`); relocate feedback; extend inline-agent set; converge preview engine.
2. **Runner:** Drop `runner` root and `workspace.changeDir`; make all unresolvable references fail; remove `tasks.*` empty-string fallback; stop reading off-table roots in workspace resolution.
3. **Content:** Migrate both profiles and all builtin prompts in the same change.
4. **Web:** Update preview fixtures and editor defaults.
5. **Docs:** Sync `docs/workflow-definition.md`, `design/workflow/task-dispatch.md`, `design/workflow/definition.md`, `design/prompt-management.md` — remove resolved gap notes.
6. **Tests:** Add the behavior-vector matrix across runner and server; add inline-agent parity test; assert #465 invariants and end-to-end scenarios.

**Rollback:** Revert the commit. Since the change is atomic across server, runner, and content, partial rollback is not meaningful — the dispatch context, rendering rules, and builtin content must move together.

**#465 invariant preservation:** The raw-declaration dispatch and attempt-snapshot immutability are unchanged. This change only alters *what roots the snapshot contains* (the closed set), not *when or how the snapshot is built or frozen*. Retry/recovery continue to copy raw declarations and re-render against each attempt's snapshot.

## Open Questions

- **Preview API shape:** The current `PromptTemplateEngine.Render` returns `(string, MissingVariables, depth)`. With strict failure, should the preview return a structured error (missing refs, type violations) or throw? Leaning toward returning structured errors so the web UI can surface them per-expression, but the exact response shape is an implementation detail.
- **`failure` dispatch root completeness:** Whether to complete the dispatch `failure` root to include `error` or leave it as-is (since recovery.ts is the primary expansion site) depends on whether any path renders `failure.*` from the dispatch snapshot rather than the recovery-constructed context. To be confirmed during implementation.
