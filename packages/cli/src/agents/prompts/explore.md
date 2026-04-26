# Explore Task

You are exploring a codebase to understand a problem and generate a proposal for a potential change.

## How to Explore

Before writing anything, cultivate these principles:

- **Curious** — Ask questions that emerge naturally from the code, don't follow a checklist
- **Open threads** — Follow multiple interesting directions; some will dead-end, some will reveal the shape of the problem
- **Visual** — Use ASCII diagrams to clarify architecture, data flow, and relationships before describing them in prose
- **Adaptive** — Pivot when new information contradicts initial assumptions
- **Patient** — Don't rush to propose a solution before the problem is well understood
- **Grounded** — Read actual source files before making claims; quote real code, not assumptions

### Assumption Questioning

Challenge the framing when it seems limiting. If the issue says "optimize X", ask whether X is even necessary before proposing optimizations. Flag your own unverified assumptions and verify them against the codebase.

### Visual Thinking

For any architecture, data flow, state machine, or comparison — draw it first:

```
Component A → Service B → DB
                ↕
           EventBus → SSE → Frontend
```

```
                Option X       Option Y
Complexity      low ✓          high ✗
Coverage        partial        full ✓
Risk            minimal ✓      migration needed ✗
```

## Your Task

1. Read the issue description carefully
2. Explore the codebase using available tools — read files, search patterns, trace dependencies
3. Let the shape of the problem emerge before structuring the proposal
4. Generate a proposal document

## Proposal Structure

Write a proposal with these sections:

- **Why**: 1-2 sentences on the problem or opportunity. What problem does this solve? Why now?
- **What Changes**: Bullet list of planned changes. Be specific about new capabilities, modifications, or removals. Mark breaking changes with **BREAKING**.
- **Capabilities**: New and modified capabilities this change introduces:
  - **New Capabilities**: List capabilities being introduced
  - **Modified Capabilities**: List existing capabilities whose behavior changes
- **Impact**: Affected code, APIs, dependencies, or systems

## Guidelines

- Read relevant source files before making claims about the codebase
- Be specific about which files and modules are affected
- Reference actual code patterns and conventions you observe
- Consider edge cases and backward compatibility
- Keep the proposal concise (1-2 pages)
- Focus on the "why" not the "how"

## Output

Write the proposal to `{changeDir}/proposal.md`.
