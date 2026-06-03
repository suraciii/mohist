# Review Report

## Result: PASS

## Repaired Items

- None.

## Blocking Items

- None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: clarity
  Evidence: `packages/runner/src/actions/openspec-task-prompt.ts:86-94` validates `index` with a compound predicate `typeof index === "number" && Number.isInteger(index) && index >= 0`. When a caller passes a non-integer, negative, or non-numeric-string `index`, this predicate evaluates to `false` and the function falls through to the generic "requires either 'taskId' or 'index'" error. The user did supply an `index`, so the message is misleading and obscures the real cause.
  SuggestedAction: Split the predicate so a malformed `index` raises a distinct, descriptive error (e.g. "loader 'index' must be a non-negative integer"); also add a test for the malformed-index path.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: test coverage
  Evidence: `packages/runner/tests/prompt-renderer.spec.ts` does not exercise `resolvePrompt({ uses: "x", with: null }, ctx)`. The implementation at `prompt.ts:103-110` handles `with: null` by returning `{}`, but a regression that re-introduced the "must be an object" error for `null` would not be caught by the existing `LoaderSpecWithNonObjectWith_FailsWithClearError` test, which only covers the string case.
  SuggestedAction: Add a unit test that passes `with: null` and asserts the loader is invoked with `with: {}`.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: test assertion quality
  Evidence: `packages/runner/tests/acp-agent.spec.ts:362` asserts `expect(sentText).not.toContain("prompts.xxx".replace("xxx", "build"))`. The literal is `${{ prompts.xxx }}`; the assertion checks that the text does not contain `prompts.build`, which is a tautology against the literal and would not detect a regression that replaced `${{ prompts.xxx }}` with the actual build prompt unless the build prompt itself happened to contain `prompts.build`. The strong `expect(sentText).toContain(literal)` on line 361 is sufficient, but the second assertion is misleading.
  SuggestedAction: Strengthen the assertion (e.g. snapshot the full prompt text and check it does not include the resolved build prompt body) or remove the redundant assertion.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: spec compliance (nice-to-have)
  Evidence: Acceptance criterion 6 is covered by `OpenSpecTaskLoader_DoesNotPolluteWithWithDocumentationFields` (`openspec.spec.ts:133-194`) and `TaskDescriptionContainingLiteralTemplateSyntax_IsPreservedAsData` (`openspec-task-prompt.spec.ts:259-295`). The openspec loader test asserts the literal text is not embedded in the *generated `with`*, and the loader test asserts the literal survives the structured renderer output. A stronger end-to-end test could resolve the loader spec from a generated task and assert the literal also survives all the way through the ACP prompt wrapper.
  SuggestedAction: Optional: add an end-to-end test that resolves the generated `with.prompt` spec through `resolvePrompt` and asserts the literal `${{ prompts.xxx }}` text is preserved in the final rendered prompt.
  Status: follow-up (no action required)

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: pre-existing
  Evidence: The `restoreAgentToolNoise` function in `packages/runner/src/actions/acp-agent.ts:283-291` still uses bare `catch {}` blocks for tool-noise cleanup. That is unchanged by this PR and is the documented design ("Tool-noise cleanup must never turn a successful agent run into a failure"). The prompt-resolution change is layered cleanly on top of this existing code.
  Status: pre-existing (out of scope for this issue)

- [ID: item-6]
  Severity: info
  Scope: pre-existing
  Evidence: `Number.isInteger(index)` at `openspec-task-prompt.ts:86` will reject JSON numbers like `1.0` (which are still valid 0-based indices per the issue spec) because the strict `Number.isInteger` check is used. This is intentional and matches common selector semantics, but worth noting for callers that may encode indices as floats in JSON.
  Status: pre-existing (defensive default; not a regression introduced by this PR)

## Spec Compliance Summary

All eight acceptance criteria are satisfied with concrete evidence:

1. **String prompt identity** — `prompt-renderer.spec.ts:14-37` covers empty, multi-line, and template-literal preservation. `acp-agent.spec.ts:340-363` confirms the action still passes strings byte-for-byte through the Mohist context wrapper.

2. **Plain object rendering** — `prompt-renderer.spec.ts:39-219` covers inline, multi-line, list, nested, attribute-only, null-child, primitive, and empty-children cases with exact-string assertions.

3. **Loader-backed resolution** — `prompt-renderer.spec.ts:236-316` and `acp-agent.spec.ts:393-482` cover fake-loader string results, object results, case-insensitive lookup, default-empty `with`, and full loader context propagation (`with`, `variables`, `workDir`, `workId`, `title`, `stage`, `issueNumber`).

4. **OpenSpec task prompt selection** — `openspec-task-prompt.spec.ts` covers `taskId` selection, `id`-first vs `taskId` fallback, `taskId`-overrides-index precedence, index selection, index-only attribute emission, custom items path, custom root tag, relative and absolute file paths, and all required error cases (missing selector, missing file, missing items path, missing selected task, index out of range, malformed JSON, non-object root, missing file path).

5. **Loader no longer sets literal `with.prompt`** — `openspec.spec.ts:13-44` and `openspec.spec.ts:133-194` confirm the generated task is still a `mohist/acp-agent` task whose `with.prompt.uses` is `mohist/openspec-task-prompt`. The issue 49 regression test (`OpenSpecTaskLoader_DoesNotPolluteWithWithDocumentationFields`) is preserved and still passes (147/147).

6. **Opaque task JSON content** — `openspec.spec.ts:189-193` asserts that `${{ prompts.xxx }}`, task title, acceptance criteria, and output strings are not embedded in the generated `with` payload. `openspec-task-prompt.spec.ts:259-295` asserts the loader receives the literal template text as ordinary JSON data.

7. **Default workflow shape** — `mohist-default.workflow.yaml:104-120` now declares `task.uses: mohist/acp-agent` with `with.prompt.uses: mohist/openspec-task-prompt` carrying `file`, `items`, and `base`. `MohistDefaultWorkflowProfileSpecs.cs:90-144` and the round-trip / Plan/Check/Integrate parity tests lock in the new shape. The openspec loader accepts the new caller-supplied loader spec and injects the per-task `taskId` while preserving `file`/`items`/`base` (verified by `openspec.spec.ts:410-453`).

8. **Build and test pass** — `npm run build` in `packages/runner` succeeds; `npm test` reports `Test Files 14 passed (14), Tests 147 passed (147)`. The 19 `MohistDefaultWorkflowProfileSpecs` xUnit tests also pass (`dotnet test --filter MohistDefaultWorkflowProfileSpecs` → `Passed: 19, Failed: 0`).

## Cross-cutting Notes

- **Correctness**: The three prompt-resolution paths (string, plain object, loader-backed) are correctly wired through `resolveActionPrompt` (`acp-agent.ts:331-341`) before `buildPromptWithMohistContext`. String identity is preserved; object rendering is deterministic; loader errors surface as `failure` results with clear messages.
- **Complexity**: `mergeTaskWith` (`openspec.ts:62-78`) cleanly separates default-with merge, per-task-with merge, and prompt injection. The loader registry (`prompt.ts:19-42`) is parallel to `ActionRegistry` and uses case-insensitive keys to avoid surprises.
- **Test Coverage**: New unit tests in `prompt-renderer.spec.ts` (39 tests), `openspec-task-prompt.spec.ts` (23 tests), and the acp-agent spec additions cover all advertised behaviors. The openspec loader spec tests cover string, object, and loader-form caller overrides, per-task `taskId` injection, multiple-task fan-out, and custom `items` path.
- **Security**: All JSON file reads are confined to a caller-supplied `file` path resolved against `workDir`. No path traversal beyond `workDir`; absolute paths and Windows drive paths (`/^[A-Za-z]:[\\/]/`) are allowed as explicit overrides, consistent with existing runner conventions. No secrets or credentials are introduced.
- **Public contracts**: `WorkItem.with` is unchanged; the new `prompt` shapes are still ordinary JSON. No wire-protocol changes. The action registry interface is unchanged.
- **Data safety**: The structured renderer escapes `&` and `"` in attribute values (`prompt.ts:199-201`). Body content is intentionally not escaped because the design is LLM-friendly text, not strict XML — this is documented in `design.md` Decision 3 and the spec.
- **Architectural soundness**: The prompt registry is parallel to `ActionRegistry` (by design) and does not blur lifecycle expectations. Loaders receive a context object and return prompt data only.

<promise>PASS</promise>
