# Prompt Management

Prompt belongs to Project Space. Not workflow profile.

- Project prompts: Project Space (only configurable layer; project = tenancy boundary).
- Builtin prompts (.prompt files): loader fallback. Project didn't configure this key → use builtin.

Workflow is one consumer (references prompts by key). Standalone agents consume the same pool.

## How it works

```
WorkflowDefinition: action declares prompt key (string ref)
        │
        v   dispatch sends key + variables (no text)
   ┌──────────────────────────────────────┐
   │  Runner — at execution time —         │
   │  fetch prompt by key + project        │
   │  resolve with dispatch variables      │
   │  send to agent                        │
   └──────────────┬───────────────────────┘
                  │ lazy, single-key fetch
                  v
         Project Space prompt store
```

Workflow touches only the key. Never the prompt text.
Prompt resolution is execution-side, not decision-side.

## Resolution

```
Project prompts  →  key → body   (configured, hit = use)
       ↓ miss
Builtin .prompt  →  fallback     (source code, read-only)
```

## Variable expansion

```
PromptTemplateEngine.Render(body, variables)
  regex ${{ path.to.var }}  →  JsonElement tree lookup, max 5 recursive rounds
  returns (rendered, missing, depth)
```

Unresolved variables stay as-is.

## Builtin templates (12 total)

| Stage | Files |
|---|---|
| plan | proposal, specs, design, tasks, self-review |
| build | build |
| check | review, review-self-check, auto-fix, re-verify |
| — | explore, conflict-resolution |
