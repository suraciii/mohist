## Why

A workflow agent prompt must be self-contained and portable: the same prompt body works regardless of which issue it runs against. Today the only thing preventing issue title/body from being injected into prompt text is that the `buildPromptWithMohistContext` envelope was recently deleted — that "no injection" property is an emergent accident of the current code, not a documented contract. A future change could silently re-introduce code-side issue-context embedding, and there is no spec requirement stating that issue context must come from `mo issue show` CLI instructions embedded in the prompt template itself.

## What Changes

- Establish as a durable requirement that **no code path injects issue title or body into prompt text**. The resolved prompt delivered to the agent SHALL consist solely of the prompt template body (plus declared loader output), never a preamble of issue number/title/body assembled by code.
- Establish that **issue context is obtained by the agent at runtime via `mo issue show` CLI instructions embedded in the prompt template**, making the template the single source of truth for what context the agent needs.
- Require that **every built-in `.prompt` template that needs issue context includes the `mo issue show` instruction**. (All 14 current builtins already do; this turns the convention into a requirement so it cannot drift.)
- Preserve `issue.number` / `project.id` variable interpolation in templates — these construct the CLI command and are explicitly in scope to keep. Only title/body *content* injection by code is prohibited.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `workflow-prompt-assembly`: Add a requirement codifying that the resolved prompt carries no code-injected issue context (title/body); issue context is sourced exclusively via CLI instructions embedded in the prompt template. The existing "no `## Mohist Issue Context` envelope" scenario is the seed, but the new requirement generalizes it from a prohibition on one markdown wrapper to a prohibition on any code-side issue title/body injection, and adds the positive rule that templates own issue-context fetching.

## Impact

- **`openspec/specs/workflow-prompt-assembly/spec.md`**: gains the issue-context-sourcing requirement and scenarios (no code path injects issue title/body; templates fetch via `mo issue show`).
- **`packages/server/src/Mohist.Server/Workflow/Services/Prompts/builtins/*.prompt`**: audited and locked — each issue-aware template already embeds `mo issue show`; no body changes expected, but the convention becomes enforceable.
- **`packages/runner/src/actions/acp-agent.ts`**: `buildPromptLoaderContext` currently threads `issueNumber` into the loader context; design phase will confirm whether any loader consumes it for context injection and close the door if not. (The action itself already routes through `resolvePrompt` with no envelope — no behavioral change expected there.)
- **`packages/runner/tests/acp-agent.spec.ts`**: the existing negative assertion (`sentText` does not contain `## Mohist Issue Context`) is retained and broadened to assert no issue title/body appears in the resolved prompt.
