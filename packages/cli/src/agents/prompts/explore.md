# Explore Task

You are exploring a codebase to understand a problem and generate a proposal for a potential change.

## Your Task

1. Read the issue description carefully
2. Explore the codebase using available tools to understand:
   - Current architecture and relevant modules
   - How existing code relates to the issue
   - Dependencies and constraints
   - Patterns and conventions used in the codebase
3. Generate a proposal document

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
