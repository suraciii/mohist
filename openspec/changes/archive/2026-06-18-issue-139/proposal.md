## Why

Workflow task prompts reach the LLM through three inconsistent code paths. `resolvePrompt` already encodes the correct contract — string input passes through verbatim, object input renders as XML, loader input dispatches to a registered `PromptLoader` — but two other paths bypass it: `buildPromptWithMohistContext` wraps any resolved prompt in a markdown `## Mohist Issue Context` / `## Task Prompt` envelope, and `buildFallbackPrompt` assembles an ad-hoc markdown prompt from loose `title`/`description`/`acceptanceCriteria` fields when no `prompt` is declared. The rule that input type determines assembly format is implemented but undocumented and unenforced, so prompt shape depends on which code path ran rather than on a single contract.

## What Changes

- Establish one documented contract for workflow task prompt assembly: **text input → text output (verbatim), object input → XML output via `renderStructuredPrompt`, loader input (`uses` + `with`) → text or object resolved by a registered `PromptLoader`**. XML is not mandatory; it applies only when the prompt input is already an object.
- Route every code path that builds a prompt for LLM consumption through `resolvePrompt`. Remove the independent markdown-wrapping post-processing in `acpAgentAction`.
- Remove `buildPromptWithMohistContext` as a standalone wrapping step; any issue-context injection moves into a `PromptLoader` (or is addressed by the separate context-injection child issue) so it respects the same text/object contract instead of unconditionally emitting markdown. **BREAKING** for callers that imported `buildPromptWithMohistContext`.
- Remove `buildFallbackPrompt` in favor of explicit `prompt` specs in task definitions. Tasks without a declared `prompt` fail with a clear error rather than synthesizing an untracked markdown prompt. **BREAKING** for tasks that relied on implicit `title`/`description`-based prompt synthesis.
- Document and test the "text → text, object → XML" rule as the authoritative contract.
- Existing text-based `.prompt` template bodies and inline YAML string prompts continue to work as plain text, unchanged.

## Capabilities

### New Capabilities

- `workflow-prompt-assembly`: The single contract governing how a workflow task's `prompt` input is assembled for LLM consumption. Covers the text/object/loader dispatch rule, XML rendering of structured prompts, the `PromptLoaderRegistry`, and the requirement that all prompt-building code paths route through `resolvePrompt` with no independent markdown wrapping.

### Modified Capabilities

_None._ No existing spec describes prompt assembly behavior; the `workflow-definition` spec covers prompt *references* (`${{ prompts.* }}`) and the `agent-runtime` / `workflow-engine` specs cover session execution and stage progression, none of which define the assembly format contract being introduced here.

## Impact

- **`packages/runner/src/core/prompt.ts`**: `PromptSpec`, `resolvePrompt`, `renderStructuredPrompt`, and `PromptLoaderRegistry` become the documented single entry point. No behavioral change to the dispatcher itself; it already implements the contract.
- **`packages/runner/src/actions/acp-agent.ts`**: `acpAgentAction` no longer post-wraps via `buildPromptWithMohistContext`; `resolveActionPrompt` no longer falls back to `buildFallbackPrompt`. Both helpers are removed. The exported `buildPromptWithMohistContext` symbol is dropped (breaking).
- **`packages/runner/tests/acp-agent.spec.ts`**: existing `buildPromptWithMohistContext` unit test is removed; tests assert that text specs pass through and object specs render XML end-to-end through `resolvePrompt`.
- **Workflow task definitions**: tasks that previously relied on implicit fallback prompt synthesis must declare an explicit `prompt` (text or object). Built-in `.prompt` template bodies are unaffected.
- **Issue context injection**: the markdown envelope is removed from this layer; whether/how issue context is re-injected is out of scope here and tracked by the separate context-injection child issue. This proposal does not add or remove context-injection requirements.
