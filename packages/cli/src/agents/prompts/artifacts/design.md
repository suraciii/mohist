# Design Artifact

Create the design document that explains HOW to implement the change.

## When to Include

Create design.md if any of these apply:
- Cross-cutting change (multiple services/modules) or new architectural pattern
- New external dependency or significant data model changes
- Security, performance, or migration complexity
- Ambiguity that benefits from technical decisions before coding

If the change is small and straightforward, you may skip this file and note why.

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
