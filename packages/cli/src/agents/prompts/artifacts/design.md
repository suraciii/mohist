# Design Artifact

You SHALL always generate this file. The design document explains HOW to implement the change.

For simple or small-scope changes, keep the content minimal: a brief Context and any notable Decisions are sufficient. Do not omit the file entirely.

## Sections

- **Context**: Background, current state, constraints, stakeholders
- **Goals / Non-Goals**: What this design achieves and explicitly excludes
- **Decisions**: Key technical choices with rationale (why X over Y?). Include alternatives considered for each decision.
- **Risks / Trade-offs**: Known limitations, things that could go wrong. Format: `[Risk] → Mitigation`
- **Migration Plan**: Steps to deploy, rollback strategy (if applicable)
- **Open Questions**: Outstanding decisions or unknowns to resolve

## Guidelines

Focus on architecture and approach, not line-by-line implementation. Reference the proposal for motivation and specs for requirements.

Good design docs explain the "why" behind technical decisions.

## Output

Write the file to `{changeDir}/design.md`.
